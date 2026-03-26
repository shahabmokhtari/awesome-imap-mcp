using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Llm.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class ReportTools(MessageRepository messageRepo, LlmAnalysisRepository analysisRepo)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description(
        "Generate a comprehensive mailbox report with per-folder volume stats, attachment counts, and total storage used.")]
    public string MailboxReport(
        [Description("Account ID")] string accountId,
        [Description("Number of days to include (default: 30)")] int days = 30)
    {
        try
        {
            var volume = messageRepo.GetEmailVolume(accountId, days);
            var totalMessages = volume.Sum(v => v.MessageCount);
            var totalSize = volume.Sum(v => v.TotalSizeBytes);
            var totalAttachments = volume.Sum(v => v.WithAttachments);

            return JsonSerializer.Serialize(new
            {
                account_id = accountId,
                period_days = days,
                summary = new
                {
                    total_messages = totalMessages,
                    total_size_bytes = totalSize,
                    total_size_mb = Math.Round(totalSize / (1024.0 * 1024.0), 2),
                    messages_with_attachments = totalAttachments,
                    folder_count = volume.Count
                },
                folders = volume.Select(v => new
                {
                    path = v.FolderPath,
                    message_count = v.MessageCount,
                    size_bytes = v.TotalSizeBytes,
                    size_mb = Math.Round(v.TotalSizeBytes / (1024.0 * 1024.0), 2),
                    with_attachments = v.WithAttachments
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReportTools.MailboxReport] {ex}");
            return JsonSerializer.Serialize(new { error = $"Report generation failed: {ex.Message}" }, JsonOptions);
        }
    }

    [McpServerTool, Description(
        "Get top email senders ranked by message count. " +
        "Falls back to all-time data if no messages exist in the specified date window.")]
    public string TopSenders(
        [Description("Account ID")] string accountId,
        [Description("Number of days to include (default: 30)")] int days = 30,
        [Description("Max number of senders to return (default: 10)")] int limit = 10)
    {
        try
        {
            var senders = messageRepo.GetTopSenders(accountId, days, limit);

            return JsonSerializer.Serialize(new
            {
                account_id = accountId,
                period_days = days,
                count = senders.Count,
                senders = senders.Select((s, i) => new
                {
                    rank = i + 1,
                    from_email = s.FromEmail,
                    message_count = s.MessageCount,
                    total_size_bytes = s.TotalSizeBytes,
                    total_size_mb = Math.Round(s.TotalSizeBytes / (1024.0 * 1024.0), 2)
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReportTools.TopSenders] {ex}");
            return JsonSerializer.Serialize(new { error = $"Top senders query failed: {ex.Message}" }, JsonOptions);
        }
    }

    [McpServerTool, Description(
        "Get category breakdown from LLM analysis results. Shows how many emails fall into each category (inbox/promotion/social/update).")]
    public string CategoryBreakdown(
        [Description("Account ID (optional, all accounts if omitted)")] string? accountId = null)
    {
        try
        {
            var breakdown = analysisRepo.GetCategoryBreakdown(accountId);

            return JsonSerializer.Serialize(new
            {
                account_id = accountId ?? "all",
                total_categorized = breakdown.Sum(b => b.Count),
                categories = breakdown.Select(b => new
                {
                    category = b.CategoryResult,
                    count = b.Count
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReportTools.CategoryBreakdown] {ex}");
            return JsonSerializer.Serialize(new { error = $"Category breakdown failed: {ex.Message}" }, JsonOptions);
        }
    }
}
