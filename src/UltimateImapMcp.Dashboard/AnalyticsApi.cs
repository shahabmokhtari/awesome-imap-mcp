using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Dashboard;

public static class AnalyticsApi
{
    public static IEndpointRouteBuilder MapAnalyticsApi(this IEndpointRouteBuilder app)
    {
        // GET /api/analytics/volume?startDate=&endDate=&accountId=
        app.MapGet("/api/analytics/volume", (HttpContext ctx, AppDatabase db,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("AnalyticsApi");
            try
            {
                var startDateStr = ctx.Request.Query["startDate"].FirstOrDefault();
                var endDateStr = ctx.Request.Query["endDate"].FirstOrDefault();
                var accountId = ctx.Request.Query["accountId"].FirstOrDefault();

                // Default: all time (use epoch 0 = 2000-01-01 as a practical minimum)
                long startEpoch;
                long endEpoch;

                if (!string.IsNullOrEmpty(startDateStr) && DateTimeOffset.TryParse(startDateStr, out var startDate))
                    startEpoch = startDate.ToUnixTimeSeconds();
                else
                    startEpoch = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

                if (!string.IsNullOrEmpty(endDateStr) && DateTimeOffset.TryParse(endDateStr, out var endDate))
                    endEpoch = endDate.ToUnixTimeSeconds();
                else
                    endEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                using var conn = db.GetReadConnection();
                using var cmd = conn.CreateCommand();

                var accountFilter = !string.IsNullOrEmpty(accountId)
                    ? "AND m.account_id = $accountId" : "";

                cmd.CommandText = $"""
                    SELECT strftime('%Y-%m', datetime(m.date_epoch, 'unixepoch')) as month,
                           COUNT(*) as count
                    FROM messages m
                    WHERE m.date_epoch >= $startEpoch
                      AND m.date_epoch <= $endEpoch
                      AND m.deleted_at IS NULL
                      AND m.date_epoch IS NOT NULL
                      {accountFilter}
                    GROUP BY month
                    ORDER BY month ASC;
                    """;
                cmd.Parameters.AddWithValue("$startEpoch", startEpoch);
                cmd.Parameters.AddWithValue("$endEpoch", endEpoch);
                if (!string.IsNullOrEmpty(accountId))
                    cmd.Parameters.AddWithValue("$accountId", accountId);

                using var reader = cmd.ExecuteReader();
                var months = new List<object>();
                while (reader.Read())
                {
                    months.Add(new
                    {
                        month = reader.IsDBNull(0) ? "unknown" : reader.GetString(0),
                        count = reader.GetInt32(1)
                    });
                }

                return Results.Ok(new { months });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get analytics volume");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // GET /api/analytics/top-senders?limit=100&startDate=&endDate=&accountId=&search=
        app.MapGet("/api/analytics/top-senders", (HttpContext ctx, AppDatabase db,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("AnalyticsApi");
            try
            {
                var limitStr = ctx.Request.Query["limit"].FirstOrDefault();
                var limit = 100;
                if (!string.IsNullOrEmpty(limitStr) && int.TryParse(limitStr, out var parsedLimit))
                    limit = Math.Clamp(parsedLimit, 1, 500);

                var startDateStr = ctx.Request.Query["startDate"].FirstOrDefault();
                var endDateStr = ctx.Request.Query["endDate"].FirstOrDefault();
                var accountId = ctx.Request.Query["accountId"].FirstOrDefault();
                var search = ctx.Request.Query["search"].FirstOrDefault();

                using var conn = db.GetReadConnection();
                using var cmd = conn.CreateCommand();

                // type=received (default) excludes sent/drafts folders; type=sent shows only sent; type=all shows everything
                var typeFilter = ctx.Request.Query["type"].FirstOrDefault() ?? "received";

                var conditions = new List<string>
                {
                    "m.from_email IS NOT NULL",
                    "m.from_email != ''",
                    "m.deleted_at IS NULL"
                };

                // Filter by folder type (sent vs received)
                var joinFolder = "";
                if (typeFilter == "received")
                {
                    joinFolder = "JOIN folders f ON m.folder_id = f.id";
                    conditions.Add("COALESCE(f.role, '') NOT IN ('sent', 'drafts', 'trash', 'spam')");
                    conditions.Add("f.path NOT LIKE '%Sent%'");
                    conditions.Add("f.path NOT LIKE '%Draft%'");
                }
                else if (typeFilter == "sent")
                {
                    joinFolder = "JOIN folders f ON m.folder_id = f.id";
                    conditions.Add("(f.role = 'sent' OR f.path LIKE '%Sent%')");
                }

                if (!string.IsNullOrEmpty(startDateStr) && DateTimeOffset.TryParse(startDateStr, out var startDate))
                {
                    conditions.Add("m.date_epoch >= $startEpoch");
                    cmd.Parameters.AddWithValue("$startEpoch", startDate.ToUnixTimeSeconds());
                }

                if (!string.IsNullOrEmpty(endDateStr) && DateTimeOffset.TryParse(endDateStr, out var endDate))
                {
                    conditions.Add("m.date_epoch <= $endEpoch");
                    cmd.Parameters.AddWithValue("$endEpoch", endDate.ToUnixTimeSeconds());
                }

                if (!string.IsNullOrEmpty(accountId))
                {
                    conditions.Add("m.account_id = $accountId");
                    cmd.Parameters.AddWithValue("$accountId", accountId);
                }

                if (!string.IsNullOrEmpty(search))
                {
                    conditions.Add("(m.from_email LIKE $search ESCAPE '\\' OR m.from_address LIKE $search ESCAPE '\\')");
                    cmd.Parameters.AddWithValue("$search", $"%{EscapeLike(search)}%");
                }

                var where = "WHERE " + string.Join(" AND ", conditions);

                // Epoch boundaries for month-based breakdown
                var now = DateTimeOffset.UtcNow;
                var threeMonthsAgo = now.AddMonths(-3).ToUnixTimeSeconds();
                var sixMonthsAgo = now.AddMonths(-6).ToUnixTimeSeconds();
                var twelveMonthsAgo = now.AddMonths(-12).ToUnixTimeSeconds();
                var twentyFourMonthsAgo = now.AddMonths(-24).ToUnixTimeSeconds();

                cmd.CommandText = $"""
                    SELECT m.from_email,
                           COALESCE(m.from_address, m.from_email) as display_name,
                           COUNT(*) as total_count,
                           MIN(m.date) as first_seen,
                           MAX(m.date) as last_seen,
                           SUM(CASE WHEN m.date_epoch >= $threeMonthsAgo THEN 1 ELSE 0 END) as last3m,
                           SUM(CASE WHEN m.date_epoch >= $sixMonthsAgo THEN 1 ELSE 0 END) as last6m,
                           SUM(CASE WHEN m.date_epoch >= $twelveMonthsAgo THEN 1 ELSE 0 END) as last12m,
                           SUM(CASE WHEN m.date_epoch >= $twentyFourMonthsAgo THEN 1 ELSE 0 END) as last24m
                    FROM messages m
                    {joinFolder}
                    {where}
                    GROUP BY m.from_email
                    ORDER BY total_count DESC
                    LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$threeMonthsAgo", threeMonthsAgo);
                cmd.Parameters.AddWithValue("$sixMonthsAgo", sixMonthsAgo);
                cmd.Parameters.AddWithValue("$twelveMonthsAgo", twelveMonthsAgo);
                cmd.Parameters.AddWithValue("$twentyFourMonthsAgo", twentyFourMonthsAgo);
                cmd.Parameters.AddWithValue("$limit", limit);

                using var reader = cmd.ExecuteReader();
                var senders = new List<object>();
                while (reader.Read())
                {
                    senders.Add(new
                    {
                        email = reader.GetString(0),
                        name = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
                        count = reader.GetInt32(2),
                        firstSeen = reader.IsDBNull(3) ? null : reader.GetString(3),
                        lastSeen = reader.IsDBNull(4) ? null : reader.GetString(4),
                        last3m = reader.GetInt32(5),
                        last6m = reader.GetInt32(6),
                        last12m = reader.GetInt32(7),
                        last24m = reader.GetInt32(8)
                    });
                }

                return Results.Ok(new { senders });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get analytics top senders");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // GET /api/analytics/account-breakdown
        app.MapGet("/api/analytics/account-breakdown", (AppDatabase db,
            AccountRepository accountRepo, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("AnalyticsApi");
            try
            {
                var allAccounts = accountRepo.GetAll();
                var accountMap = allAccounts.ToDictionary(a => a.Id);

                using var conn = db.GetReadConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT m.account_id,
                           COUNT(*) as total_messages,
                           COALESCE(SUM(m.size_bytes), 0) as total_size
                    FROM messages m
                    WHERE m.deleted_at IS NULL
                    GROUP BY m.account_id
                    ORDER BY total_messages DESC;
                    """;

                using var reader = cmd.ExecuteReader();
                var accounts = new List<object>();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    var hasAccount = accountMap.TryGetValue(id, out var account);
                    accounts.Add(new
                    {
                        id,
                        name = hasAccount ? account!.Name : id,
                        email = hasAccount ? account!.Username : "",
                        totalMessages = reader.GetInt32(1),
                        totalSizeMb = Math.Round(reader.GetInt64(2) / (1024.0 * 1024.0), 2),
                        enabled = hasAccount && account!.Enabled
                    });
                }

                return Results.Ok(new { accounts });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get analytics account breakdown");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // GET /api/analytics/label-distribution?accountId=
        app.MapGet("/api/analytics/label-distribution", (HttpContext ctx, AppDatabase db,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("AnalyticsApi");
            try
            {
                var accountId = ctx.Request.Query["accountId"].FirstOrDefault();

                using var conn = db.GetReadConnection();
                using var cmd = conn.CreateCommand();

                var accountFilter = !string.IsNullOrEmpty(accountId)
                    ? "AND m.account_id = $accountId" : "";

                // Count messages by their flags/labels
                // Split flags by comma/space and count each unique flag
                cmd.CommandText = $"""
                    SELECT m.flags, COUNT(*) as count
                    FROM messages m
                    WHERE m.deleted_at IS NULL
                      AND m.flags IS NOT NULL
                      AND m.flags != ''
                      {accountFilter}
                    GROUP BY m.flags
                    ORDER BY count DESC;
                    """;

                if (!string.IsNullOrEmpty(accountId))
                    cmd.Parameters.AddWithValue("$accountId", accountId);

                using var reader = cmd.ExecuteReader();

                // Aggregate: flags column may contain multiple flags like "\Seen \Flagged"
                var labelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var totalWithFlags = 0;
                while (reader.Read())
                {
                    var flags = reader.GetString(0);
                    var count = reader.GetInt32(1);
                    totalWithFlags += count;

                    // Split on spaces (IMAP flags are space-separated)
                    foreach (var flag in flags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (labelCounts.ContainsKey(flag))
                            labelCounts[flag] += count;
                        else
                            labelCounts[flag] = count;
                    }
                }

                // Also count messages with no flags
                using var noFlagsCmd = conn.CreateCommand();
                noFlagsCmd.CommandText = $"""
                    SELECT COUNT(*) FROM messages m
                    WHERE m.deleted_at IS NULL
                      AND (m.flags IS NULL OR m.flags = '')
                      {accountFilter};
                    """;
                if (!string.IsNullOrEmpty(accountId))
                    noFlagsCmd.Parameters.AddWithValue("$accountId", accountId);
                var noFlagsCount = Convert.ToInt32(noFlagsCmd.ExecuteScalar());

                var totalMessages = totalWithFlags + noFlagsCount;
                var labels = labelCounts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => new
                    {
                        name = kv.Key,
                        count = kv.Value,
                        percentage = totalMessages > 0
                            ? Math.Round(kv.Value * 100.0 / totalMessages, 1)
                            : 0.0
                    })
                    .ToList();

                return Results.Ok(new { labels });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get analytics label distribution");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // POST /api/analytics/bulk-delete
        app.MapPost("/api/analytics/bulk-delete", async (HttpContext ctx, AppDatabase db,
            QueueManager queueManager, AccountRepository accountRepo,
            FolderRepository folderRepo, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("AnalyticsApi");
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<BulkDeleteRequest>().ConfigureAwait(false);
                if (body is null || string.IsNullOrEmpty(body.SenderEmail))
                    return Results.BadRequest(new { error = "senderEmail is required" });

                // Count matching messages and group by account+folder
                using var conn = db.GetReadConnection();
                using var cmd = conn.CreateCommand();

                var conditions = new List<string>
                {
                    "m.from_email = $senderEmail",
                    "m.deleted_at IS NULL"
                };

                if (!string.IsNullOrEmpty(body.AccountId))
                {
                    conditions.Add("m.account_id = $accountId");
                    cmd.Parameters.AddWithValue("$accountId", body.AccountId);
                }

                if (!string.IsNullOrEmpty(body.StartDate) && DateTimeOffset.TryParse(body.StartDate, out var startDate))
                {
                    conditions.Add("m.date_epoch >= $startEpoch");
                    cmd.Parameters.AddWithValue("$startEpoch", startDate.ToUnixTimeSeconds());
                }

                if (!string.IsNullOrEmpty(body.EndDate) && DateTimeOffset.TryParse(body.EndDate, out var endDate))
                {
                    conditions.Add("m.date_epoch <= $endEpoch");
                    cmd.Parameters.AddWithValue("$endEpoch", endDate.ToUnixTimeSeconds());
                }

                var where = "WHERE " + string.Join(" AND ", conditions);
                cmd.Parameters.AddWithValue("$senderEmail", body.SenderEmail);

                // Get message IDs grouped by account_id + folder_id for queue operations
                cmd.CommandText = $"""
                    SELECT m.account_id, mf.folder_id, GROUP_CONCAT(mf.uid) as uids
                    FROM messages m
                    JOIN message_folders mf ON mf.message_id = m.id
                    {where}
                    GROUP BY m.account_id, mf.folder_id;
                    """;

                var operationIds = new List<string>();
                var totalQueued = 0;

                using var reader = cmd.ExecuteReader();
                var batches = new List<(string AccountId, int FolderId, List<long> Uids)>();
                while (reader.Read())
                {
                    var acctId = reader.GetString(0);
                    var folderId = reader.GetInt32(1);
                    var uidsStr = reader.GetString(2);
                    var uids = uidsStr.Split(',').Select(long.Parse).ToList();
                    batches.Add((acctId, folderId, uids));
                }
                reader.Close();

                // Determine action: delete (default), trash, or archive
                var action = (body.Action ?? "delete").ToLowerInvariant();
                var isMove = action is "trash" or "archive";

                // Look up folder paths and enqueue in chunks of 50 UIDs
                const int chunkSize = 50;
                foreach (var (acctId, folderId, uids) in batches)
                {
                    var folders = folderRepo.GetByAccount(acctId);
                    var folder = folders.FirstOrDefault(f => f.Id == folderId);
                    if (folder is null) continue;

                    // Validate account exists and is enabled
                    var account = accountRepo.ResolveEnabledAccount(acctId);
                    if (account is null) continue;

                    // For move operations, find or use standard destination folder
                    string? destFolder = null;
                    if (isMove)
                    {
                        destFolder = action == "trash"
                            ? folders.FirstOrDefault(f => f.Role == "trash")?.Path
                              ?? folders.FirstOrDefault(f => f.Path.Contains("Trash", StringComparison.OrdinalIgnoreCase))?.Path
                              ?? "Trash"
                            : folders.FirstOrDefault(f => f.Role == "archive")?.Path
                              ?? folders.FirstOrDefault(f => f.Path.Contains("Archive", StringComparison.OrdinalIgnoreCase))?.Path
                              ?? "Archive";
                    }

                    // Split UIDs into chunks to avoid oversized operations
                    for (var i = 0; i < uids.Count; i += chunkSize)
                    {
                        var chunk = uids.Skip(i).Take(chunkSize).ToList();

                        object payloadObj;
                        OperationType opType;

                        if (isMove)
                        {
                            opType = chunk.Count > 10 ? OperationType.BulkMove : OperationType.Move;
                            payloadObj = new
                            {
                                folder = folder.Path,
                                destination = destFolder,
                                uids = chunk,
                                reason = $"Bulk {action} from sender: {body.SenderEmail}"
                            };
                        }
                        else
                        {
                            opType = chunk.Count > 10 ? OperationType.BulkDelete : OperationType.Delete;
                            payloadObj = new
                            {
                                folder = folder.Path,
                                uids = chunk,
                                reason = $"Bulk delete from sender: {body.SenderEmail}"
                            };
                        }

                        var payload = JsonSerializer.Serialize(payloadObj);

                        try
                        {
                            var opId = queueManager.EnqueueOperation(acctId, opType, payload);
                            operationIds.Add(opId);
                            totalQueued += chunk.Count;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to enqueue bulk {Action} for account {AccountId} folder {FolderId} chunk starting at {Offset}",
                                action, acctId, folderId, i);
                        }
                    }
                }

                return Results.Ok(new
                {
                    queued = totalQueued,
                    operationIds
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process bulk delete request");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // GET /api/analytics/summary
        app.MapGet("/api/analytics/summary", (HttpContext ctx, AppDatabase db,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("AnalyticsApi");
            try
            {
                var accountId = ctx.Request.Query["accountId"].FirstOrDefault();
                using var conn = db.GetReadConnection();

                var accountFilter = !string.IsNullOrEmpty(accountId)
                    ? "AND m.account_id = $accountId" : "";

                // Total emails
                using var totalCmd = conn.CreateCommand();
                totalCmd.CommandText = $"""
                    SELECT COUNT(*) FROM messages m
                    WHERE m.deleted_at IS NULL {accountFilter};
                    """;
                if (!string.IsNullOrEmpty(accountId))
                    totalCmd.Parameters.AddWithValue("$accountId", accountId);
                var totalEmails = Convert.ToInt32(totalCmd.ExecuteScalar());

                // Distinct senders
                using var sendersCmd = conn.CreateCommand();
                sendersCmd.CommandText = $"""
                    SELECT COUNT(DISTINCT m.from_email) FROM messages m
                    WHERE m.deleted_at IS NULL
                      AND m.from_email IS NOT NULL AND m.from_email != ''
                      {accountFilter};
                    """;
                if (!string.IsNullOrEmpty(accountId))
                    sendersCmd.Parameters.AddWithValue("$accountId", accountId);
                var uniqueSenders = Convert.ToInt32(sendersCmd.ExecuteScalar());

                // Date range (months of history)
                using var dateCmd = conn.CreateCommand();
                dateCmd.CommandText = $"""
                    SELECT MIN(m.date_epoch), MAX(m.date_epoch) FROM messages m
                    WHERE m.deleted_at IS NULL AND m.date_epoch IS NOT NULL {accountFilter};
                    """;
                if (!string.IsNullOrEmpty(accountId))
                    dateCmd.Parameters.AddWithValue("$accountId", accountId);
                using var dateReader = dateCmd.ExecuteReader();
                var monthsOfHistory = 0;
                if (dateReader.Read() && !dateReader.IsDBNull(0) && !dateReader.IsDBNull(1))
                {
                    var minEpoch = dateReader.GetInt64(0);
                    var maxEpoch = dateReader.GetInt64(1);
                    var span = DateTimeOffset.FromUnixTimeSeconds(maxEpoch) - DateTimeOffset.FromUnixTimeSeconds(minEpoch);
                    monthsOfHistory = Math.Max(1, (int)Math.Ceiling(span.TotalDays / 30));
                }

                // Flagged count (messages with \Flagged flag)
                using var flaggedCmd = conn.CreateCommand();
                flaggedCmd.CommandText = $"""
                    SELECT COUNT(*) FROM messages m
                    WHERE m.deleted_at IS NULL
                      AND m.flags LIKE '%\\Flagged%'
                      {accountFilter};
                    """;
                if (!string.IsNullOrEmpty(accountId))
                    flaggedCmd.Parameters.AddWithValue("$accountId", accountId);
                var flaggedCount = Convert.ToInt32(flaggedCmd.ExecuteScalar());

                // Unread count (messages without \Seen flag)
                using var unreadCmd = conn.CreateCommand();
                unreadCmd.CommandText = $"""
                    SELECT COUNT(*) FROM messages m
                    WHERE m.deleted_at IS NULL
                      AND (m.flags IS NULL OR m.flags NOT LIKE '%\\Seen%')
                      {accountFilter};
                    """;
                if (!string.IsNullOrEmpty(accountId))
                    unreadCmd.Parameters.AddWithValue("$accountId", accountId);
                var unreadCount = Convert.ToInt32(unreadCmd.ExecuteScalar());

                // With attachments
                using var attachCmd = conn.CreateCommand();
                attachCmd.CommandText = $"""
                    SELECT COUNT(*) FROM messages m
                    WHERE m.deleted_at IS NULL
                      AND m.has_attachments = 1
                      {accountFilter};
                    """;
                if (!string.IsNullOrEmpty(accountId))
                    attachCmd.Parameters.AddWithValue("$accountId", accountId);
                var withAttachments = Convert.ToInt32(attachCmd.ExecuteScalar());

                return Results.Ok(new
                {
                    totalEmails,
                    uniqueSenders,
                    monthsOfHistory,
                    flaggedCount,
                    unreadCount,
                    withAttachments
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get analytics summary");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        return app;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private record BulkDeleteRequest
    {
        public string? SenderEmail { get; init; }
        public string? AccountId { get; init; }
        public string? StartDate { get; init; }
        public string? EndDate { get; init; }
        public string? Folder { get; init; }
        /// <summary>"delete" (default), "trash", or "archive"</summary>
        public string? Action { get; init; }
    }
}
