using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class ComposeTools(QueueManager queueManager)
{
    private readonly QueueManager _queueManager = queueManager;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description("Compose and send a new email. Returns a pending_id and confirm_mode.")]
    public string SendEmail(
        [Description("Account ID")] string accountId,
        [Description("Recipient email address(es), comma-separated")] string to,
        [Description("Email subject")] string subject,
        [Description("Email body (plain text)")] string body,
        [Description("CC recipient(s), comma-separated (optional)")] string? cc = null)
    {
        var payload = JsonSerializer.Serialize(new { to, subject, body, cc });
        var result = _queueManager.EnqueueSend(accountId, payload);

        return JsonSerializer.Serialize(new
        {
            pending_id = result.PendingId,
            confirm_mode = result.ConfirmMode,
            status = result.Status,
            sends_at = result.SendsAt,
            undo_window_seconds = result.UndoWindowSeconds
        }, JsonOptions);
    }

    [McpServerTool, Description("Reply to an existing email message.")]
    public string ReplyTo(
        [Description("Account ID")] string accountId,
        [Description("Message UID to reply to")] int uid,
        [Description("Folder containing the message")] string folder,
        [Description("Reply body (plain text)")] string body,
        [Description("Reply to all recipients (default: false)")] bool replyAll = false)
    {
        var payload = JsonSerializer.Serialize(new { uid, folder, body, reply_all = replyAll });
        var result = _queueManager.EnqueueSend(accountId, payload);

        return JsonSerializer.Serialize(new
        {
            pending_id = result.PendingId,
            confirm_mode = result.ConfirmMode,
            status = result.Status,
            sends_at = result.SendsAt,
            undo_window_seconds = result.UndoWindowSeconds
        }, JsonOptions);
    }

    [McpServerTool, Description("Forward an email message to one or more recipients.")]
    public string Forward(
        [Description("Account ID")] string accountId,
        [Description("Message UID to forward")] int uid,
        [Description("Folder containing the message")] string folder,
        [Description("Recipient email address(es), comma-separated")] string to,
        [Description("Additional body text to prepend (optional)")] string? body = null)
    {
        var payload = JsonSerializer.Serialize(new { uid, folder, to, body });
        var result = _queueManager.EnqueueSend(accountId, payload);

        return JsonSerializer.Serialize(new
        {
            pending_id = result.PendingId,
            confirm_mode = result.ConfirmMode,
            status = result.Status,
            sends_at = result.SendsAt,
            undo_window_seconds = result.UndoWindowSeconds
        }, JsonOptions);
    }
}
