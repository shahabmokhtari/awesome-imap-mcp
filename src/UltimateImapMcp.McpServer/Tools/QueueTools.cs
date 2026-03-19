using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.Queue;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class QueueTools(QueueManager queueManager)
{
    private readonly QueueManager _queueManager = queueManager;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description("Confirm a pending send operation, allowing it to be executed immediately.")]
    public string ConfirmSend(
        [Description("Pending operation ID")] string pendingId)
    {
        var confirmed = _queueManager.Confirm(pendingId);
        return JsonSerializer.Serialize(new
        {
            pending_id = pendingId,
            confirmed,
            message = confirmed ? "Operation confirmed and queued for execution." : "Operation not found or cannot be confirmed (may already be confirmed, completed, or cancelled)."
        }, JsonOptions);
    }

    [McpServerTool, Description("Cancel a pending or confirmed operation before it is executed.")]
    public string CancelOperation(
        [Description("Pending operation ID")] string pendingId)
    {
        var cancelled = _queueManager.Cancel(pendingId);
        return JsonSerializer.Serialize(new
        {
            pending_id = pendingId,
            cancelled,
            message = cancelled ? "Operation cancelled successfully." : "Operation not found or cannot be cancelled (may already be completed, processing, or cancelled)."
        }, JsonOptions);
    }

    [McpServerTool, Description("List all pending and confirmed operations, optionally filtered by account.")]
    public string ListPending(
        [Description("Account ID to filter by (optional, omit for all accounts)")] string? accountId = null)
    {
        var operations = _queueManager.ListPending(accountId);

        var mapped = operations.Select(op => new
        {
            id = op.Id,
            account_id = op.AccountId,
            operation = op.Operation,
            priority = op.Priority,
            status = op.Status,
            requires_confirm = op.RequiresConfirm,
            retry_count = op.RetryCount,
            max_retries = op.MaxRetries,
            created_at = op.CreatedAt,
            confirmed_at = op.ConfirmedAt,
            sends_at = op.SendsAt,
            error_message = op.ErrorMessage
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            account_id = accountId,
            count = mapped.Count,
            operations = mapped
        }, JsonOptions);
    }
}
