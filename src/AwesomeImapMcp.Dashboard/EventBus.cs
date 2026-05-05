using Microsoft.Extensions.Logging;

namespace AwesomeImapMcp.Dashboard;

/// <summary>Marker interface for all dashboard events.</summary>
public interface IEvent { }

public record SyncProgressEvent(string AccountId, string Folder, int MessagesSynced, int TotalMessages) : IEvent;
public record SyncCompleteEvent(string AccountId, string Folder, int MessagesSynced) : IEvent;
public record SyncErrorEvent(string AccountId, string Folder, string Error) : IEvent;
public record QueueAddedEvent(string PendingId, string Operation, string AccountId) : IEvent;
public record QueueCompletedEvent(string PendingId, string Operation) : IEvent;
public record QueueFailedEvent(string PendingId, string Operation, string Error) : IEvent;

/// <summary>
/// In-memory pub/sub event bus using lock-synchronized handler lists for decoupled
/// communication between services and the SignalR hub.
/// </summary>
public interface IEventBus
{
    void Publish<T>(T @event) where T : IEvent;
    IDisposable Subscribe<T>(Action<T> handler) where T : IEvent;
}

public sealed class EventBus(ILogger<EventBus> logger) : IEventBus
{
    private readonly object _lock = new();
    private readonly Dictionary<Type, List<object>> _handlers = new();

    public void Publish<T>(T @event) where T : IEvent
    {
        List<object> handlers;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                return;
            handlers = [.. list]; // snapshot to avoid holding lock during invocation
        }

        foreach (var handler in handlers)
        {
            try
            {
                ((Action<T>)handler)(@event);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "EventBus subscriber failed for {EventType}", typeof(T).Name);
            }
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : IEvent
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _handlers[typeof(T)] = list;
            }
            list.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                if (_handlers.TryGetValue(typeof(T), out var list))
                    list.Remove(handler);
            }
        });
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            onDispose();
        }
    }
}
