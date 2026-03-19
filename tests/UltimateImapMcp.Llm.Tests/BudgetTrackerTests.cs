using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Llm;
using UltimateImapMcp.Llm.Repositories;

namespace UltimateImapMcp.Llm.Tests;

public class BudgetTrackerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly LlmUsageRepository _usageRepo;

    public BudgetTrackerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_budget_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _usageRepo = new LlmUsageRepository(_db);
    }

    [Fact]
    public void CanSpend_NoBudgetSet_ReturnsTrue()
    {
        var config = new LlmConfig { DailyTokenBudget = 0, MonthlyCostLimit = 0 };
        var tracker = new BudgetTracker(_usageRepo, config);

        Assert.True(tracker.CanSpend(10000));
    }

    [Fact]
    public void CanSpend_UnderDailyLimit_ReturnsTrue()
    {
        var config = new LlmConfig { DailyTokenBudget = 100000, MonthlyCostLimit = 0 };
        var tracker = new BudgetTracker(_usageRepo, config);

        Assert.True(tracker.CanSpend(1000));
    }

    [Fact]
    public void CanSpend_OverDailyLimit_ReturnsFalse()
    {
        var config = new LlmConfig { DailyTokenBudget = 1000, MonthlyCostLimit = 0 };
        var tracker = new BudgetTracker(_usageRepo, config);

        // Record usage that nearly fills the daily budget
        tracker.RecordUsage("gpt-4o-mini", 400, 400, 0.001m);

        // Trying to spend 300 more tokens would exceed 1000 (800 used + 300 = 1100)
        Assert.False(tracker.CanSpend(300));
    }

    [Fact]
    public void CanSpend_ExactlyAtDailyLimit_ReturnsFalse()
    {
        var config = new LlmConfig { DailyTokenBudget = 1000, MonthlyCostLimit = 0 };
        var tracker = new BudgetTracker(_usageRepo, config);

        tracker.RecordUsage("gpt-4o-mini", 500, 500, 0.001m);

        // 1000 used, trying to spend 1 more
        Assert.False(tracker.CanSpend(1));
    }

    [Fact]
    public void CanSpend_UnderMonthlyLimit_ReturnsTrue()
    {
        var config = new LlmConfig { DailyTokenBudget = 0, MonthlyCostLimit = 10.0 };
        var tracker = new BudgetTracker(_usageRepo, config);

        tracker.RecordUsage("gpt-4o", 1000, 500, 0.05m);

        Assert.True(tracker.CanSpend(1000));
    }

    [Fact]
    public void CanSpend_OverMonthlyLimit_ReturnsFalse()
    {
        var config = new LlmConfig { DailyTokenBudget = 0, MonthlyCostLimit = 0.10 };
        var tracker = new BudgetTracker(_usageRepo, config);

        tracker.RecordUsage("gpt-4o", 10000, 5000, 0.15m);

        Assert.False(tracker.CanSpend(1));
    }

    [Fact]
    public void CanSpend_BothLimits_DailyExceeded_ReturnsFalse()
    {
        var config = new LlmConfig { DailyTokenBudget = 500, MonthlyCostLimit = 100.0 };
        var tracker = new BudgetTracker(_usageRepo, config);

        tracker.RecordUsage("gpt-4o-mini", 250, 250, 0.001m);

        // Daily limit would be exceeded (500 + 100 > 500) even though monthly is fine
        Assert.False(tracker.CanSpend(100));
    }

    [Fact]
    public void RecordUsage_AccumulatesMultipleCalls()
    {
        var config = new LlmConfig();
        var tracker = new BudgetTracker(_usageRepo, config);

        tracker.RecordUsage("gpt-4o-mini", 100, 50, 0.01m);
        tracker.RecordUsage("gpt-4o-mini", 200, 100, 0.02m);

        var summary = tracker.GetDailySummary();
        Assert.Equal(300, summary.TotalTokensInput);
        Assert.Equal(150, summary.TotalTokensOutput);
        Assert.Equal(2, summary.TotalRequests);
    }

    [Fact]
    public void GetDailySummary_NoUsage_ReturnsZeros()
    {
        var config = new LlmConfig();
        var tracker = new BudgetTracker(_usageRepo, config);

        var summary = tracker.GetDailySummary();
        Assert.Equal(0, summary.TotalTokensInput);
        Assert.Equal(0, summary.TotalTokensOutput);
        Assert.Equal(0.0, summary.TotalCostUsd);
        Assert.Equal(0, summary.TotalRequests);
    }

    [Fact]
    public void GetMonthlySummary_AggregatesAcrossModels()
    {
        var config = new LlmConfig();
        var tracker = new BudgetTracker(_usageRepo, config);

        tracker.RecordUsage("gpt-4o-mini", 100, 50, 0.01m);
        tracker.RecordUsage("claude-haiku", 200, 100, 0.02m);

        var summary = tracker.GetMonthlySummary();
        Assert.Equal(300, summary.TotalTokensInput);
        Assert.Equal(150, summary.TotalTokensOutput);
        Assert.Equal(2, summary.TotalRequests);
    }

    [Fact]
    public void GetBudgetStatus_ReturnsFormattedString()
    {
        var config = new LlmConfig { DailyTokenBudget = 10000, MonthlyCostLimit = 5.0 };
        var tracker = new BudgetTracker(_usageRepo, config);

        tracker.RecordUsage("gpt-4o-mini", 100, 50, 0.001m);

        var status = tracker.GetBudgetStatus();
        Assert.Contains("Daily tokens:", status);
        Assert.Contains("Monthly cost:", status);
        Assert.Contains("10,000", status);  // daily limit
        Assert.Contains("$5.00", status);    // monthly limit
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
