using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.Core.Providers;
using UltimateImapMcp.Core.Repositories;
using UltimateImapMcp.Dashboard;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Llm;
using UltimateImapMcp.Llm.Repositories;
using UltimateImapMcp.McpServer.Tools;
using UltimateImapMcp.Queue;

namespace UltimateImapMcp.McpServer;

/// <summary>
/// Background service that hosts the MCP server over HTTP+SSE transport
/// on the configured http_port. Always started to serve the tool API for
/// multi-instance proxy. MCP HTTP transport is only added when configured.
/// If the port is already in use, enters standby mode and takes over when it frees up.
/// </summary>
public sealed class HttpMcpTransportHost : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IServiceProvider _rootServices;
    private readonly ILogger<HttpMcpTransportHost> _logger;
    private WebApplication? _webApp;
    private int _consecutiveFailures;
    private bool _hasStartedOnce;
    private string? _proxyBaseUrl;

    public HttpMcpTransportHost(AppConfig config, IServiceProvider rootServices,
        ILogger<HttpMcpTransportHost> logger)
    {
        _config = config;
        _rootServices = rootServices;
        _logger = logger;
    }

    /// <summary>Set the proxy URL for failover recovery.</summary>
    public void SetProxyBaseUrl(string url) => _proxyBaseUrl = url;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _config.Server.HttpPort;

        // Wait in standby if another instance owns the port
        await PortUtils.WaitForPortWithBackoffAsync(port, _logger, "MCP HTTP", stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartHttpServer(port, stoppingToken).ConfigureAwait(false);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures == 1)
                    _logger.LogWarning(ex, "MCP HTTP transport failed on port {Port} — entering standby mode", port);
                else
                    _logger.LogDebug(ex, "MCP HTTP transport retry #{Count} for port {Port}", _consecutiveFailures, port);
                _webApp = null;

                // Lost primary status — re-enable proxy (dispose old one first)
                if (_proxyBaseUrl is not null)
                {
                    (McpJsonDefaults.ToolProxy as IDisposable)?.Dispose();
                    McpJsonDefaults.ToolProxy = new UltimateImapMcp.Core.Coordination.ProxyToolExecutor(_proxyBaseUrl);
                }

                // Wait for port to become available again before retrying
                await PortUtils.WaitForPortWithBackoffAsync(port, _logger, "MCP HTTP", stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task StartHttpServer(int port, CancellationToken stoppingToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        // Share singleton services from the root host
        builder.Services.AddSingleton(_config);
        builder.Services.AddSingleton(_rootServices.GetRequiredService<AppDatabase>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<CredentialEncryptor>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<AccountRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<FolderRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<MessageRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<AttachmentRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<SyncLogRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<ImapSyncService>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<SyncManager>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<CacheConfig>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<QueueRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<QueueManager>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<QueueConfig>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<LlmConfig>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<LlmUsageRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<LlmAnalysisRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<BudgetTracker>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<IEmailAnalyzer>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<MetricsRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<LogsRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<IOAuthAccessTokenProvider>());

        // Local labels fallback
        builder.Services.AddSingleton(_rootServices.GetRequiredService<LabelsDatabase>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<LocalLabelRepository>());

        // Additional services needed for tool API execution
        builder.Services.AddSingleton(_rootServices.GetRequiredService<IEmailBackendFactory>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<ProviderProfileRegistry>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<AccountsStore>());

        // Register MCP server with HTTP transport only when configured
        var addMcpTransport = _config.Server.Transport is "http" or "both";
        if (addMcpTransport)
        {
            builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "ultimate-imap-mcp", Version = "0.1.0" };
            })
            .WithHttpTransport()
            .WithToolsFromAssembly();
        }

        var app = builder.Build();
        _webApp = app;

        // Tool API endpoint — always available for multi-instance proxy
        app.MapToolsApi();

        // Health check endpoint
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

        if (addMcpTransport)
        {
            app.MapMcp();
        }

        if (!_hasStartedOnce)
        {
            _logger.LogInformation("MCP HTTP transport starting on http://0.0.0.0:{Port}", port);
            _hasStartedOnce = true;
        }
        else
        {
            _logger.LogDebug("MCP HTTP transport reconnecting on http://0.0.0.0:{Port}", port);
        }

        // We're now serving — clear proxy (we are primary)
        (McpJsonDefaults.ToolProxy as IDisposable)?.Dispose();
        McpJsonDefaults.ToolProxy = null;

        await app.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_webApp is not null)
        {
            await _webApp.StopAsync(cancellationToken).ConfigureAwait(false);
            await _webApp.DisposeAsync().ConfigureAwait(false);
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
