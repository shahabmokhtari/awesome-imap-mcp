using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class DuplicateTools(AppDatabase db, QueueManager queueManager, AppConfig config, ILogger<DuplicateTools> logger)
{
    [McpServerTool, Description(
        "Detect emails that exist in multiple IMAP accounts (same RFC Message-ID across different account_ids). " +
        "Useful for finding cross-account duplicates when the same email was synced to multiple accounts.")]
    public string DetectDuplicates(
        [Description("Filter to duplicates involving this account (optional)")] string? accountId = null,
        [Description("Max number of duplicate groups to return (default: 50)")] int limit = 50)
    {
        return McpJsonDefaults.LogToolCall(logger, "detect_duplicates",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["limit"] = limit },
            () =>
            {
                try
                {
                    using var conn = db.GetReadConnection();
                    using var cmd = conn.CreateCommand();

                    var accountFilter = accountId is not null
                        ? "AND m.message_id IN (SELECT message_id FROM messages WHERE account_id = $acct AND message_id IS NOT NULL AND deleted_at IS NULL)"
                        : "";

                    // Use MIN(mf.uid) and MIN(f.path) to pick one folder per account
                    // (avoids duplicate rows when a message appears in multiple folders)
                    cmd.CommandText = $"""
                        SELECT m.message_id, m.account_id, m.id, m.subject, m.from_address, m.date,
                               MIN(mf.uid) as uid, MIN(f.path) as folder_path
                        FROM messages m
                        JOIN message_folders mf ON mf.message_id = m.id
                        JOIN folders f ON f.id = mf.folder_id
                        WHERE m.message_id IS NOT NULL AND m.deleted_at IS NULL
                        AND m.message_id IN (
                            SELECT message_id FROM messages
                            WHERE message_id IS NOT NULL AND deleted_at IS NULL
                            GROUP BY message_id
                            HAVING COUNT(DISTINCT account_id) > 1
                        )
                        {accountFilter}
                        GROUP BY m.message_id, m.account_id, m.id
                        ORDER BY m.message_id, m.account_id
                        """;

                    if (accountId is not null)
                        cmd.Parameters.AddWithValue("$acct", accountId);

                    var groups = new Dictionary<string, (string? Subject, string? From, string? Date, List<object> Copies)>();

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var msgId = reader.GetString(0);
                        var acct = reader.GetString(1);
                        var dbId = reader.GetInt32(2);
                        var subject = reader.IsDBNull(3) ? null : reader.GetString(3);
                        var from = reader.IsDBNull(4) ? null : reader.GetString(4);
                        var date = reader.IsDBNull(5) ? null : reader.GetString(5);
                        var uid = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
                        var folderPath = reader.IsDBNull(7) ? null : reader.GetString(7);

                        if (!groups.ContainsKey(msgId))
                            groups[msgId] = (subject, from, date, []);

                        groups[msgId].Copies.Add(new
                        {
                            account_id = acct,
                            db_id = dbId,
                            folder_path = folderPath,
                            uid,
                            date
                        });
                    }

                    var limitedGroups = groups.Take(limit).ToList();
                    var totalDuplicates = limitedGroups.Sum(g => g.Value.Copies.Count);

                    return JsonSerializer.Serialize(new
                    {
                        duplicate_groups = limitedGroups.Count,
                        total_duplicates = totalDuplicates,
                        groups = limitedGroups.Select(g => new
                        {
                            message_id = g.Key,
                            subject = g.Value.Subject,
                            from = g.Value.From,
                            date = g.Value.Date,
                            copies = g.Value.Copies
                        }).ToList()
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DetectDuplicates failed");
                    return McpJsonDefaults.Error($"Duplicate detection failed: {ex.Message}");
                }
            }, config);
    }

    [McpServerTool, Description(
        "Delete duplicate emails from a specific account, keeping them in the other account(s). " +
        "Only deletes messages whose RFC Message-ID also exists in at least one other account. " +
        "Deletions are queued via the operation queue. Defaults to dry run (preview only).")]
    public string DeleteDuplicates(
        [Description("Account ID to delete duplicates FROM")] string accountId,
        [Description("Optional folder name filter (only delete duplicates in this folder)")] string? folder = null,
        [Description("If true (default), only count duplicates without deleting")] bool dryRun = true)
    {
        return McpJsonDefaults.LogToolCall(logger, "delete_duplicates",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["folder"] = folder, ["dryRun"] = dryRun },
            () =>
            {
                try
                {
                    using var readConn = db.GetReadConnection();
                    using var findCmd = readConn.CreateCommand();

                    var folderFilter = folder is not null
                        ? "AND f.path = $folder"
                        : "";

                    findCmd.CommandText = $"""
                        SELECT m.account_id, f.path, mf.uid
                        FROM messages m
                        JOIN message_folders mf ON mf.message_id = m.id
                        JOIN folders f ON f.id = mf.folder_id
                        WHERE m.account_id = $acct
                          AND m.message_id IS NOT NULL
                          AND m.deleted_at IS NULL
                          {folderFilter}
                          AND m.message_id IN (
                              SELECT message_id FROM messages
                              WHERE message_id IS NOT NULL AND deleted_at IS NULL
                                AND account_id != $acct
                          )
                        """;

                    findCmd.Parameters.AddWithValue("$acct", accountId);
                    if (folder is not null)
                        findCmd.Parameters.AddWithValue("$folder", folder);

                    var toDelete = new List<(string AccountId, string FolderPath, int Uid)>();
                    using var reader = findCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var acct = reader.GetString(0);
                        var folderPath = reader.GetString(1);
                        var uid = reader.GetInt32(2);
                        toDelete.Add((acct, folderPath, uid));
                    }

                    var count = toDelete.Count;

                    if (dryRun)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            account_id = accountId,
                            folder = folder ?? "all",
                            duplicates_found = count,
                            would_delete = count,
                            dry_run = true
                        }, McpJsonDefaults.Options);
                    }

                    // Group by account + folder for efficient queue operations
                    var grouped = toDelete.GroupBy(d => (d.AccountId, d.FolderPath));
                    var enqueued = 0;
                    foreach (var group in grouped)
                    {
                        var uidList = group.Select(g => g.Uid).ToList();
                        var payload = JsonSerializer.Serialize(new { uids = uidList, folder = group.Key.FolderPath });
                        var operationType = uidList.Count > 10 ? OperationType.BulkDelete : OperationType.Delete;
                        queueManager.EnqueueOperation(group.Key.AccountId, operationType, payload);
                        enqueued += uidList.Count;
                    }

                    return JsonSerializer.Serialize(new
                    {
                        account_id = accountId,
                        folder = folder ?? "all",
                        duplicates_found = count,
                        enqueued,
                        dry_run = false
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DeleteDuplicates failed");
                    return McpJsonDefaults.Error($"Duplicate deletion failed: {ex.Message}");
                }
            }, config);
    }
}
