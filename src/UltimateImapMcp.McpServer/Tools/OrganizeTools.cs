using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class OrganizeTools(QueueManager queueManager, AppConfig config, ILogger<OrganizeTools> logger)
{
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

    [McpServerTool, Description(
        "Delete one or more messages by UID. Messages are moved to trash on most IMAP servers. This operation is queued.")]
    public string DeleteMessages(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Folder name")] string folder)
    {
        return McpJsonDefaults.LogToolCall(logger, "delete_messages",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["uids"] = uids, ["folder"] = folder },
            () =>
            {
                try
                {
                    var uidList = ParseUids(uids);
                    var payload = JsonSerializer.Serialize(new { uids = uidList, folder });
                    var operationType = uidList.Count > 10 ? OperationType.BulkDelete : OperationType.Delete;
                    var pendingId = queueManager.EnqueueOperation(accountId, operationType, payload);

                    return JsonSerializer.Serialize(new { pending_id = pendingId, operation = "delete", uids = uidList, folder }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DeleteMessages failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description(
        "Move messages between folders (e.g., INBOX to Archive). This operation is queued.")]
    public string MoveMessages(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Source folder name")] string fromFolder,
        [Description("Destination folder name")] string toFolder)
    {
        return McpJsonDefaults.LogToolCall(logger, "move_messages",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["uids"] = uids, ["fromFolder"] = fromFolder, ["toFolder"] = toFolder },
            () =>
            {
                try
                {
                    var uidList = ParseUids(uids);
                    var payload = JsonSerializer.Serialize(new { uids = uidList, from_folder = fromFolder, to_folder = toFolder });
                    var operationType = uidList.Count > 10 ? OperationType.BulkMove : OperationType.Move;
                    var pendingId = queueManager.EnqueueOperation(accountId, operationType, payload);

                    return JsonSerializer.Serialize(new { pending_id = pendingId, operation = "move", uids = uidList, from_folder = fromFolder, to_folder = toFolder }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MoveMessages failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description("Mark one or more messages as read. This operation is queued.")]
    public string MarkRead(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Folder name")] string folder)
    {
        return McpJsonDefaults.LogToolCall(logger, "mark_read",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["uids"] = uids, ["folder"] = folder },
            () =>
            {
                try
                {
                    var uidList = ParseUids(uids);
                    var payload = JsonSerializer.Serialize(new { uids = uidList, folder });
                    var pendingId = queueManager.EnqueueOperation(accountId, OperationType.MarkRead, payload);

                    return JsonSerializer.Serialize(new { pending_id = pendingId, operation = "mark_read", uids = uidList, folder }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MarkRead failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description("Mark one or more messages as unread. This operation is queued.")]
    public string MarkUnread(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Folder name")] string folder)
    {
        return McpJsonDefaults.LogToolCall(logger, "mark_unread",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["uids"] = uids, ["folder"] = folder },
            () =>
            {
                try
                {
                    var uidList = ParseUids(uids);
                    var payload = JsonSerializer.Serialize(new { uids = uidList, folder });
                    var pendingId = queueManager.EnqueueOperation(accountId, OperationType.MarkUnread, payload);

                    return JsonSerializer.Serialize(new { pending_id = pendingId, operation = "mark_unread", uids = uidList, folder }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MarkUnread failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description("Flag or unflag one or more messages (starred/important marker). This operation is queued.")]
    public string FlagMessages(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Folder name")] string folder,
        [Description("True to flag messages, false to unflag")] bool set)
    {
        return McpJsonDefaults.LogToolCall(logger, "flag_messages",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["uids"] = uids, ["folder"] = folder, ["set"] = set },
            () =>
            {
                try
                {
                    var uidList = ParseUids(uids);
                    var operationType = set ? OperationType.Flag : OperationType.Unflag;
                    var payload = JsonSerializer.Serialize(new { uids = uidList, folder, set });
                    var pendingId = queueManager.EnqueueOperation(accountId, operationType, payload);

                    return JsonSerializer.Serialize(new { pending_id = pendingId, operation = set ? "flag" : "unflag", uids = uidList, folder }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "FlagMessages failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description(
        "Add or remove a label (IMAP keyword) from one or more messages. This operation is queued. " +
        "Use list_labels to see the configured vocabulary for consistent labeling.")]
    public string LabelMessages(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs")] string uids,
        [Description("Label name")] string label,
        [Description("Folder name")] string folder,
        [Description("Action: \"add\" or \"remove\"")] string action)
    {
        return McpJsonDefaults.LogToolCall(logger, "label_messages",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["uids"] = uids, ["label"] = label, ["action"] = action },
            () =>
            {
                try
                {
                    var uidList = ParseUids(uids);
                    var operationType = action.Equals("remove", StringComparison.OrdinalIgnoreCase)
                        ? OperationType.Unlabel
                        : OperationType.Label;
                    var payload = JsonSerializer.Serialize(new { uids = uidList, label, folder, action });
                    var pendingId = queueManager.EnqueueOperation(accountId, operationType, payload);

                    // Advisory vocabulary warning (only on add, only if vocabulary is non-empty)
                    string? warning = null;
                    if (!action.Equals("remove", StringComparison.OrdinalIgnoreCase)
                        && config.Labels.Items.Count > 0
                        && !config.Labels.Items.Any(l => l.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
                    {
                        var known = string.Join(", ", config.Labels.Items.Select(l => l.Name));
                        warning = $"Label '{label}' is not in the configured vocabulary. Known labels: {known}";
                    }

                    return JsonSerializer.Serialize(new { pending_id = pendingId, operation = $"label_{action}", uids = uidList, label, warning }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "LabelMessages failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }
}
