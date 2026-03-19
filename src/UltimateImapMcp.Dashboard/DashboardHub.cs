using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UltimateImapMcp.Dashboard;

/// <summary>
/// SignalR hub for real-time dashboard updates. Clients receive events for:
/// sync:progress, sync:complete, sync:error,
/// queue:added, queue:completed, queue:failed
/// </summary>
public class DashboardHub : Hub
{
    // Hub is intentionally empty — events are pushed from DashboardHubRelay
}

/// <summary>
/// Background service that subscribes to IEventBus events and forwards
/// them to connected SignalR clients.
/// </summary>
public sealed class DashboardHubRelay(
    IEventBus eventBus,
    IHubContext<DashboardHub> hubContext,
    ILogger<DashboardHubRelay> logger) : BackgroundService
{
    private readonly List<IDisposable> _subscriptions = [];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(eventBus.Subscribe<SyncProgressEvent>(e =>
            SendToAll("sync:progress", e)));

        _subscriptions.Add(eventBus.Subscribe<SyncCompleteEvent>(e =>
            SendToAll("sync:complete", e)));

        _subscriptions.Add(eventBus.Subscribe<SyncErrorEvent>(e =>
            SendToAll("sync:error", e)));

        _subscriptions.Add(eventBus.Subscribe<QueueAddedEvent>(e =>
            SendToAll("queue:added", e)));

        _subscriptions.Add(eventBus.Subscribe<QueueCompletedEvent>(e =>
            SendToAll("queue:completed", e)));

        _subscriptions.Add(eventBus.Subscribe<QueueFailedEvent>(e =>
            SendToAll("queue:failed", e)));

        logger.LogInformation("DashboardHubRelay started — forwarding events to SignalR clients");
        return Task.CompletedTask;
    }

    private void SendToAll(string method, object arg)
    {
        _ = hubContext.Clients.All.SendAsync(method, arg);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        return base.StopAsync(cancellationToken);
    }
}
