using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.Llm.Repositories;

/// <summary>Record for a row in the llm_usage table.</summary>
public record LlmUsageRecord(
    int Id, string Date, string Model,
    int TokensInput, int TokensOutput,
    double CostUsd, int RequestCount);

/// <summary>Aggregated usage summary.</summary>
public record UsageSummary(
    int TotalTokensInput, int TotalTokensOutput,
    double TotalCostUsd, int TotalRequests);

/// <summary>
/// Reads and writes the llm_usage table for tracking API usage and budgeting.
/// </summary>
public class LlmUsageRepository(AppDatabase db)
{
    /// <summary>
    /// Records usage for a model on a given date. Uses UPSERT to accumulate.
    /// </summary>
    public void RecordUsage(string date, string model, int tokensInput, int tokensOutput, double costUsd)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO llm_usage (date, model, tokens_input, tokens_output, cost_usd, request_count)
                VALUES ($date, $model, $tokensInput, $tokensOutput, $costUsd, 1)
                ON CONFLICT(date, model) DO UPDATE SET
                    tokens_input = tokens_input + $tokensInput,
                    tokens_output = tokens_output + $tokensOutput,
                    cost_usd = cost_usd + $costUsd,
                    request_count = request_count + 1;
                """;
            cmd.Parameters.AddWithValue("$date", date);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.Parameters.AddWithValue("$tokensInput", tokensInput);
            cmd.Parameters.AddWithValue("$tokensOutput", tokensOutput);
            cmd.Parameters.AddWithValue("$costUsd", costUsd);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Gets usage summary for a specific date (daily totals across all models).
    /// </summary>
    public UsageSummary GetDailySummary(string date)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(SUM(tokens_input), 0),
                COALESCE(SUM(tokens_output), 0),
                COALESCE(SUM(cost_usd), 0.0),
                COALESCE(SUM(request_count), 0)
            FROM llm_usage WHERE date = $date;
            """;
        cmd.Parameters.AddWithValue("$date", date);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new UsageSummary(
            reader.GetInt32(0), reader.GetInt32(1),
            reader.GetDouble(2), reader.GetInt32(3));
    }

    /// <summary>
    /// Gets usage summary for a month (YYYY-MM prefix match).
    /// </summary>
    public UsageSummary GetMonthlySummary(string yearMonth)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(SUM(tokens_input), 0),
                COALESCE(SUM(tokens_output), 0),
                COALESCE(SUM(cost_usd), 0.0),
                COALESCE(SUM(request_count), 0)
            FROM llm_usage WHERE date LIKE $prefix;
            """;
        cmd.Parameters.AddWithValue("$prefix", yearMonth + "%");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new UsageSummary(
            reader.GetInt32(0), reader.GetInt32(1),
            reader.GetDouble(2), reader.GetInt32(3));
    }

    /// <summary>
    /// Gets all usage records for a date range.
    /// </summary>
    public List<LlmUsageRecord> GetByDateRange(string startDate, string endDate)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, date, model, tokens_input, tokens_output, cost_usd, request_count
            FROM llm_usage WHERE date >= $start AND date <= $end
            ORDER BY date, model;
            """;
        cmd.Parameters.AddWithValue("$start", startDate);
        cmd.Parameters.AddWithValue("$end", endDate);
        using var reader = cmd.ExecuteReader();
        var list = new List<LlmUsageRecord>();
        while (reader.Read())
        {
            list.Add(new LlmUsageRecord(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4),
                reader.GetDouble(5), reader.GetInt32(6)));
        }
        return list;
    }
}
