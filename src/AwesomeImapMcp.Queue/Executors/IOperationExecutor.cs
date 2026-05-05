using AwesomeImapMcp.Queue.Models;

namespace AwesomeImapMcp.Queue.Executors;

public interface IOperationExecutor
{
    IReadOnlyList<string> SupportedOperations { get; }
    Task ExecuteAsync(QueuedOperation operation, CancellationToken ct);
}
