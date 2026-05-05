using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Queue;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public class QueueTools(QueueManager queueManager, AppConfig config, ILogger<QueueTools> logger)
{
    [McpServerTool, Description(
        "Confirm a pending send operation to execute it immediately. Use list_pending to see available operations.")]
    public string ConfirmSend(
        [Description("Pending operation ID")] string pendingId)
    {
        return McpJsonDefaults.LogToolCall(logger, "confirm_send",
            new Dictionary<string, object?> { ["pendingId"] = pendingId },
            () =>
            {
                try
                {
                    var confirmed = queueManager.Confirm(pendingId);
                    return JsonSerializer.Serialize(new
                    {
                        pending_id = pendingId,
                        confirmed,
                        message = confirmed ? "Operation confirmed and queued for execution." : "Operation not found or cannot be confirmed (may already be confirmed, completed, or cancelled)."
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "ConfirmSend failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description(
        "Cancel a pending operation before it executes. Cannot cancel already-executed operations.")]
    public string CancelOperation(
        [Description("Pending operation ID")] string pendingId)
    {
        return McpJsonDefaults.LogToolCall(logger, "cancel_operation",
            new Dictionary<string, object?> { ["pendingId"] = pendingId },
            () =>
            {
                try
                {
                    var cancelled = queueManager.Cancel(pendingId);
                    return JsonSerializer.Serialize(new
                    {
                        pending_id = pendingId,
                        cancelled,
                        message = cancelled ? "Operation cancelled successfully." : "Operation not found or cannot be cancelled (may already be completed, processing, or cancelled)."
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "CancelOperation failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description(
        "List all pending and confirmed operations, optionally filtered by account. " +
        "Use this to check operation status or find pending_ids for confirm/cancel.")]
    public string ListPending(
        [Description("Account ID to filter by (optional, omit for all accounts)")] string? accountId = null)
    {
        return McpJsonDefaults.LogToolCall(logger, "list_pending",
            new Dictionary<string, object?> { ["accountId"] = accountId },
            () =>
            {
                try
                {
                    var operations = queueManager.ListPending(accountId);

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
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "ListPending failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }
}
