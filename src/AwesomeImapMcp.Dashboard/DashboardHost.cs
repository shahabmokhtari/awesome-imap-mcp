using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AwesomeImapMcp.Core;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.OAuth;
using AwesomeImapMcp.Core.Repositories;
using AwesomeImapMcp.Llm;
using AwesomeImapMcp.Llm.Repositories;

namespace AwesomeImapMcp.Dashboard;

/// <summary>
/// Background service that starts a Kestrel web server for the dashboard,
/// serving REST API, SignalR hub, and static files (React SPA).
/// Only activated when dashboard_enabled is true in config.
/// If the port is already in use, enters standby mode and takes over when it frees up.
/// </summary>
public sealed class DashboardHost : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IServiceProvider _rootServices;
    private readonly ILogger<DashboardHost> _logger;
    private WebApplication? _webApp;
    private int _consecutiveFailures;

    /// <summary>True when this instance is actively serving the dashboard (owns the port).</summary>
    public bool IsActivelyServing => _webApp is not null;

    public DashboardHost(AppConfig config, IServiceProvider rootServices,
        ILogger<DashboardHost> logger)
    {
        _config = config;
        _rootServices = rootServices;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Server.DashboardEnabled)
        {
            _logger.LogDebug("Dashboard is disabled — not starting web server");
            return;
        }

        var port = _config.Server.DashboardPort;
        var isInitialLaunch = !PortUtils.IsPortInUse(port);

        // Wait for the port to become free before starting; returns immediately if already available
        await PortUtils.WaitForPortWithBackoffAsync(port, _logger, "Dashboard", stoppingToken).ConfigureAwait(false);

        // Clear sessions once at startup so clients must re-authenticate after a server restart.
        // Placed here rather than in StartDashboard, which is re-entered on crash recovery
        // and standby takeover — sessions should not be wiped on each retry.
        var authRepo = new DashboardAuthRepository(
            _rootServices.GetRequiredService<AwesomeImapMcp.Core.Database.AppDatabase>(),
            _rootServices.GetRequiredService<AwesomeImapMcp.Core.Configuration.AppConfig>());
        authRepo.CleanExpiredSessions();
        authRepo.ClearAllSessions();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartDashboard(port, isInitialLaunch, stoppingToken).ConfigureAwait(false);
                _consecutiveFailures = 0;
                isInitialLaunch = false; // subsequent starts after crashes are not initial
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures == 1)
                    _logger.LogWarning(ex, "Dashboard failed on port {Port} — entering standby mode", port);
                else
                    _logger.LogDebug(ex, "Dashboard retry #{Count} for port {Port}", _consecutiveFailures, port);
                _webApp = null;

                // Wait for port to become available again before retrying
                await PortUtils.WaitForPortWithBackoffAsync(port, _logger, "Dashboard", stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task StartDashboard(int port, bool openBrowser, CancellationToken stoppingToken)
    {
        // Use AppContext.BaseDirectory as content root so wwwroot is found
        // next to the application binaries, not in the current working directory
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel(o =>
            ListenAddress.Configure(o, _config.Server.BindAddress, port, _logger, "Dashboard",
                string.IsNullOrEmpty(_config.Server.DashboardPinHash)
                    ? "No dashboard PIN is set, so it is open to anyone who can reach it."
                    : "It is protected by the dashboard PIN, sent over plain HTTP."));

        if (!Directory.Exists(builder.Environment.WebRootPath))
        {
            _logger.LogWarning(
                "Dashboard wwwroot not found at {Path}. Static files will not be served. " +
                "Run 'npm run build' in dashboard/client/ to build the SPA.",
                builder.Environment.WebRootPath);
        }

        // Register dashboard-specific services
        builder.Services.ConfigureHttpJsonOptions(opts =>
        {
            opts.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
        builder.Services.AddHttpClient();
        builder.Services.AddSignalR();

        ForwardRootServices(builder.Services, _rootServices, _config);

        // Dashboard-own services
        builder.Services.AddSingleton<DashboardAuthRepository>();
        builder.Services.AddHostedService<DashboardHubRelay>();

        ListenAddress.AddHostFiltering(builder.Services, _config.Server.BindAddress);
        var app = builder.Build();
        _webApp = app;

        // Middleware pipeline
        ListenAddress.UseHostFiltering(app, _config.Server.BindAddress);
        app.UsePinAuth();

        // Disable browser caching for all responses
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers.Pragma = "no-cache";
            ctx.Response.Headers.Expires = "0";
            await next().ConfigureAwait(false);
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Map API routes
        app.MapAuthApi();
        app.MapAccountsApi();
        app.MapSyncApi();
        app.MapQueueApi();
        app.MapSettingsApi();
        app.MapMessagesApi();
        app.MapOAuthApi();
        app.MapMetricsApi();
        app.MapLogsApi();
        app.MapLlmApi();
        app.MapServerApi();
        app.MapToolsApi();
        app.MapCacheApi();
        app.MapAnalyticsApi();

        // Map SignalR hub
        app.MapHub<DashboardHub>("/hub");

        // SPA fallback — serve index.html for non-API routes
        app.MapFallbackToFile("index.html");

        _logger.LogInformation("Dashboard starting on http://localhost:{Port}", port);

        // Auto-open browser only on the initial launch, not on standby takeover
        if (openBrowser && _config.Server.DashboardAutoOpen)
        {
            var url = $"http://localhost:{port}";
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not open browser. Dashboard available at {Url}", url);
            }
        }

        await app.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Let base.StopAsync signal stoppingToken, which causes RunAsync to exit
        // and WebApplication to tear itself down. No explicit _webApp.StopAsync
        // needed — RunAsync already handles stop+dispose internally.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _webApp = null;
    }

    /// <summary>
    /// Shares the root host's singletons with the dashboard's own container. The
    /// Tools page builds MCP tool classes from this container, so every tool
    /// constructor dependency must be forwarded here (enforced by a test).
    /// </summary>
    public static void ForwardRootServices(IServiceCollection services, IServiceProvider root, AppConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Core.Database.AppDatabase>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Core.Encryption.CredentialEncryptor>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.ImapClient.Repositories.AccountRepository>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.ImapClient.Repositories.FolderRepository>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.ImapClient.Repositories.MessageRepository>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.ImapClient.Repositories.SyncLogRepository>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.ImapClient.SyncManager>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Queue.QueueRepository>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Queue.QueueManager>());

        // Tool execution dependencies (for ToolsApi to construct MCP tool classes)
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.ImapClient.Repositories.AttachmentRepository>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Llm.Repositories.LlmAnalysisRepository>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Llm.IEmailAnalyzer>());

        // Observability repositories
        services.AddSingleton(root.GetRequiredService<MetricsRepository>());
        services.AddSingleton(root.GetRequiredService<LogsRepository>());

        // Multi-instance coordination
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Core.Database.HealthDatabase>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Core.Coordination.IInstanceCoordinator>());

        // OAuth services (from root host)
        services.AddSingleton(root.GetRequiredService<OAuthTokenRepository>());
        services.AddSingleton(root.GetRequiredService<OAuthTokenService>());
        services.AddSingleton(root.GetRequiredService<OAuthStateStore>());
        services.AddSingleton(root.GetRequiredService<IOAuthAccessTokenProvider>());

        // LLM config (for test endpoint)
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Core.Configuration.LlmConfig>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Llm.Repositories.LlmUsageRepository>());
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Llm.BudgetTracker>());

        // Email backend factory (for on-demand body fetch)
        services.AddSingleton(root.GetRequiredService<AwesomeImapMcp.Core.Email.IEmailBackendFactory>());

        // Account config store (account management tools: add_account_*, start_dashboard)
        services.AddSingleton(root.GetRequiredService<AccountsStore>());

        // ACP pool (optional — only registered when ACP provider is enabled)
        var acpPool = root.GetService<AwesomeImapMcp.Llm.Acp.IAcpClientPool>();
        if (acpPool is not null)
            services.AddSingleton(acpPool);

        // Instance info + root lifetime (for shutdown endpoint)
        services.AddSingleton(root.GetRequiredService<InstanceInfo>());
        services.AddSingleton(new RootLifetime(
            root.GetRequiredService<IHostApplicationLifetime>()));


        // Event bus (relayed to SignalR clients)
        services.AddSingleton(root.GetRequiredService<IEventBus>());
    }

}
