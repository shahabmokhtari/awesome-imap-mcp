using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Llm;
using UltimateImapMcp.Llm.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class AnalysisTools(
    IEmailAnalyzer analyzer,
    LlmAnalysisRepository analysisRepo,
    MessageRepository messageRepo,
    FolderRepository folderRepo,
    BudgetTracker budgetTracker)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description(
        "Analyze an email using LLM. Returns spam score, category, priority, summary, or custom analysis. " +
        "Provide either 'messageId' (database ID) alone, or 'accountId' + 'uid' (with optional 'folderId'). " +
        "If using in-context mode, returns the email data with instructions for you to perform the analysis.")]
    public async Task<string> AnalyzeEmail(
        [Description("Database message ID (if provided, accountId/uid/folderId are ignored)")] int? messageId = null,
        [Description("Account ID")] string? accountId = null,
        [Description("Message UID")] int? uid = null,
        [Description("Folder ID (integer, optional)")] int? folderId = null,
        [Description("Analysis type: spam_score, category, priority, summary, custom")] string type = "summary")
    {
        try
        {
            var msg = ResolveMessage(messageId, accountId, folderId, uid);
            if (msg is null)
                return Error("Message not found. Provide 'messageId' or 'accountId'+'uid'.");

            var analysisType = ParseAnalysisType(type);

            // Check budget
            if (analyzer.SupportsBackgroundAnalysis && !budgetTracker.CanSpend(1000))
                return Error("LLM budget exceeded. Check budget status with get_analysis_budget.");

            var email = new EmailContent(
                msg.Subject ?? "(no subject)",
                msg.FromAddress ?? "(unknown)",
                msg.BodyText,
                msg.Snippet);

            var result = await analyzer.AnalyzeAsync(email, analysisType).ConfigureAwait(false);

            // Store the result if background analysis was performed
            if (analyzer.SupportsBackgroundAnalysis)
            {
                analysisRepo.Upsert(msg.Id, type, result.ResultJson,
                    result.ModelUsed, result.TokensInput, result.TokensOutput,
                    result.CostUsd is not null ? (double)result.CostUsd : null);

                if (result.TokensInput is not null || result.TokensOutput is not null)
                {
                    budgetTracker.RecordUsage(
                        result.ModelUsed ?? "unknown",
                        result.TokensInput ?? 0,
                        result.TokensOutput ?? 0,
                        result.CostUsd);
                }
            }

            return JsonSerializer.Serialize(new
            {
                account_id = msg.AccountId,
                folder_id = msg.FolderId,
                uid = msg.Uid,
                analysis_type = type,
                background = analyzer.SupportsBackgroundAnalysis,
                model = result.ModelUsed,
                tokens_input = result.TokensInput,
                tokens_output = result.TokensOutput,
                cost_usd = result.CostUsd,
                result = JsonSerializer.Deserialize<JsonElement>(result.ResultJson)
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AnalysisTools.AnalyzeEmail] {ex}");
            return Error($"Analysis failed: {ex.Message}");
        }
    }

    [McpServerTool, Description(
        "Analyze multiple emails in a folder. Returns analysis results for up to `limit` messages.")]
    public async Task<string> AnalyzeFolder(
        [Description("Account ID")] string accountId,
        [Description("Folder path (e.g., INBOX)")] string folderPath,
        [Description("Analysis type: spam_score, category, priority, summary, custom")] string type = "summary",
        [Description("Max number of messages to analyze (default: 10)")] int limit = 10)
    {
        try
        {
            var folder = folderRepo.GetByPath(accountId, folderPath);
            if (folder is null)
                return Error($"Folder '{folderPath}' not found for account '{accountId}'.");

            var analysisType = ParseAnalysisType(type);
            var messages = messageRepo.GetByFolder(accountId, folder.Id, limit);

            if (messages.Count == 0)
                return Error($"No messages found in folder '{folderPath}'.");

            var results = new List<object>();
            var totalTokensIn = 0;
            var totalTokensOut = 0;
            var totalCost = 0m;

            foreach (var msg in messages)
            {
                if (analyzer.SupportsBackgroundAnalysis && !budgetTracker.CanSpend(1000))
                {
                    results.Add(new { uid = msg.Uid, status = "skipped", reason = "budget_exceeded" });
                    continue;
                }

                var email = new EmailContent(
                    msg.Subject ?? "(no subject)",
                    msg.FromAddress ?? "(unknown)",
                    msg.BodyText,
                    msg.Snippet);

                var result = await analyzer.AnalyzeAsync(email, analysisType).ConfigureAwait(false);

                if (analyzer.SupportsBackgroundAnalysis)
                {
                    analysisRepo.Upsert(msg.Id, type, result.ResultJson,
                        result.ModelUsed, result.TokensInput, result.TokensOutput,
                        result.CostUsd is not null ? (double)result.CostUsd : null);

                    if (result.TokensInput is not null || result.TokensOutput is not null)
                    {
                        budgetTracker.RecordUsage(
                            result.ModelUsed ?? "unknown",
                            result.TokensInput ?? 0,
                            result.TokensOutput ?? 0,
                            result.CostUsd);

                        totalTokensIn += result.TokensInput ?? 0;
                        totalTokensOut += result.TokensOutput ?? 0;
                        totalCost += result.CostUsd ?? 0;
                    }
                }

                results.Add(new
                {
                    uid = msg.Uid,
                    subject = msg.Subject,
                    status = "analyzed",
                    result = JsonSerializer.Deserialize<JsonElement>(result.ResultJson)
                });
            }

            return JsonSerializer.Serialize(new
            {
                account_id = accountId,
                folder = folderPath,
                analysis_type = type,
                analyzed = results.Count(r => r is not null),
                total_tokens_input = totalTokensIn,
                total_tokens_output = totalTokensOut,
                total_cost_usd = totalCost,
                results
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AnalysisTools.AnalyzeFolder] {ex}");
            return Error($"Folder analysis failed: {ex.Message}");
        }
    }

    [McpServerTool, Description(
        "Get stored LLM analysis results. Filter by account, type, or get all.")]
    public string GetAnalysis(
        [Description("Account ID (optional, all accounts if omitted)")] string? accountId = null,
        [Description("Analysis type filter (optional): spam_score, category, priority, summary, custom")] string? type = null,
        [Description("Max results to return (default: 50)")] int limit = 50)
    {
        try
        {
            var results = analysisRepo.GetByType(type, accountId, limit);

            return JsonSerializer.Serialize(new
            {
                count = results.Count,
                filter = new { account_id = accountId, type, limit },
                results = results.Select(r => new
                {
                    id = r.Id,
                    message_id = r.MessageId,
                    analysis_type = r.AnalysisType,
                    result = JsonSerializer.Deserialize<JsonElement>(r.Result),
                    model_used = r.ModelUsed,
                    tokens_input = r.TokensInput,
                    tokens_output = r.TokensOutput,
                    cost_usd = r.CostUsd,
                    analyzed_at = r.AnalyzedAt
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AnalysisTools.GetAnalysis] {ex}");
            return Error($"Failed to get analysis results: {ex.Message}");
        }
    }

    [McpServerTool, Description(
        "Get LLM usage budget status showing daily token usage and monthly cost against configured limits.")]
    public string GetAnalysisBudget()
    {
        var daily = budgetTracker.GetDailySummary();
        var monthly = budgetTracker.GetMonthlySummary();
        var status = budgetTracker.GetBudgetStatus();

        return JsonSerializer.Serialize(new
        {
            status,
            daily = new
            {
                tokens_input = daily.TotalTokensInput,
                tokens_output = daily.TotalTokensOutput,
                total_tokens = daily.TotalTokensInput + daily.TotalTokensOutput,
                cost_usd = daily.TotalCostUsd,
                requests = daily.TotalRequests
            },
            monthly = new
            {
                tokens_input = monthly.TotalTokensInput,
                tokens_output = monthly.TotalTokensOutput,
                total_tokens = monthly.TotalTokensInput + monthly.TotalTokensOutput,
                cost_usd = monthly.TotalCostUsd,
                requests = monthly.TotalRequests
            }
        }, JsonOptions);
    }

    private static AnalysisType ParseAnalysisType(string type) => type.ToLowerInvariant() switch
    {
        "spam_score" or "spamscore" => AnalysisType.SpamScore,
        "category" => AnalysisType.Category,
        "priority" => AnalysisType.Priority,
        "summary" => AnalysisType.Summary,
        "custom" => AnalysisType.Custom,
        _ => throw new ArgumentException($"Unknown analysis type: '{type}'. Use: spam_score, category, priority, summary, custom")
    };

    /// <summary>Resolves a message from various parameter combinations.</summary>
    private MessageRecord? ResolveMessage(int? messageId, string? accountId, int? folderId, int? uid)
    {
        if (messageId is not null)
            return messageRepo.GetById(messageId.Value);

        if (string.IsNullOrEmpty(accountId) || uid is null)
            return null;

        if (folderId is not null)
            return messageRepo.GetByUid(accountId, folderId.Value, uid.Value);

        // Search across all folders for this account+uid
        foreach (var folder in folderRepo.GetByAccount(accountId))
        {
            var msg = messageRepo.GetByUid(accountId, folder.Id, uid.Value);
            if (msg is not null) return msg;
        }

        return null;
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, JsonOptions);
}
