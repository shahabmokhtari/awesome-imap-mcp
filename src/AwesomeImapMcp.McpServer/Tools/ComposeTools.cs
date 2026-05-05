using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Queue;
using AwesomeImapMcp.Queue.Models;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public class ComposeTools(QueueManager queueManager, AppConfig config, ILogger<ComposeTools> logger)
{
    [McpServerTool, Description(
        "Compose and send a new email. The message is queued and may require confirmation depending on account settings. " +
        "Returns a pending_id for tracking.")]
    public string SendEmail(
        [Description("Account ID")] string accountId,
        [Description("Recipient email address(es), comma-separated")] string to,
        [Description("Email subject")] string subject,
        [Description("Email body (plain text)")] string body,
        [Description("CC recipient(s), comma-separated (optional)")] string? cc = null,
        [Description("BCC recipient(s), comma-separated")] string? bcc = null)
    {
        return McpJsonDefaults.LogToolCall(logger, "send_email",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["to"] = to, ["subject"] = subject },
            () =>
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new { to, subject, body, cc, bcc });
                    var result = queueManager.EnqueueSend(accountId, payload);
                    return FormatResult(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return McpJsonDefaults.Error($"Send failed: {ex.Message}");
                }
            }, config);
    }

    [McpServerTool, Description(
        "Reply to an existing email. Set reply_all=true to include all original recipients. " +
        "The original message is automatically quoted.")]
    public string ReplyTo(
        [Description("Account ID")] string accountId,
        [Description("Message UID to reply to")] int uid,
        [Description("Folder containing the message")] string folder,
        [Description("Reply body (plain text)")] string body,
        [Description("Reply to all recipients (default: false)")] bool replyAll = false)
    {
        return McpJsonDefaults.LogToolCall(logger, "reply_to",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["uid"] = uid, ["folder"] = folder },
            () =>
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new { uid, folder, body, reply_all = replyAll });
                    var result = queueManager.EnqueueSend(accountId, payload);
                    return FormatResult(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return McpJsonDefaults.Error($"Reply failed: {ex.Message}");
                }
            }, config);
    }

    [McpServerTool, Description(
        "Forward an email to new recipients. The original message content is automatically included. " +
        "Add optional body text as a preamble.")]
    public string Forward(
        [Description("Account ID")] string accountId,
        [Description("Message UID to forward")] int uid,
        [Description("Folder containing the message")] string folder,
        [Description("Recipient email address(es), comma-separated")] string to,
        [Description("Additional body text to prepend (optional)")] string? body = null)
    {
        return McpJsonDefaults.LogToolCall(logger, "forward",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["uid"] = uid, ["folder"] = folder, ["to"] = to },
            () =>
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new { uid, folder, to, body });
                    var result = queueManager.EnqueueSend(accountId, payload);
                    return FormatResult(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return McpJsonDefaults.Error($"Forward failed: {ex.Message}");
                }
            }, config);
    }

    private static string FormatResult(SendEnqueueResult result) =>
        JsonSerializer.Serialize(new
        {
            pending_id = result.PendingId,
            confirm_mode = result.ConfirmMode,
            status = result.Status,
            sends_at = result.SendsAt,
            undo_window_seconds = result.UndoWindowSeconds
        }, McpJsonDefaults.Options);
}
