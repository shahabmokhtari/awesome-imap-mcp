using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue;

public record SendEnqueueResult(string PendingId, string ConfirmMode, string Status, string? SendsAt, int? UndoWindowSeconds);

public class QueueManager(QueueRepository repo, AppConfig config)
{
    public SendEnqueueResult EnqueueSend(string accountId, string payload)
    {
        var account = config.Accounts.FirstOrDefault(a =>
            a.Name.Equals(accountId, StringComparison.OrdinalIgnoreCase));
        if (account is null)
            throw new InvalidOperationException($"Account '{accountId}' not found.");
        var confirmMode = account.ConfirmMode ?? "implicit";
        var undoSeconds = account.UndoWindowSeconds;

        string? sendsAt = null;
        bool requiresConfirm;
        string status;

        if (confirmMode == "explicit")
        {
            requiresConfirm = true;
            status = "awaiting_confirmation";
        }
        else
        {
            requiresConfirm = false;
            sendsAt = DateTime.UtcNow.AddSeconds(undoSeconds).ToString("O");
            status = "will_send_at";
        }

        var id = repo.Insert(new EnqueueRequest
        {
            AccountId = accountId,
            Operation = OperationType.Send,
            Priority = OperationPriority.P0,
            Payload = payload,
            RequiresConfirm = requiresConfirm,
            SendsAt = sendsAt
        });

        // For implicit confirm, auto-confirm immediately (worker checks sends_at)
        if (!requiresConfirm)
            repo.UpdateStatus(id, "confirmed");

        return new SendEnqueueResult(id, confirmMode, status, sendsAt, undoSeconds);
    }

    public string EnqueueOperation(string accountId, OperationType operation, string payload)
    {
        var priority = operation switch
        {
            OperationType.Send or OperationType.Reply or OperationType.Forward => OperationPriority.P0,
            OperationType.BulkDelete or OperationType.BulkMove => OperationPriority.P2,
            _ => OperationPriority.P1
        };

        var id = repo.Insert(new EnqueueRequest
        {
            AccountId = accountId,
            Operation = operation,
            Priority = priority,
            Payload = payload
        });

        // P1/P2 operations auto-confirm (no undo window)
        if (priority != OperationPriority.P0)
            repo.UpdateStatus(id, "confirmed");

        return id;
    }

    public bool Confirm(string pendingId)
    {
        var op = repo.GetById(pendingId);
        if (op == null || op.Status is not ("pending" or "awaiting_confirmation"))
            return false;
        repo.UpdateStatus(pendingId, "confirmed");
        return true;
    }

    public bool Cancel(string pendingId) => repo.Cancel(pendingId);

    public QueuedOperation? GetOperation(string pendingId) => repo.GetById(pendingId);

    public List<QueuedOperation> ListPending(string? accountId = null) =>
        repo.GetAllPending(accountId);
}
