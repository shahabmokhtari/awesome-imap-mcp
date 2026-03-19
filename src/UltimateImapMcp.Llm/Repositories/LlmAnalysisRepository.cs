using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.Llm.Repositories;

/// <summary>Record for a row in the llm_analysis table.</summary>
public record LlmAnalysisRecord(
    int Id, int MessageId, string AnalysisType, string Result,
    string? ModelUsed, int? TokensInput, int? TokensOutput,
    double? CostUsd, string AnalyzedAt);

/// <summary>Category breakdown aggregate record.</summary>
public record CategoryBreakdownRecord(string CategoryResult, int Count);

/// <summary>
/// Reads and writes the llm_analysis table for email analysis results.
/// </summary>
public class LlmAnalysisRepository(AppDatabase db)
{
    /// <summary>
    /// Inserts or replaces an analysis result for a message.
    /// </summary>
    public void Upsert(int messageId, string analysisType, string result,
        string? modelUsed, int? tokensInput, int? tokensOutput, double? costUsd)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO llm_analysis (message_id, analysis_type, result, model_used, tokens_input, tokens_output, cost_usd)
                VALUES ($messageId, $analysisType, $result, $modelUsed, $tokensInput, $tokensOutput, $costUsd)
                ON CONFLICT(message_id, analysis_type) DO UPDATE SET
                    result = $result,
                    model_used = $modelUsed,
                    tokens_input = $tokensInput,
                    tokens_output = $tokensOutput,
                    cost_usd = $costUsd,
                    analyzed_at = datetime('now');
                """;
            cmd.Parameters.AddWithValue("$messageId", messageId);
            cmd.Parameters.AddWithValue("$analysisType", analysisType);
            cmd.Parameters.AddWithValue("$result", result);
            cmd.Parameters.AddWithValue("$modelUsed", (object?)modelUsed ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tokensInput", (object?)tokensInput ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tokensOutput", (object?)tokensOutput ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$costUsd", (object?)costUsd ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Gets all analysis results for a specific message.
    /// </summary>
    public List<LlmAnalysisRecord> GetByMessageId(int messageId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, message_id, analysis_type, result, model_used, tokens_input, tokens_output, cost_usd, analyzed_at
            FROM llm_analysis WHERE message_id = $messageId
            ORDER BY analyzed_at DESC;
            """;
        cmd.Parameters.AddWithValue("$messageId", messageId);
        return ReadRecords(cmd);
    }

    /// <summary>
    /// Gets a specific analysis for a message.
    /// </summary>
    public LlmAnalysisRecord? GetByMessageAndType(int messageId, string analysisType)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, message_id, analysis_type, result, model_used, tokens_input, tokens_output, cost_usd, analyzed_at
            FROM llm_analysis WHERE message_id = $messageId AND analysis_type = $type;
            """;
        cmd.Parameters.AddWithValue("$messageId", messageId);
        cmd.Parameters.AddWithValue("$type", analysisType);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    /// <summary>
    /// Gets analysis results filtered by type, with optional account filter.
    /// </summary>
    public List<LlmAnalysisRecord> GetByType(string? analysisType = null, string? accountId = null, int limit = 50)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();

        var where = "WHERE 1=1";
        if (analysisType is not null)
            where += " AND a.analysis_type = $type";
        if (accountId is not null)
            where += " AND m.account_id = $accountId";

        cmd.CommandText = $"""
            SELECT a.id, a.message_id, a.analysis_type, a.result, a.model_used,
                   a.tokens_input, a.tokens_output, a.cost_usd, a.analyzed_at
            FROM llm_analysis a
            JOIN messages m ON m.id = a.message_id
            {where}
            ORDER BY a.analyzed_at DESC
            LIMIT $limit;
            """;

        if (analysisType is not null)
            cmd.Parameters.AddWithValue("$type", analysisType);
        if (accountId is not null)
            cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$limit", limit);

        return ReadRecords(cmd);
    }

    /// <summary>
    /// Gets category breakdown aggregated from llm_analysis category results.
    /// Returns (category_value, count) pairs for an account.
    /// </summary>
    public List<CategoryBreakdownRecord> GetCategoryBreakdown(string? accountId = null)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();

        var join = accountId is not null ? "JOIN messages m ON m.id = a.message_id" : "";
        var where = accountId is not null ? "AND m.account_id = $accountId" : "";

        cmd.CommandText = $"""
            SELECT a.result, COUNT(*) as cnt
            FROM llm_analysis a
            {join}
            WHERE a.analysis_type = 'category'
            {where}
            GROUP BY a.result
            ORDER BY cnt DESC;
            """;

        if (accountId is not null)
            cmd.Parameters.AddWithValue("$accountId", accountId);

        using var reader = cmd.ExecuteReader();
        var list = new List<CategoryBreakdownRecord>();
        while (reader.Read())
        {
            list.Add(new CategoryBreakdownRecord(reader.GetString(0), reader.GetInt32(1)));
        }
        return list;
    }

    /// <summary>
    /// Deletes analysis for a message.
    /// </summary>
    public int DeleteByMessageId(int messageId)
    {
        return db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM llm_analysis WHERE message_id = $messageId;";
            cmd.Parameters.AddWithValue("$messageId", messageId);
            return cmd.ExecuteNonQuery();
        });
    }

    private static List<LlmAnalysisRecord> ReadRecords(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<LlmAnalysisRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    private static LlmAnalysisRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Id: r.GetInt32(0),
        MessageId: r.GetInt32(1),
        AnalysisType: r.GetString(2),
        Result: r.GetString(3),
        ModelUsed: r.IsDBNull(4) ? null : r.GetString(4),
        TokensInput: r.IsDBNull(5) ? null : r.GetInt32(5),
        TokensOutput: r.IsDBNull(6) ? null : r.GetInt32(6),
        CostUsd: r.IsDBNull(7) ? null : r.GetDouble(7),
        AnalyzedAt: r.GetString(8));
}
