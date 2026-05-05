using AwesomeImapMcp.Core.Database;

namespace AwesomeImapMcp.Core.Repositories;

/// <summary>
/// Manages locally-stored labels for IMAP accounts that don't support keywords.
/// </summary>
public class LocalLabelRepository(LabelsDatabase db)
{
    public void AddLabel(string accountId, string messageId, string label)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO local_labels (account_id, message_id, label)
                VALUES ($accountId, $messageId, $label)
                """;
            cmd.Parameters.AddWithValue("$accountId", accountId);
            cmd.Parameters.AddWithValue("$messageId", messageId);
            cmd.Parameters.AddWithValue("$label", label);
            cmd.ExecuteNonQuery();
        });
    }

    public void RemoveLabel(string accountId, string messageId, string label)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM local_labels
                WHERE account_id = $accountId AND message_id = $messageId AND label = $label
                """;
            cmd.Parameters.AddWithValue("$accountId", accountId);
            cmd.Parameters.AddWithValue("$messageId", messageId);
            cmd.Parameters.AddWithValue("$label", label);
            cmd.ExecuteNonQuery();
        });
    }

    public List<string> GetLabels(string accountId, string messageId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT label FROM local_labels
            WHERE account_id = $accountId AND message_id = $messageId
            ORDER BY label
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$messageId", messageId);
        using var reader = cmd.ExecuteReader();
        var labels = new List<string>();
        while (reader.Read())
            labels.Add(reader.GetString(0));
        return labels;
    }

    /// <summary>
    /// Returns all local labels for an account, grouped by RFC Message-ID.
    /// </summary>
    public Dictionary<string, List<string>> GetByAccount(string accountId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT message_id, label FROM local_labels
            WHERE account_id = $accountId
            ORDER BY message_id, label
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, List<string>>();
        while (reader.Read())
        {
            var msgId = reader.GetString(0);
            var label = reader.GetString(1);
            if (!result.TryGetValue(msgId, out var labels))
            {
                labels = [];
                result[msgId] = labels;
            }
            labels.Add(label);
        }
        return result;
    }

    /// <summary>
    /// Re-applies local labels to the flags column in the message cache.
    /// Call this after sync to ensure local labels survive IMAP flag overwrites.
    /// </summary>
    public int ReconcileLabels(string accountId, AppDatabase cacheDb)
    {
        var localLabels = GetByAccount(accountId);
        if (localLabels.Count == 0) return 0;

        var reconciled = 0;
        // Read messages from cache DB and merge local labels into flags
        using var readConn = cacheDb.GetReadConnection();

        foreach (var (rfcMessageId, labels) in localLabels)
        {
            // Find message in cache by RFC Message-ID
            using var findCmd = readConn.CreateCommand();
            findCmd.CommandText = """
                SELECT id, flags FROM messages
                WHERE account_id = $accountId AND message_id = $messageId AND deleted_at IS NULL
                LIMIT 1
                """;
            findCmd.Parameters.AddWithValue("$accountId", accountId);
            findCmd.Parameters.AddWithValue("$messageId", rfcMessageId);
            using var reader = findCmd.ExecuteReader();
            if (!reader.Read()) continue;

            var msgDbId = reader.GetInt32(0);
            var currentFlags = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var flagSet = new HashSet<string>(
                currentFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            var changed = false;
            foreach (var label in labels)
            {
                if (flagSet.Add(label))
                    changed = true;
            }

            if (changed)
            {
                cacheDb.ExecuteWrite(writeConn =>
                {
                    using var updateCmd = writeConn.CreateCommand();
                    updateCmd.CommandText = "UPDATE messages SET flags = $flags WHERE id = $id";
                    updateCmd.Parameters.AddWithValue("$flags", string.Join(" ", flagSet));
                    updateCmd.Parameters.AddWithValue("$id", msgDbId);
                    updateCmd.ExecuteNonQuery();
                });
                reconciled++;
            }
        }
        return reconciled;
    }
}
