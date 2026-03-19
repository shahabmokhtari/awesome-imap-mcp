using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class OrganizeTools(QueueManager queueManager)
{
    private readonly QueueManager _queueManager = queueManager;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static List<int> ParseUids(string uids)
    {
        var parts = uids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var uid))
                throw new ArgumentException($"Invalid UID value: '{part}'. UIDs must be integers.");
            result.Add(uid);
        }
        return result;
    }

    [McpServerTool, Description("Delete one or more messages by UID from a folder.")]
    public string DeleteMessages(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Folder name")] string folder)
    {
        try
        {
            var uidList = ParseUids(uids);
            var payload = JsonSerializer.Serialize(new { uids = uidList, folder });
            var operationType = uidList.Count > 10 ? OperationType.BulkDelete : OperationType.Delete;
            var pendingId = _queueManager.EnqueueOperation(accountId, operationType, payload);

            return JsonSerializer.Serialize(new { pending_id = pendingId, operation = "delete", uids = uidList, folder }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool, Description("Move one or more messages from one folder to another.")]
    public string MoveMessages(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Source folder name")] string fromFolder,
        [Description("Destination folder name")] string toFolder)
    {
        try
        {
            var uidList = ParseUids(uids);
            var payload = JsonSerializer.Serialize(new { uids = uidList, from_folder = fromFolder, to_folder = toFolder });
            var operationType = uidList.Count > 10 ? OperationType.BulkMove : OperationType.Move;
            var pendingId = _queueManager.EnqueueOperation(accountId, operationType, payload);

            return JsonSerializer.Serialize(new { pending_id = pendingId, operation = "move", uids = uidList, from_folder = fromFolder, to_folder = toFolder }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool, Description("Mark one or more messages as read.")]
    public string MarkRead(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Folder name")] string folder)
    {
        try
        {
            var uidList = ParseUids(uids);
            var payload = JsonSerializer.Serialize(new { uids = uidList, folder });
            var pendingId = _queueManager.EnqueueOperation(accountId, OperationType.MarkRead, payload);

            return JsonSerializer.Serialize(new { pending_id = pendingId, operation = "mark_read", uids = uidList, folder }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool, Description("Mark one or more messages as unread.")]
    public string MarkUnread(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Folder name")] string folder)
    {
        try
        {
            var uidList = ParseUids(uids);
            var payload = JsonSerializer.Serialize(new { uids = uidList, folder });
            var pendingId = _queueManager.EnqueueOperation(accountId, OperationType.MarkUnread, payload);

            return JsonSerializer.Serialize(new { pending_id = pendingId, operation = "mark_unread", uids = uidList, folder }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool, Description("Flag or unflag one or more messages.")]
    public string FlagMessages(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Folder name")] string folder,
        [Description("True to flag messages, false to unflag")] bool set)
    {
        try
        {
            var uidList = ParseUids(uids);
            var operationType = set ? OperationType.Flag : OperationType.Unflag;
            var payload = JsonSerializer.Serialize(new { uids = uidList, folder, set });
            var pendingId = _queueManager.EnqueueOperation(accountId, operationType, payload);

            return JsonSerializer.Serialize(new { pending_id = pendingId, operation = set ? "flag" : "unflag", uids = uidList, folder }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool, Description("Add or remove a label from one or more messages.")]
    public string LabelMessages(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Label name")] string label,
        [Description("Action: \"add\" or \"remove\"")] string action)
    {
        try
        {
            var uidList = ParseUids(uids);
            var operationType = action.Equals("remove", StringComparison.OrdinalIgnoreCase)
                ? OperationType.Unlabel
                : OperationType.Label;
            var payload = JsonSerializer.Serialize(new { uids = uidList, label, action });
            var pendingId = _queueManager.EnqueueOperation(accountId, operationType, payload);

            return JsonSerializer.Serialize(new { pending_id = pendingId, operation = $"label_{action}", uids = uidList, label }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }
}
