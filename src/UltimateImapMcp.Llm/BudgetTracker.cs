using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Llm.Repositories;

namespace UltimateImapMcp.Llm;

/// <summary>
/// Tracks LLM API usage against configured daily token and monthly cost budgets.
/// Checked before every LLM call to enforce limits.
/// </summary>
public class BudgetTracker(LlmUsageRepository usageRepo, LlmConfig config)
{
    /// <summary>
    /// Checks whether the estimated tokens can be spent within budget limits.
    /// Returns true if within budget or budgets are disabled (set to 0).
    /// </summary>
    public bool CanSpend(int estimatedTokens)
    {
        // Check daily token budget
        if (config.DailyTokenBudget > 0)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var daily = usageRepo.GetDailySummary(today);
            var totalUsed = daily.TotalTokensInput + daily.TotalTokensOutput;
            if (totalUsed + estimatedTokens > config.DailyTokenBudget)
                return false;
        }

        // Check monthly cost limit
        if (config.MonthlyCostLimit > 0)
        {
            var yearMonth = DateTime.UtcNow.ToString("yyyy-MM");
            var monthly = usageRepo.GetMonthlySummary(yearMonth);
            if (monthly.TotalCostUsd >= config.MonthlyCostLimit)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Records usage for the current date.
    /// </summary>
    public void RecordUsage(string model, int tokensInput, int tokensOutput, decimal? costUsd)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        usageRepo.RecordUsage(today, model, tokensInput, tokensOutput, (double)(costUsd ?? 0m));
    }

    /// <summary>
    /// Gets usage summary for the current day.
    /// </summary>
    public UsageSummary GetDailySummary()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return usageRepo.GetDailySummary(today);
    }

    /// <summary>
    /// Gets usage summary for the current month.
    /// </summary>
    public UsageSummary GetMonthlySummary()
    {
        var yearMonth = DateTime.UtcNow.ToString("yyyy-MM");
        return usageRepo.GetMonthlySummary(yearMonth);
    }

    /// <summary>
    /// Gets a human-readable budget status message.
    /// </summary>
    public string GetBudgetStatus()
    {
        var daily = GetDailySummary();
        var monthly = GetMonthlySummary();

        var dailyTokens = daily.TotalTokensInput + daily.TotalTokensOutput;
        var dailyLimit = config.DailyTokenBudget > 0
            ? $"{dailyTokens:N0} / {config.DailyTokenBudget:N0}"
            : $"{dailyTokens:N0} (no limit)";

        var monthlyLimit = config.MonthlyCostLimit > 0
            ? $"${monthly.TotalCostUsd:F4} / ${config.MonthlyCostLimit:F2}"
            : $"${monthly.TotalCostUsd:F4} (no limit)";

        return $"Daily tokens: {dailyLimit} | Monthly cost: {monthlyLimit} | " +
               $"Requests today: {daily.TotalRequests} | Requests this month: {monthly.TotalRequests}";
    }
}
