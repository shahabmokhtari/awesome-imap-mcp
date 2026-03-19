using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public interface IOperationExecutor
{
    IReadOnlyList<string> SupportedOperations { get; }
    Task ExecuteAsync(QueuedOperation operation, CancellationToken ct);
}
