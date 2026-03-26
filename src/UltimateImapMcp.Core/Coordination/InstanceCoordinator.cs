using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.Core.Coordination;

/// <summary>
/// Background service that writes heartbeats to health.db and elects a leader
/// among running instances.
/// </summary>
public sealed class InstanceCoordinator : BackgroundService, IInstanceCoordinator
{
    private readonly HealthDatabase _healthDb;
    private readonly InstanceInfo _instanceInfo;
    private readonly AppConfig _config;
    private readonly Func<int> _accountCountProvider;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<InstanceCoordinator> _logger;
    private readonly string _startedAt;

    public bool IsLeader { get; private set; }
    public string InstanceId => _instanceInfo.Id;

    private Func<bool>? _dashboardActiveProvider;

    public InstanceCoordinator(
        HealthDatabase healthDb,
        InstanceInfo instanceInfo,
        AppConfig config,
        Func<int> accountCountProvider,
        IHostApplicationLifetime lifetime,
        ILogger<InstanceCoordinator> logger)
    {
        _healthDb = healthDb;
        _instanceInfo = instanceInfo;
        _config = config;
        _accountCountProvider = accountCountProvider;
        _lifetime = lifetime;
        _logger = logger;
        _startedAt = DateTime.UtcNow.ToString("o");
    }

    /// <summary>Set a callback that returns whether the dashboard is actively serving.</summary>
    public void SetDashboardActiveProvider(Func<bool> provider) => _dashboardActiveProvider = provider;

    /// <summary>
    /// Determines the leader instance from a list of heartbeats.
    /// Dashboard hosts with the earliest started_at win; otherwise earliest started_at wins.
    /// Returns null if no fresh heartbeats exist.
    /// </summary>
    public static string? ComputeLeaderId(IReadOnlyList<HeartbeatRecord> all, TimeSpan staleThreshold)
    {
        var cutoff = DateTime.UtcNow.Subtract(staleThreshold);
        var fresh = all.Where(h => DateTime.Parse(h.LastHeartbeat).ToUniversalTime() > cutoff).ToList();

        if (fresh.Count == 0)
            return null;

        var dashboardHosts = fresh.Where(h => h.IsDashboardHost).ToList();
        if (dashboardHosts.Count > 0)
            return dashboardHosts.OrderBy(h => h.StartedAt).First().InstanceId;

        return fresh.OrderBy(h => h.StartedAt).First().InstanceId;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _config.Server.HeartbeatInterval;
        var staleMultiplier = _config.Server.HeartbeatStaleAfter;
        var staleThreshold = TimeSpan.FromSeconds(intervalSeconds * staleMultiplier);

        // Prune zombies on startup
        _healthDb.PruneStale(staleThreshold);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                WriteHeartbeat();

                var all = _healthDb.GetAllHeartbeats();
                var leaderId = ComputeLeaderId(all, staleThreshold);
                IsLeader = leaderId == _instanceInfo.Id;

                if (IsLeader)
                {
                    _healthDb.PruneStale(staleThreshold);
                }

                // Check if we've been asked to shut down
                var own = all.FirstOrDefault(h => h.InstanceId == _instanceInfo.Id);
                if (own?.ShutdownRequested == true)
                {
                    _logger.LogInformation("Shutdown requested for this instance via coordination");
                    _lifetime.StopApplication();
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in heartbeat loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _healthDb.DeleteHeartbeat(_instanceInfo.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete own heartbeat on shutdown");
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<InstanceHeartbeat> GetLiveInstances()
    {
        var intervalSeconds = _config.Server.HeartbeatInterval;
        var staleMultiplier = _config.Server.HeartbeatStaleAfter;
        var staleThreshold = TimeSpan.FromSeconds(intervalSeconds * staleMultiplier);

        var all = _healthDb.GetAllHeartbeats();
        var cutoff = DateTime.UtcNow.Subtract(staleThreshold);
        var leaderId = ComputeLeaderId(all, staleThreshold);

        return all
            .Where(h => DateTime.Parse(h.LastHeartbeat).ToUniversalTime() > cutoff)
            .Select(h => new InstanceHeartbeat(
                h.InstanceId, h.ProcessId, h.Cwd, h.Transport,
                h.IsDashboardHost, h.InstanceId == leaderId, h.StartedAt,
                h.LastHeartbeat, h.AccountsCount, h.CpuTimeMs,
                h.MemoryMb, h.ShutdownRequested))
            .ToList();
    }

    public Task<bool> RequestShutdownAsync(string instanceId) =>
        Task.FromResult(_healthDb.SetShutdownRequested(instanceId));

    private void WriteHeartbeat()
    {
        var process = Process.GetCurrentProcess();
        var now = DateTime.UtcNow.ToString("o");
        var cpuMs = (long)process.TotalProcessorTime.TotalMilliseconds;
        var memMb = (int)(process.WorkingSet64 / (1024 * 1024));
        var accountCount = 0;
        try { accountCount = _accountCountProvider(); }
        catch { /* best effort */ }

        // IsDashboardHost = actually serving (not just enabled in config)
        var isDashboardActive = _config.Server.DashboardEnabled
            && (_dashboardActiveProvider?.Invoke() ?? false);

        _healthDb.UpsertHeartbeat(
            _instanceInfo.Id,
            Environment.ProcessId,
            Environment.CurrentDirectory,
            _config.Server.Transport,
            isDashboardActive,
            _startedAt,
            now,
            accountCount,
            cpuMs,
            memMb);
    }
}
