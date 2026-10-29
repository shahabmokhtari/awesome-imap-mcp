using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AwesomeImapMcp.Core;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.Core.Email;
using AwesomeImapMcp.Core.Encryption;
using AwesomeImapMcp.Core.OAuth;
using AwesomeImapMcp.Core.Providers;
using AwesomeImapMcp.Core.Repositories;
using AwesomeImapMcp.Dashboard;
using AwesomeImapMcp.ImapClient;
using AwesomeImapMcp.ImapClient.Repositories;
using AwesomeImapMcp.Llm;
using AwesomeImapMcp.Llm.Repositories;
using AwesomeImapMcp.McpServer.Tools;
using AwesomeImapMcp.Queue;

namespace AwesomeImapMcp.McpServer;

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
                    McpJsonDefaults.ToolProxy = new AwesomeImapMcp.Core.Coordination.ProxyToolExecutor(_proxyBaseUrl);
                }

                // Wait for port to become available again before retrying
                await PortUtils.WaitForPortWithBackoffAsync(port, _logger, "MCP HTTP", stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task StartHttpServer(int port, CancellationToken stoppingToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o =>
            ListenAddress.Configure(o, _config.Server.BindAddress, port, _logger, "MCP HTTP transport",
                "It has no authentication: anyone who can reach this port can read, send and delete mail."));

        ListenAddress.AddHostFiltering(builder.Services, _config.Server.BindAddress);

        ForwardRootServices(builder.Services, _rootServices, _config);

        // Register MCP server with HTTP transport only when configured
        var addMcpTransport = _config.Server.Transport is "http" or "both";
        if (addMcpTransport)
        {
            builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "awesome-imap-mcp", Version = "0.1.0" };
            })
            .WithHttpTransport()
            .WithToolsFromAssembly();
        }

        var app = builder.Build();
        _webApp = app;
        ListenAddress.UseHostFiltering(app, _config.Server.BindAddress);

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
            _logger.LogInformation("MCP HTTP transport starting on {Address}:{Port}", _config.Server.BindAddress, port);
            _hasStartedOnce = true;
        }
        else
        {
            _logger.LogDebug("MCP HTTP transport reconnecting on {Address}:{Port}", _config.Server.BindAddress, port);
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

    /// <summary>
    /// Shares the root host's singletons with this transport's container, which
    /// builds the MCP tool classes. Every tool constructor dependency must be
    /// forwarded here (enforced by a test).
    /// </summary>
    public static void ForwardRootServices(IServiceCollection services, IServiceProvider root, AppConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton(root.GetRequiredService<AppDatabase>());
        services.AddSingleton(root.GetRequiredService<CredentialEncryptor>());
        services.AddSingleton(root.GetRequiredService<AccountRepository>());
        services.AddSingleton(root.GetRequiredService<FolderRepository>());
        services.AddSingleton(root.GetRequiredService<MessageRepository>());
        services.AddSingleton(root.GetRequiredService<AttachmentRepository>());
        services.AddSingleton(root.GetRequiredService<SyncLogRepository>());
        services.AddSingleton(root.GetRequiredService<ImapSyncService>());
        services.AddSingleton(root.GetRequiredService<SyncManager>());
        services.AddSingleton(root.GetRequiredService<CacheConfig>());
        services.AddSingleton(root.GetRequiredService<QueueRepository>());
        services.AddSingleton(root.GetRequiredService<QueueManager>());
        services.AddSingleton(root.GetRequiredService<QueueConfig>());
        services.AddSingleton(root.GetRequiredService<LlmConfig>());
        services.AddSingleton(root.GetRequiredService<LlmUsageRepository>());
        services.AddSingleton(root.GetRequiredService<LlmAnalysisRepository>());
        services.AddSingleton(root.GetRequiredService<BudgetTracker>());
        services.AddSingleton(root.GetRequiredService<IEmailAnalyzer>());
        services.AddSingleton(root.GetRequiredService<MetricsRepository>());
        services.AddSingleton(root.GetRequiredService<LogsRepository>());
        services.AddSingleton(root.GetRequiredService<IOAuthAccessTokenProvider>());

        // Local labels fallback
        services.AddSingleton(root.GetRequiredService<LabelsDatabase>());
        services.AddSingleton(root.GetRequiredService<LocalLabelRepository>());

        // Additional services needed for tool API execution
        services.AddSingleton(root.GetRequiredService<IEmailBackendFactory>());
        services.AddSingleton(root.GetRequiredService<ProviderProfileRegistry>());
        services.AddSingleton(root.GetRequiredService<AccountsStore>());
    }

}
