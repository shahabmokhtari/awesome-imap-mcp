using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.Core.Repositories;

/// <summary>Record for a row in the logs table.</summary>
public record LogRecord(int Id, string Level, string Category, string Message,
    string? Exception, string? Metadata, string CreatedAt, string Scope, string InstanceId);

/// <summary>
/// Reads and writes the logs table for structured application logs.
/// </summary>
public class LogsRepository(AppDatabase db)
{
    /// <summary>
    /// Writes a single log entry.
    /// </summary>
    public void Write(string level, string category, string message,
        string? exception = null, string? metadata = null,
        string scope = "system", string instanceId = "")
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO logs (level, category, message, exception, metadata, scope, instance_id)
                VALUES ($level, $category, $message, $exception, $metadata, $scope, $instance_id);
                """;
            cmd.Parameters.AddWithValue("$level", level);
            cmd.Parameters.AddWithValue("$category", category);
            cmd.Parameters.AddWithValue("$message", message);
            cmd.Parameters.AddWithValue("$exception", (object?)exception ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$metadata", (object?)metadata ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$scope", scope);
            cmd.Parameters.AddWithValue("$instance_id", instanceId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Batch-writes multiple log entries in a single transaction.
    /// </summary>
    public void WriteBatch(IReadOnlyList<(string Level, string Category, string Message,
        string? Exception, string? Metadata, string Scope, string InstanceId)> entries)
    {
        if (entries.Count == 0) return;

        db.ExecuteWrite(conn =>
        {
            using var transaction = conn.BeginTransaction();
            try
            {
                foreach (var entry in entries)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = """
                        INSERT INTO logs (level, category, message, exception, metadata, scope, instance_id)
                        VALUES ($level, $category, $message, $exception, $metadata, $scope, $instance_id);
                        """;
                    cmd.Parameters.AddWithValue("$level", entry.Level);
                    cmd.Parameters.AddWithValue("$category", entry.Category);
                    cmd.Parameters.AddWithValue("$message", entry.Message);
                    cmd.Parameters.AddWithValue("$exception", (object?)entry.Exception ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$metadata", (object?)entry.Metadata ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$scope", entry.Scope);
                    cmd.Parameters.AddWithValue("$instance_id", entry.InstanceId);
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        });
    }

    /// <summary>
    /// Queries log entries with optional filters.
    /// <paramref name="levels"/> accepts a comma-separated list (e.g. "Error,Warning").
    /// </summary>
    public List<LogRecord> Query(string? levels = null, string? category = null,
        string? fromTime = null, string? toTime = null, string? search = null,
        int limit = 100, string? scope = null, string? instanceId = null,
        int offset = 0)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();

        var where = BuildWhereClause(levels, category, fromTime, toTime, search, scope, instanceId, cmd);

        cmd.CommandText = $"""
            SELECT id, level, category, message, exception, metadata, created_at, scope, instance_id
            FROM logs
            {where}
            ORDER BY created_at DESC
            LIMIT $limit OFFSET $offset;
            """;

        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

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
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }
        return list;
    }

    /// <summary>
    /// Returns total count of log entries matching the given filters.
    /// </summary>
    public int QueryCount(string? levels = null, string? category = null,
        string? fromTime = null, string? toTime = null, string? search = null,
        string? scope = null, string? instanceId = null)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();

        var where = BuildWhereClause(levels, category, fromTime, toTime, search, scope, instanceId, cmd);

        cmd.CommandText = $"SELECT COUNT(*) FROM logs {where};";

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Builds the WHERE clause and adds parameters to the command.
    /// </summary>
    private static string BuildWhereClause(string? levels, string? category,
        string? fromTime, string? toTime, string? search,
        string? scope, string? instanceId,
        Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        var where = "WHERE 1=1";

        if (levels is not null)
        {
            var levelList = levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (levelList.Length == 1)
            {
                where += " AND level = $level0";
                cmd.Parameters.AddWithValue("$level0", levelList[0]);
            }
            else if (levelList.Length > 1)
            {
                var paramNames = new string[levelList.Length];
                for (var i = 0; i < levelList.Length; i++)
                {
                    paramNames[i] = $"$level{i}";
                    cmd.Parameters.AddWithValue(paramNames[i], levelList[i]);
                }
                where += $" AND level IN ({string.Join(", ", paramNames)})";
            }
        }

        if (category is not null)
        {
            where += " AND category = $category";
            cmd.Parameters.AddWithValue("$category", category);
        }
        if (fromTime is not null)
        {
            where += " AND created_at >= $from";
            cmd.Parameters.AddWithValue("$from", fromTime);
        }
        if (toTime is not null)
        {
            where += " AND created_at <= $to";
            cmd.Parameters.AddWithValue("$to", toTime);
        }
        if (search is not null)
        {
            where += " AND message LIKE $search ESCAPE '\\'";
            var escapedSearch = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$search", $"%{escapedSearch}%");
        }
        if (scope is not null)
        {
            where += " AND scope = $scope";
            cmd.Parameters.AddWithValue("$scope", scope);
        }
        if (instanceId is not null)
        {
            where += " AND instance_id = $instance_id";
            cmd.Parameters.AddWithValue("$instance_id", instanceId);
        }

        return where;
    }

    /// <summary>
    /// Returns all distinct instance IDs from the logs table.
    /// </summary>
    public List<string> GetDistinctInstanceIds()
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT instance_id FROM logs
            WHERE instance_id != ''
            ORDER BY instance_id DESC
            LIMIT 50;
            """;

        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    /// <summary>
    /// Prunes old log entries by level with different retention periods.
    /// Warning logs use the same retention as Info logs.
    /// </summary>
    public int Prune(int debugDays = 7, int infoDays = 30, int errorDays = 90)
    {
        return db.ExecuteWrite(conn =>
        {
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
                cmd.Parameters.AddWithValue("$days", $"-{infoDays} days");
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
        });
    }
}
