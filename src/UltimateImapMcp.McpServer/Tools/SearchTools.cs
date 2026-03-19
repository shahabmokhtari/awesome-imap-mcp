using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class SearchTools(MessageRepository messageRepo)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description(
        "Search emails using full-text search. Searches cached emails first (instant). " +
        "Supports searching by subject, body, and sender. " +
        "Use summary_only=true to get compact results without body content.")]
    public string SearchEmails(
        [Description("Search query text")] string query,
        [Description("Account ID to search (optional)")] string? accountId = null,
        [Description("Maximum results (default: 20)")] int maxResults = 20,
        [Description("If true, return only subject/from/date/snippet (default: true)")] bool summaryOnly = true,
        [Description("Max body length in characters (0=unlimited, default: 0). Applied when summary_only=false.")] int maxBodyLength = 0)
    {
        var results = messageRepo.SearchFts(query, accountId, maxResults: maxResults);

        var mapped = results.Select(m =>
        {
            if (summaryOnly)
            {
                return (object)new
                {
                    uid = m.Uid,
                    subject = m.Subject,
                    from = m.FromAddress,
                    date = m.Date,
                    snippet = m.Snippet,
                    has_attachments = m.HasAttachments,
                    thread_id = m.ThreadId
                };
            }

            var body = m.BodyText;
            if (maxBodyLength > 0 && body != null && body.Length > maxBodyLength)
                body = body[..maxBodyLength] + "... [truncated]";

            return (object)new
            {
                uid = m.Uid,
                subject = m.Subject,
                from = m.FromAddress,
                to = m.ToAddresses,
                cc = m.CcAddresses,
                date = m.Date,
                snippet = m.Snippet,
                has_attachments = m.HasAttachments,
                thread_id = m.ThreadId,
                body,
                body_fetched = m.BodyFetched
            };
        }).ToList();

        return JsonSerializer.Serialize(new { count = mapped.Count, results = mapped }, JsonOptions);
    }
}
