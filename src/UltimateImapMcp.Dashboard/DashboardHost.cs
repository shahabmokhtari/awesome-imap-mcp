using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.Core.Repositories;

namespace UltimateImapMcp.Dashboard;

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

        // Wait in standby if another instance owns the port
        await PortUtils.WaitForPortWithBackoffAsync(port, _logger, "Dashboard", stoppingToken).ConfigureAwait(false);

        // Clear sessions once on startup — force re-auth after server restart.
        // This runs once in ExecuteAsync, not inside StartDashboard which retries on failure.
        var authRepo = new DashboardAuthRepository(
            _rootServices.GetRequiredService<UltimateImapMcp.Core.Database.AppDatabase>());
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
        builder.WebHost.UseUrls($"http://localhost:{port}");

        if (!Directory.Exists(builder.Environment.WebRootPath))
        {
            _logger.LogWarning(
                "Dashboard wwwroot not found at {Path}. Static files will not be served. " +
                "Run 'npm run build' in dashboard/client/ to build the SPA.",
                builder.Environment.WebRootPath);
        }

        // Register dashboard-specific services
        builder.Services.AddHttpClient();
        builder.Services.AddSignalR();

        // Share singleton services from the root host
        builder.Services.AddSingleton(_config);
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.Core.Database.AppDatabase>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.Core.Encryption.CredentialEncryptor>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.ImapClient.Repositories.AccountRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.ImapClient.Repositories.FolderRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.ImapClient.Repositories.MessageRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.ImapClient.Repositories.SyncLogRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.ImapClient.SyncManager>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.Queue.QueueRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.Queue.QueueManager>());

        // Observability repositories
        builder.Services.AddSingleton(_rootServices.GetRequiredService<MetricsRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<LogsRepository>());

        // OAuth services (from root host)
        builder.Services.AddSingleton(_rootServices.GetRequiredService<OAuthTokenRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<OAuthTokenService>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<OAuthStateStore>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<IOAuthAccessTokenProvider>());

        // LLM config (for test endpoint)
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.Core.Configuration.LlmConfig>());

        // Instance info + root lifetime (for shutdown endpoint)
        builder.Services.AddSingleton(_rootServices.GetRequiredService<InstanceInfo>());
        builder.Services.AddSingleton(new RootLifetime(
            _rootServices.GetRequiredService<IHostApplicationLifetime>()));

        // Dashboard-own services
        builder.Services.AddSingleton(_rootServices.GetRequiredService<IEventBus>());
        builder.Services.AddSingleton<DashboardAuthRepository>();
        builder.Services.AddHostedService<DashboardHubRelay>();

        var app = builder.Build();
        _webApp = app;

        // Middleware pipeline
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
        if (_webApp is not null)
        {
            await _webApp.StopAsync(cancellationToken).ConfigureAwait(false);
            await _webApp.DisposeAsync().ConfigureAwait(false);
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
