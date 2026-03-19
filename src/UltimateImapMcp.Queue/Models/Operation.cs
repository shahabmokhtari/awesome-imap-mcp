namespace UltimateImapMcp.Queue.Models;

public enum OperationType
{
    Send, Reply, Forward,
    Delete, Move,
    MarkRead, MarkUnread,
    Flag, Unflag,
    Label, Unlabel,
    BulkDelete, BulkMove
}

public enum OperationStatus
{
    Pending, Confirmed, Processing, Completed, Failed, Cancelled
}

public enum OperationPriority
{
    P0 = 0,  // near-immediate: send, reply, forward
    P1 = 1,  // batched: delete, move, mark, flag, label
    P2 = 2   // background: bulk operations
}

public record QueuedOperation
{
    public required string Id { get; init; }
    public required string AccountId { get; init; }
    public required string Operation { get; init; }
    public required int Priority { get; init; }
    public required string Status { get; init; }
    public required string Payload { get; init; }
    public bool RequiresConfirm { get; init; }
    public string? ErrorMessage { get; init; }
    public int RetryCount { get; init; }
    public int MaxRetries { get; init; }
    public string CreatedAt { get; init; } = "";
    public string? ConfirmedAt { get; init; }
    public string? StartedAt { get; init; }
    public string? CompletedAt { get; init; }
    public string? CancelledAt { get; init; }
    public string? SendsAt { get; init; }
}

public record EnqueueRequest
{
    public required string AccountId { get; init; }
    public required OperationType Operation { get; init; }
    public required OperationPriority Priority { get; init; }
    public required string Payload { get; init; }
    public bool RequiresConfirm { get; init; }
    public int MaxRetries { get; init; } = 3;
    public string? SendsAt { get; init; }  // for implicit confirm: ISO 8601 timestamp
}
