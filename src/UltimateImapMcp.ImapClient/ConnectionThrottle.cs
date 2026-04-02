using System.Collections.Concurrent;

namespace UltimateImapMcp.ImapClient;

/// <summary>
/// Limits concurrent IMAP connections per account using semaphores.
/// Prevents exhausting server connection limits (e.g., Yahoo's ~15 connection cap).
/// Thread-safe static registry — shared across all ImapConnectionManager instances.
/// </summary>
public static class ConnectionThrottle
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

    /// <summary>
    /// Gets or creates the connection semaphore for an account.
    /// The first call for an account ID establishes the limit; subsequent calls return the same semaphore.
    /// </summary>
    public static SemaphoreSlim GetSemaphore(string accountId, int maxConnections = 5)
    {
        return _semaphores.GetOrAdd(accountId, _ => new SemaphoreSlim(maxConnections, maxConnections));
    }

    /// <summary>Acquires a connection slot. Blocks until one is available.</summary>
    public static async Task<IDisposable> AcquireAsync(string accountId, int maxConnections = 5, CancellationToken ct = default)
    {
        var semaphore = GetSemaphore(accountId, maxConnections);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new SemaphoreReleaser(semaphore);
    }

    /// <summary>Acquires a connection slot synchronously.</summary>
    public static IDisposable Acquire(string accountId, int maxConnections = 5)
    {
        var semaphore = GetSemaphore(accountId, maxConnections);
        semaphore.Wait();
        return new SemaphoreReleaser(semaphore);
    }

    private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _released, 1, 0) == 0)
                semaphore.Release();
        }
    }
}
