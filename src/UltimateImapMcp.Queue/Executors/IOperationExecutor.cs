using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public interface IOperationExecutor
{
    string OperationType { get; }
    Task ExecuteAsync(QueuedOperation operation, CancellationToken ct);
}
