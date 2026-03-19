using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.Core.Repositories;

/// <summary>Record for a row in the metrics table.</summary>
public record MetricRecord(int Id, string Name, double Value, string? Tags, string RecordedAt);

/// <summary>
/// Reads and writes the metrics table for internal observability data.
/// </summary>
public class MetricsRepository(AppDatabase db)
{
    /// <summary>
    /// Records a single metric data point.
    /// </summary>
    public void Record(string name, double value, string? tags = null)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO metrics (name, value, tags)
            VALUES ($name, $value, $tags);
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$tags", (object?)tags ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Queries metrics by name within a time range.
    /// </summary>
    public List<MetricRecord> Query(string name, string? fromTime = null, string? toTime = null, int limit = 100)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();

        var where = "WHERE name = $name";
        if (fromTime is not null) where += " AND recorded_at >= $from";
        if (toTime is not null) where += " AND recorded_at <= $to";

        cmd.CommandText = $"""
            SELECT id, name, value, tags, recorded_at
            FROM metrics
            {where}
            ORDER BY recorded_at DESC
            LIMIT $limit;
            """;

        cmd.Parameters.AddWithValue("$name", name);
        if (fromTime is not null) cmd.Parameters.AddWithValue("$from", fromTime);
        if (toTime is not null) cmd.Parameters.AddWithValue("$to", toTime);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        var list = new List<MetricRecord>();
        while (reader.Read())
        {
            list.Add(new MetricRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)));
        }
        return list;
    }

    /// <summary>
    /// Queries all metrics within a time range (any name).
    /// </summary>
    public List<MetricRecord> QueryAll(string? fromTime = null, string? toTime = null, int limit = 100)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();

        var where = "WHERE 1=1";
        if (fromTime is not null) where += " AND recorded_at >= $from";
        if (toTime is not null) where += " AND recorded_at <= $to";

        cmd.CommandText = $"""
            SELECT id, name, value, tags, recorded_at
            FROM metrics
            {where}
            ORDER BY recorded_at DESC
            LIMIT $limit;
            """;

        if (fromTime is not null) cmd.Parameters.AddWithValue("$from", fromTime);
        if (toTime is not null) cmd.Parameters.AddWithValue("$to", toTime);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        var list = new List<MetricRecord>();
        while (reader.Read())
        {
            list.Add(new MetricRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)));
        }
        return list;
    }

    /// <summary>
    /// Deletes metrics older than the specified number of days.
    /// Returns the number of rows deleted.
    /// </summary>
    public int Prune(int retentionDays)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM metrics
            WHERE recorded_at < datetime('now', $days);
            """;
        cmd.Parameters.AddWithValue("$days", $"-{retentionDays} days");
        return cmd.ExecuteNonQuery();
    }
}
