using AwesomeImapMcp.ImapClient;

namespace AwesomeImapMcp.ImapClient.Tests;

public class ConnectionThrottleTests
{
    [Fact]
    public async Task AcquireAsync_LimitsConnections()
    {
        // Use a unique account ID to avoid conflicts with other tests
        var accountId = $"test-{Guid.NewGuid()}";
        var maxConnections = 2;

        // Acquire all available slots
        var slot1 = await ConnectionThrottle.AcquireAsync(accountId, maxConnections);
        var slot2 = await ConnectionThrottle.AcquireAsync(accountId, maxConnections);

        // Third acquire should block — verify with a short timeout
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ConnectionThrottle.AcquireAsync(accountId, maxConnections, cts.Token));

        // Release one slot
        slot1.Dispose();

        // Now acquiring should succeed
        var slot3 = await ConnectionThrottle.AcquireAsync(accountId, maxConnections);
        slot2.Dispose();
        slot3.Dispose();
    }

    [Fact]
    public void Acquire_Sync_Works()
    {
        var accountId = $"test-sync-{Guid.NewGuid()}";
        using var slot = ConnectionThrottle.Acquire(accountId, 3);
        Assert.NotNull(slot);
    }

    [Fact]
    public void DoubleDispose_DoesNotThrow()
    {
        var accountId = $"test-double-{Guid.NewGuid()}";
        var slot = ConnectionThrottle.Acquire(accountId, 3);
        slot.Dispose();
        slot.Dispose(); // Should not throw or double-release
    }
}
