using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue;

public class QueueRepository(AppDatabase db)
{
    public string Insert(EnqueueRequest request)
    {
        var id = Guid.NewGuid().ToString();
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO operation_queue (id, account_id, operation, priority, status, payload,
                requires_confirm, max_retries, sends_at)
            VALUES ($id, $accountId, $op, $priority, 'pending', $payload,
                $requiresConfirm, $maxRetries, $sendsAt);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$accountId", request.AccountId);
        cmd.Parameters.AddWithValue("$op", request.Operation.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$priority", (int)request.Priority);
        cmd.Parameters.AddWithValue("$payload", request.Payload);
        cmd.Parameters.AddWithValue("$requiresConfirm", request.RequiresConfirm ? 1 : 0);
        cmd.Parameters.AddWithValue("$maxRetries", request.MaxRetries);
        cmd.Parameters.AddWithValue("$sendsAt", (object?)request.SendsAt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return id;
    }

    public QueuedOperation? GetById(string id)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM operation_queue WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public List<QueuedOperation> GetByAccount(string accountId, string? statusFilter = null)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        var where = "WHERE account_id = $accountId";
        if (statusFilter != null) where += " AND status = $status";
        cmd.CommandText = $"SELECT * FROM operation_queue {where} ORDER BY created_at DESC;";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        if (statusFilter != null) cmd.Parameters.AddWithValue("$status", statusFilter);
        using var reader = cmd.ExecuteReader();
        var list = new List<QueuedOperation>();
        while (reader.Read()) list.Add(ReadRecord(reader));
        return list;
    }

    public List<QueuedOperation> GetAll(string? statusFilter = null, int limit = 100)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        var where = statusFilter != null ? "WHERE status = $status" : "";
        cmd.CommandText = $"SELECT * FROM operation_queue {where} ORDER BY created_at DESC LIMIT $limit;";
        if (statusFilter != null) cmd.Parameters.AddWithValue("$status", statusFilter);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<QueuedOperation>();
        while (reader.Read()) list.Add(ReadRecord(reader));
        return list;
    }

    public List<QueuedOperation> GetAllPending(string? accountId = null)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        var where = "WHERE status IN ('pending', 'confirmed')";
        if (accountId != null) where += " AND account_id = $accountId";
        cmd.CommandText = $"SELECT * FROM operation_queue {where} ORDER BY priority, created_at;";
        if (accountId != null) cmd.Parameters.AddWithValue("$accountId", accountId);
        using var reader = cmd.ExecuteReader();
        var list = new List<QueuedOperation>();
        while (reader.Read()) list.Add(ReadRecord(reader));
        return list;
    }

    public List<QueuedOperation> GetConfirmedByPriority(int priority, int limit = 50)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM operation_queue
            WHERE status = 'confirmed' AND priority = $priority
            ORDER BY created_at
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$priority", priority);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<QueuedOperation>();
        while (reader.Read()) list.Add(ReadRecord(reader));
        return list;
    }

    public void UpdateStatus(string id, string status)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        var timeCol = status switch
        {
            "confirmed" => ", confirmed_at = datetime('now')",
            "processing" => ", started_at = datetime('now')",
            "completed" => ", completed_at = datetime('now')",
            "cancelled" => ", cancelled_at = datetime('now')",
            _ => ""
        };
        cmd.CommandText = $"UPDATE operation_queue SET status = $status{timeCol} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.ExecuteNonQuery();
    }

    public bool Cancel(string id)
    {
        var op = GetById(id);
        if (op == null || op.Status is "completed" or "cancelled" or "processing")
            return false;
        UpdateStatus(id, "cancelled");
        return true;
    }

    public void MarkFailed(string id, string errorMessage, bool incrementRetry = false)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = incrementRetry
            ? "UPDATE operation_queue SET status = 'failed', error_message = $error, retry_count = retry_count + 1 WHERE id = $id;"
            : "UPDATE operation_queue SET status = 'failed', error_message = $error WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$error", errorMessage);
        cmd.ExecuteNonQuery();
    }

    public void MarkRetryable(string id, string errorMessage)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE operation_queue SET status = 'confirmed', error_message = $error, retry_count = retry_count + 1
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$error", errorMessage);
        cmd.ExecuteNonQuery();
    }

    private static QueuedOperation ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        AccountId = r.GetString(r.GetOrdinal("account_id")),
        Operation = r.GetString(r.GetOrdinal("operation")),
        Priority = r.GetInt32(r.GetOrdinal("priority")),
        Status = r.GetString(r.GetOrdinal("status")),
        Payload = r.GetString(r.GetOrdinal("payload")),
        RequiresConfirm = r.GetInt32(r.GetOrdinal("requires_confirm")) == 1,
        ErrorMessage = r.IsDBNull(r.GetOrdinal("error_message")) ? null : r.GetString(r.GetOrdinal("error_message")),
        RetryCount = r.GetInt32(r.GetOrdinal("retry_count")),
        MaxRetries = r.GetInt32(r.GetOrdinal("max_retries")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        ConfirmedAt = r.IsDBNull(r.GetOrdinal("confirmed_at")) ? null : r.GetString(r.GetOrdinal("confirmed_at")),
        StartedAt = r.IsDBNull(r.GetOrdinal("started_at")) ? null : r.GetString(r.GetOrdinal("started_at")),
        CompletedAt = r.IsDBNull(r.GetOrdinal("completed_at")) ? null : r.GetString(r.GetOrdinal("completed_at")),
        CancelledAt = r.IsDBNull(r.GetOrdinal("cancelled_at")) ? null : r.GetString(r.GetOrdinal("cancelled_at")),
        SendsAt = r.IsDBNull(r.GetOrdinal("sends_at")) ? null : r.GetString(r.GetOrdinal("sends_at"))
    };
}
