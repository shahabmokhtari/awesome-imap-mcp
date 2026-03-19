using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.Dashboard;

/// <summary>
/// Background service that starts a Kestrel web server for the dashboard,
/// serving REST API, SignalR hub, and static files (React SPA).
/// Only activated when dashboard_enabled is true in config.
/// </summary>
public sealed class DashboardHost : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IServiceProvider _rootServices;
    private readonly ILogger<DashboardHost> _logger;
    private WebApplication? _webApp;

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

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");

        // Register dashboard-specific services
        builder.Services.AddSignalR();

        // Share singleton services from the root host
        builder.Services.AddSingleton(_config);
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.Core.Database.AppDatabase>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.Core.Encryption.CredentialEncryptor>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.ImapClient.Repositories.AccountRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.ImapClient.Repositories.FolderRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.ImapClient.Repositories.SyncLogRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.ImapClient.SyncManager>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.Queue.QueueRepository>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<UltimateImapMcp.Queue.QueueManager>());

        // Dashboard-own services
        builder.Services.AddSingleton(_rootServices.GetRequiredService<IEventBus>());
        builder.Services.AddSingleton<DashboardAuthRepository>();

        var app = builder.Build();
        _webApp = app;

        // Middleware pipeline
        app.UsePinAuth();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Map API routes
        app.MapAuthApi();
        app.MapAccountsApi();
        app.MapSyncApi();
        app.MapQueueApi();
        app.MapSettingsApi();

        // Map SignalR hub
        app.MapHub<DashboardHub>("/hub");

        // SPA fallback — serve index.html for non-API routes
        app.MapFallbackToFile("index.html");

        _logger.LogInformation("Dashboard starting on http://localhost:{Port}", port);

        try
        {
            await app.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected shutdown
        }
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
