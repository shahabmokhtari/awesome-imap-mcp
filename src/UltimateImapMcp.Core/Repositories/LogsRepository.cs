using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.Core.Repositories;

/// <summary>Record for a row in the logs table.</summary>
public record LogRecord(int Id, string Level, string Category, string Message,
    string? Exception, string? Metadata, string CreatedAt);

/// <summary>
/// Reads and writes the logs table for structured application logs.
/// </summary>
public class LogsRepository(AppDatabase db)
{
    /// <summary>
    /// Writes a single log entry.
    /// </summary>
    public void Write(string level, string category, string message,
        string? exception = null, string? metadata = null)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO logs (level, category, message, exception, metadata)
            VALUES ($level, $category, $message, $exception, $metadata);
            """;
        cmd.Parameters.AddWithValue("$level", level);
        cmd.Parameters.AddWithValue("$category", category);
        cmd.Parameters.AddWithValue("$message", message);
        cmd.Parameters.AddWithValue("$exception", (object?)exception ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata", (object?)metadata ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Batch-writes multiple log entries in a single transaction.
    /// </summary>
    public void WriteBatch(IReadOnlyList<(string Level, string Category, string Message,
        string? Exception, string? Metadata)> entries)
    {
        if (entries.Count == 0) return;

        var conn = db.GetWriteConnection();
        using var transaction = conn.BeginTransaction();
        try
        {
            foreach (var entry in entries)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO logs (level, category, message, exception, metadata)
                    VALUES ($level, $category, $message, $exception, $metadata);
                    """;
                cmd.Parameters.AddWithValue("$level", entry.Level);
                cmd.Parameters.AddWithValue("$category", entry.Category);
                cmd.Parameters.AddWithValue("$message", entry.Message);
                cmd.Parameters.AddWithValue("$exception", (object?)entry.Exception ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$metadata", (object?)entry.Metadata ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Queries log entries with optional filters.
    /// </summary>
    public List<LogRecord> Query(string? level = null, string? category = null,
        string? fromTime = null, string? toTime = null, string? search = null, int limit = 100)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();

        var where = "WHERE 1=1";
        if (level is not null) where += " AND level = $level";
        if (category is not null) where += " AND category = $category";
        if (fromTime is not null) where += " AND created_at >= $from";
        if (toTime is not null) where += " AND created_at <= $to";
        if (search is not null) where += " AND message LIKE $search";

        cmd.CommandText = $"""
            SELECT id, level, category, message, exception, metadata, created_at
            FROM logs
            {where}
            ORDER BY created_at DESC
            LIMIT $limit;
            """;

        if (level is not null) cmd.Parameters.AddWithValue("$level", level);
        if (category is not null) cmd.Parameters.AddWithValue("$category", category);
        if (fromTime is not null) cmd.Parameters.AddWithValue("$from", fromTime);
        if (toTime is not null) cmd.Parameters.AddWithValue("$to", toTime);
        if (search is not null) cmd.Parameters.AddWithValue("$search", $"%{search}%");
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        var list = new List<LogRecord>();
        while (reader.Read())
        {
            list.Add(new LogRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6)));
        }
        return list;
    }

    /// <summary>
    /// Prunes old log entries by level with different retention periods.
    /// </summary>
    public int Prune(int debugDays = 7, int infoDays = 30, int errorDays = 90)
    {
        var conn = db.GetWriteConnection();
        var total = 0;

        // Debug logs
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM logs
                WHERE level = 'Debug' AND created_at < datetime('now', $days);
                """;
            cmd.Parameters.AddWithValue("$days", $"-{debugDays} days");
            total += cmd.ExecuteNonQuery();
        }

        // Trace logs (same as debug)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM logs
                WHERE level = 'Trace' AND created_at < datetime('now', $days);
                """;
            cmd.Parameters.AddWithValue("$days", $"-{debugDays} days");
            total += cmd.ExecuteNonQuery();
        }

        // Info logs
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM logs
                WHERE level = 'Information' AND created_at < datetime('now', $days);
                """;
            cmd.Parameters.AddWithValue("$days", $"-{infoDays} days");
            total += cmd.ExecuteNonQuery();
        }

        // Warning logs
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM logs
                WHERE level = 'Warning' AND created_at < datetime('now', $days);
                """;
            cmd.Parameters.AddWithValue("$days", $"-{errorDays} days");
            total += cmd.ExecuteNonQuery();
        }

        // Error/Critical logs
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM logs
                WHERE level IN ('Error', 'Critical') AND created_at < datetime('now', $days);
                """;
            cmd.Parameters.AddWithValue("$days", $"-{errorDays} days");
            total += cmd.ExecuteNonQuery();
        }

        return total;
    }
}
