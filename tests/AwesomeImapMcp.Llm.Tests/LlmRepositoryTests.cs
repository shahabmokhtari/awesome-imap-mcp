using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.Llm.Repositories;

namespace AwesomeImapMcp.Llm.Tests;

public class LlmRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly LlmUsageRepository _usageRepo;
    private readonly LlmAnalysisRepository _analysisRepo;

    public LlmRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_llm_repo_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _usageRepo = new LlmUsageRepository(_db);
        _analysisRepo = new LlmAnalysisRepository(_db);

        // Seed test data: folder, message (accounts are in accounts.json, not SQLite)
        var conn = _db.GetWriteConnection();
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "INSERT INTO folders (account_id, path, delimiter) VALUES ('test', 'INBOX', '/');";
        cmd2.ExecuteNonQuery();

        using var cmd3 = conn.CreateCommand();
        cmd3.CommandText = "INSERT INTO messages (account_id, folder_id, uid, subject, from_address, date, body_fetched) VALUES ('test', 1, 1, 'Test Subject', 'sender@test.com', '2024-01-01', 0);";
        cmd3.ExecuteNonQuery();
    }

    // ---- LlmUsageRepository Tests ----

    [Fact]
    public void UsageRepo_RecordUsage_InsertsRow()
    {
        _usageRepo.RecordUsage("2024-01-15", "gpt-4o-mini", 100, 50, 0.01);
        var summary = _usageRepo.GetDailySummary("2024-01-15");

        Assert.Equal(100, summary.TotalTokensInput);
        Assert.Equal(50, summary.TotalTokensOutput);
        Assert.Equal(0.01, summary.TotalCostUsd, 4);
        Assert.Equal(1, summary.TotalRequests);
    }

    [Fact]
    public void UsageRepo_RecordUsage_Upserts_SameModelSameDate()
    {
        _usageRepo.RecordUsage("2024-01-15", "gpt-4o-mini", 100, 50, 0.01);
        _usageRepo.RecordUsage("2024-01-15", "gpt-4o-mini", 200, 100, 0.02);

        var summary = _usageRepo.GetDailySummary("2024-01-15");
        Assert.Equal(300, summary.TotalTokensInput);
        Assert.Equal(150, summary.TotalTokensOutput);
        Assert.Equal(2, summary.TotalRequests);
    }

    [Fact]
    public void UsageRepo_GetMonthlySummary_AggregatesMonth()
    {
        _usageRepo.RecordUsage("2024-01-15", "gpt-4o", 100, 50, 0.05);
        _usageRepo.RecordUsage("2024-01-20", "gpt-4o", 200, 100, 0.10);
        _usageRepo.RecordUsage("2024-02-01", "gpt-4o", 999, 999, 9.99); // different month

        var summary = _usageRepo.GetMonthlySummary("2024-01");
        Assert.Equal(300, summary.TotalTokensInput);
        Assert.Equal(150, summary.TotalTokensOutput);
        Assert.Equal(2, summary.TotalRequests);
    }

    [Fact]
    public void UsageRepo_GetByDateRange_ReturnsCorrectRange()
    {
        _usageRepo.RecordUsage("2024-01-10", "gpt-4o", 100, 50, 0.01);
        _usageRepo.RecordUsage("2024-01-15", "gpt-4o", 200, 100, 0.02);
        _usageRepo.RecordUsage("2024-01-20", "gpt-4o", 300, 150, 0.03);

        var records = _usageRepo.GetByDateRange("2024-01-10", "2024-01-15");
        Assert.Equal(2, records.Count);
    }

    // ---- LlmAnalysisRepository Tests ----

    [Fact]
    public void AnalysisRepo_Upsert_InsertsNewAnalysis()
    {
        _analysisRepo.Upsert(1, "spam_score", """{"score":23}""", "gpt-4o-mini", 100, 50, 0.01);

        var result = _analysisRepo.GetByMessageAndType(1, "spam_score");
        Assert.NotNull(result);
        Assert.Equal("""{"score":23}""", result.Result);
        Assert.Equal("gpt-4o-mini", result.ModelUsed);
    }

    [Fact]
    public void AnalysisRepo_Upsert_UpdatesExisting()
    {
        _analysisRepo.Upsert(1, "spam_score", """{"score":23}""", "gpt-4o-mini", 100, 50, 0.01);
        _analysisRepo.Upsert(1, "spam_score", """{"score":85}""", "gpt-4o", 200, 100, 0.05);

        var result = _analysisRepo.GetByMessageAndType(1, "spam_score");
        Assert.NotNull(result);
        Assert.Equal("""{"score":85}""", result.Result);
        Assert.Equal("gpt-4o", result.ModelUsed);
    }

    [Fact]
    public void AnalysisRepo_GetByMessageId_ReturnsAll()
    {
        _analysisRepo.Upsert(1, "spam_score", """{"score":23}""", null, null, null, null);
        _analysisRepo.Upsert(1, "category", """{"category":"work"}""", null, null, null, null);

        var results = _analysisRepo.GetByMessageId(1);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void AnalysisRepo_GetByType_FiltersCorrectly()
    {
        _analysisRepo.Upsert(1, "spam_score", """{"score":23}""", null, null, null, null);
        _analysisRepo.Upsert(1, "category", """{"category":"work"}""", null, null, null, null);

        var results = _analysisRepo.GetByType("spam_score");
        Assert.Single(results);
        Assert.Equal("spam_score", results[0].AnalysisType);
    }

    [Fact]
    public void AnalysisRepo_GetByType_WithAccount_FiltersCorrectly()
    {
        _analysisRepo.Upsert(1, "spam_score", """{"score":23}""", null, null, null, null);

        var results = _analysisRepo.GetByType("spam_score", "test");
        Assert.Single(results);

        var noResults = _analysisRepo.GetByType("spam_score", "nonexistent");
        Assert.Empty(noResults);
    }

    [Fact]
    public void AnalysisRepo_GetCategoryBreakdown_AggregatesCategories()
    {
        // Need multiple messages for breakdown
        var conn = _db.GetWriteConnection();
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "INSERT INTO messages (account_id, folder_id, uid, subject, from_address, date, body_fetched) VALUES ('test', 1, 2, 'Subject 2', 'sender2@test.com', '2024-01-02', 0);";
        cmd2.ExecuteNonQuery();

        using var cmd3 = conn.CreateCommand();
        cmd3.CommandText = "INSERT INTO messages (account_id, folder_id, uid, subject, from_address, date, body_fetched) VALUES ('test', 1, 3, 'Subject 3', 'sender3@test.com', '2024-01-03', 0);";
        cmd3.ExecuteNonQuery();

        _analysisRepo.Upsert(1, "category", """{"category":"work"}""", null, null, null, null);
        _analysisRepo.Upsert(2, "category", """{"category":"work"}""", null, null, null, null);
        _analysisRepo.Upsert(3, "category", """{"category":"personal"}""", null, null, null, null);

        var breakdown = _analysisRepo.GetCategoryBreakdown("test");
        Assert.Equal(2, breakdown.Count);

        // Work has 2, personal has 1, ordered by count DESC
        Assert.Equal("""{"category":"work"}""", breakdown[0].CategoryResult);
        Assert.Equal(2, breakdown[0].Count);
        Assert.Equal("""{"category":"personal"}""", breakdown[1].CategoryResult);
        Assert.Equal(1, breakdown[1].Count);
    }

    [Fact]
    public void AnalysisRepo_GetCategoryBreakdown_AllAccounts()
    {
        _analysisRepo.Upsert(1, "category", """{"category":"newsletter"}""", null, null, null, null);

        var breakdown = _analysisRepo.GetCategoryBreakdown(); // no account filter
        Assert.Single(breakdown);
        Assert.Equal(1, breakdown[0].Count);
    }

    [Fact]
    public void AnalysisRepo_GetCategoryBreakdown_EmptyResult()
    {
        var breakdown = _analysisRepo.GetCategoryBreakdown("test");
        Assert.Empty(breakdown);
    }

    [Fact]
    public void AnalysisRepo_DeleteByMessageId_RemovesRecords()
    {
        _analysisRepo.Upsert(1, "spam_score", """{"score":23}""", null, null, null, null);
        _analysisRepo.Upsert(1, "category", """{"category":"work"}""", null, null, null, null);

        var deleted = _analysisRepo.DeleteByMessageId(1);
        Assert.Equal(2, deleted);

        var results = _analysisRepo.GetByMessageId(1);
        Assert.Empty(results);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
