using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.ImapClient;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public class SyncTools(SyncManager syncManager, AccountRepository accountRepo, AppConfig config, ILogger<SyncTools> logger)
{
    [McpServerTool, Description(
        "Trigger an immediate IMAP sync for one folder or all folders. " +
        "New messages will appear in search and list results after sync completes.")]
    public async Task<string> SyncNow(
        [Description("Account ID")] string accountId,
        [Description("Folder path (optional, syncs all if omitted)")] string? folderPath = null)
    {
        return await McpJsonDefaults.LogToolCallAsync(logger, "sync_now",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["folderPath"] = folderPath },
            async () =>
            {
                try
                {
                    var resolvedId = ResolveAccountId(accountId);

                    await syncManager.TriggerSyncAsync(resolvedId, folderPath).ConfigureAwait(false);

                    return JsonSerializer.Serialize(new
                    {
                        account_id = resolvedId,
                        folder = folderPath ?? "(all folders)",
                        status = "completed",
                        message = folderPath is not null
                            ? $"Sync completed for {folderPath}."
                            : "Sync completed for all folders."
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "SyncNow failed");
                    return JsonSerializer.Serialize(new
                    {
                        account_id = accountId,
                        folder = folderPath ?? "(all folders)",
                        status = "error",
                        message = ex.Message
                    }, McpJsonDefaults.Options);
                }
            }, config);
    }

    [McpServerTool, Description(
        "Get real-time sync status for all folders showing last sync time, message counts, and current sync state.")]
    public string GetSyncStatus([Description("Account ID")] string accountId)
    {
        return McpJsonDefaults.LogToolCall(logger, "get_sync_status",
            new Dictionary<string, object?> { ["accountId"] = accountId },
            () =>
            {
                try
                {
                    var resolvedId = ResolveAccountId(accountId);
                    var statuses = syncManager.GetSyncStatus(resolvedId);

                    var mapped = statuses.Select(s => new
                    {
                        folder = s.FolderPath,
                        display_name = s.DisplayName,
                        last_synced_at = s.LastSyncedAt.ToString("o"),
                        message_count = s.MessageCount,
                        unread_count = s.UnreadCount,
                        status = s.Status,
                        staleness_seconds = (int)(DateTime.UtcNow - s.LastSyncedAt).TotalSeconds
                    }).ToList();

                    return JsonSerializer.Serialize(new
                    {
                        account_id = resolvedId,
                        folder_count = mapped.Count,
                        folders = mapped
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return McpJsonDefaults.Error($"Failed to get sync status: {ex.Message}");
                }
            }, config);
    }

    /// <summary>
    /// Resolves an account identifier (ID or name) to the canonical DB ID.
    /// Throws if the account is disabled.
    /// Returns the input unchanged if no match is found (caller handles the error).
    /// </summary>
    private string ResolveAccountId(string idOrName)
    {
        var record = accountRepo.ResolveEnabledAccount(idOrName);
        return record?.Id ?? idOrName;
    }
}
