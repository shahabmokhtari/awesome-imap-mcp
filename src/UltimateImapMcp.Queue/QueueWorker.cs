using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Queue.Executors;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue;

public class QueueWorker(
    QueueRepository repo,
    IEnumerable<IOperationExecutor> executors,
    QueueConfig config,
    ILogger<QueueWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, IOperationExecutor> _executors =
        executors.ToDictionary(e => e.OperationType, StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("QueueWorker started — P0 every {P0}s, P1 every {P1}s, P2 every {P2}s",
            config.P0FlushInterval, config.P1FlushInterval, config.P2FlushInterval);

        var p0Interval = TimeSpan.FromSeconds(config.P0FlushInterval);
        var p1Interval = TimeSpan.FromSeconds(config.P1FlushInterval);
        var p2Interval = TimeSpan.FromSeconds(config.P2FlushInterval);

        var lastP0 = DateTime.UtcNow;
        var lastP1 = DateTime.UtcNow;
        var lastP2 = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            if (now - lastP0 >= p0Interval)
            {
                await FlushPriorityAsync(repo, _executors, 0, ct);
                lastP0 = now;
            }
            if (now - lastP1 >= p1Interval)
            {
                await FlushPriorityAsync(repo, _executors, 1, ct);
                lastP1 = now;
            }
            if (now - lastP2 >= p2Interval)
            {
                await FlushPriorityAsync(repo, _executors, 2, ct);
                lastP2 = now;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    public static async Task FlushPriorityAsync(
        QueueRepository repo,
        Dictionary<string, IOperationExecutor> executors,
        int priority,
        CancellationToken ct)
    {
        var operations = repo.GetConfirmedByPriority(priority);

        foreach (var op in operations)
        {
            // For P0 sends with sends_at: check if undo window has passed
            if (op.SendsAt != null && DateTime.Parse(op.SendsAt) > DateTime.UtcNow)
                continue;

            if (!executors.TryGetValue(op.Operation, out var executor))
            {
                repo.MarkFailed(op.Id, $"No executor found for operation type: {op.Operation}");
                continue;
            }

            repo.UpdateStatus(op.Id, "processing");

            try
            {
                await executor.ExecuteAsync(op, ct);
                repo.UpdateStatus(op.Id, "completed");
            }
            catch (Exception ex)
            {
                if (op.RetryCount + 1 >= op.MaxRetries)
                {
                    repo.MarkFailed(op.Id, ex.Message, incrementRetry: true);
                }
                else
                {
                    repo.MarkRetryable(op.Id, ex.Message);
                }
            }
        }
    }
}
