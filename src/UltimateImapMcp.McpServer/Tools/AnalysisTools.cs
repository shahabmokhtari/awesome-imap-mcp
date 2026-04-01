using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Email;
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
    IEmailBackendFactory backendFactory,
    BudgetTracker budgetTracker,
    AppConfig config,
    ILogger<AnalysisTools> logger)
{
    [McpServerTool, Description(
        "Analyze an email using the configured LLM provider. " +
        "Types: 'summary' (key points), 'spam_score' (0-100 spam likelihood), 'category' (inbox/promotion/social/update), " +
        "'priority' (high/medium/low), 'custom' (freeform). " +
        "Provide messageId alone, or accountId + uid.")]
    public async Task<string> AnalyzeEmail(
        [Description("Database message ID (if provided, accountId/uid/folderId are ignored)")] int? messageId = null,
        [Description("Account ID")] string? accountId = null,
        [Description("Message UID")] int? uid = null,
        [Description("Folder ID (integer, optional)")] int? folderId = null,
        [Description("Analysis type: spam_score, category, priority, summary, custom")] string type = "summary",
        [Description("Custom analysis instructions (used when type='custom', or overrides the default prompt for any type)")] string? customPrompt = null)
    {
        return await McpJsonDefaults.LogToolCallAsync(logger, "analyze_email",
            new Dictionary<string, object?> { ["messageId"] = messageId, ["accountId"] = accountId, ["uid"] = uid, ["type"] = type },
            async () =>
            {
                try
                {
                    var msg = messageRepo.Resolve(messageId, accountId, folderId, uid, folderRepo);
                    if (msg is null)
                        return McpJsonDefaults.Error("Message not found. Provide 'messageId' or 'accountId'+'uid'.");

                    // Auto-fetch body if not yet cached — analysis is much better with full body
                    if (!msg.BodyFetched)
                    {
                        try
                        {
                            var folder = folderRepo.GetByAccount(msg.AccountId)
                                .FirstOrDefault(f => f.Id == msg.FolderId);
                            if (folder is not null)
                            {
                                await using var backend = backendFactory.CreateSyncBackend(msg.AccountId);
                                await backend.FetchMessageBodyAsync(msg.AccountId, folder.Path, msg.Uid).ConfigureAwait(false);
                                msg = messageRepo.GetById(msg.Id) ?? msg;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning(ex, "Failed to fetch body for analysis, proceeding with snippet");
                        }
                    }

                    var analysisType = ParseAnalysisType(type);

                    // Check budget
                    if (analyzer.SupportsBackgroundAnalysis && !budgetTracker.CanSpend(1000))
                        return McpJsonDefaults.Error("LLM budget exceeded. Check budget status with get_analysis_budget.");

                    var email = new EmailContent(
                        msg.Subject ?? "(no subject)",
                        msg.FromAddress ?? "(unknown)",
                        msg.BodyText,
                        msg.Snippet);

                    // If custom prompt provided, prepend it to the email body context
                    if (!string.IsNullOrEmpty(customPrompt))
                    {
                        email = new EmailContent(
                            msg.Subject ?? "(no subject)",
                            msg.FromAddress ?? "(unknown)",
                            $"[Analysis Instructions: {customPrompt}]\n\n{msg.BodyText}",
                            msg.Snippet);
                    }

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
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "AnalyzeEmail failed");
                    return McpJsonDefaults.Error($"Analysis failed: {ex.Message}");
                }
            }, config);
    }

    [McpServerTool, Description(
        "Batch-analyze emails in a folder using LLM. Processes up to 'limit' messages sequentially. " +
        "Respects the configured daily token budget.")]
    public async Task<string> AnalyzeFolder(
        [Description("Account ID")] string accountId,
        [Description("Folder path (e.g., INBOX)")] string folderPath,
        [Description("Analysis type: spam_score, category, priority, summary, custom")] string type = "summary",
        [Description("Max number of messages to analyze (default: 10)")] int limit = 10,
        [Description("Custom analysis instructions (used when type='custom')")] string? customPrompt = null)
    {
        return await McpJsonDefaults.LogToolCallAsync(logger, "analyze_folder",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["folderPath"] = folderPath, ["type"] = type, ["limit"] = limit },
            async () =>
            {
                try
                {
                    var folder = folderRepo.GetByPath(accountId, folderPath);
                    if (folder is null)
                        return McpJsonDefaults.Error($"Folder '{folderPath}' not found for account '{accountId}'.");

                    var analysisType = ParseAnalysisType(type);
                    var messages = messageRepo.GetByFolder(accountId, folder.Id, limit);

                    if (messages.Count == 0)
                        return McpJsonDefaults.Error($"No messages found in folder '{folderPath}'.");

                    var results = new List<object>();
                    var totalTokensIn = 0;
                    var totalTokensOut = 0;
                    var totalCost = 0m;

                    foreach (var msg0 in messages)
                    {
                        var msg = msg0;
                        if (analyzer.SupportsBackgroundAnalysis && !budgetTracker.CanSpend(1000))
                        {
                            results.Add(new { uid = msg.Uid, status = "skipped", reason = "budget_exceeded" });
                            continue;
                        }

                        // Auto-fetch body if not yet cached
                        if (!msg.BodyFetched)
                        {
                            try
                            {
                                await using var backend = backendFactory.CreateSyncBackend(msg.AccountId);
                                await backend.FetchMessageBodyAsync(msg.AccountId, folder.Path, msg.Uid).ConfigureAwait(false);
                                msg = messageRepo.GetById(msg.Id) ?? msg;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogWarning(ex, "Failed to fetch body for UID {Uid}, proceeding with snippet", msg.Uid);
                            }
                        }

                        var email = new EmailContent(
                            msg.Subject ?? "(no subject)",
                            msg.FromAddress ?? "(unknown)",
                            msg.BodyText,
                            msg.Snippet);

                        // If custom prompt provided, prepend it to the email body context
                        if (!string.IsNullOrEmpty(customPrompt))
                        {
                            email = new EmailContent(
                                msg.Subject ?? "(no subject)",
                                msg.FromAddress ?? "(unknown)",
                                $"[Analysis Instructions: {customPrompt}]\n\n{msg.BodyText}",
                                msg.Snippet);
                        }

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
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "AnalyzeFolder failed");
                    return McpJsonDefaults.Error($"Folder analysis failed: {ex.Message}");
                }
            }, config);
    }

    [McpServerTool, Description(
        "Get stored LLM analysis results. Filter by account, type, or get all cached analyses.")]
    public string GetAnalysis(
        [Description("Account ID (optional, all accounts if omitted)")] string? accountId = null,
        [Description("Analysis type filter (optional): spam_score, category, priority, summary, custom")] string? type = null,
        [Description("Max results to return (default: 50)")] int limit = 50)
    {
        return McpJsonDefaults.LogToolCall(logger, "get_analysis",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["type"] = type, ["limit"] = limit },
            () =>
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
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "GetAnalysis failed");
                    return McpJsonDefaults.Error($"Failed to get analysis results: {ex.Message}");
                }
            }, config);
    }

    [McpServerTool, Description(
        "Get LLM usage budget status showing daily token usage and monthly cost against configured limits.")]
    public string GetAnalysisBudget()
    {
        return McpJsonDefaults.LogToolCall(logger, "get_analysis_budget",
            new Dictionary<string, object?>(),
            () =>
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
                }, McpJsonDefaults.Options);
            }, config);
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
}
