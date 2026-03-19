using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.ImapClient;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class SyncTools(SyncManager syncManager)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description("Trigger immediate sync for a folder or all folders.")]
    public async Task<string> SyncNow(
        [Description("Account ID")] string accountId,
        [Description("Folder path (optional, syncs all if omitted)")] string? folderPath = null)
    {
        try
        {
            await syncManager.TriggerSyncAsync(accountId, folderPath).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                account_id = accountId,
                folder = folderPath ?? "(all folders)",
                status = "completed",
                message = folderPath is not null
                    ? $"Sync completed for {folderPath}."
                    : "Sync completed for all folders."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SyncTools] SyncNow failed: {ex}");
            return JsonSerializer.Serialize(new
            {
                account_id = accountId,
                folder = folderPath ?? "(all folders)",
                status = "error",
                message = ex.Message
            }, JsonOptions);
        }
    }

    [McpServerTool, Description("Get sync status for all folders of an account.")]
    public string GetSyncStatus([Description("Account ID")] string accountId)
    {
        var statuses = syncManager.GetSyncStatus(accountId);

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
            account_id = accountId,
            folder_count = mapped.Count,
            folders = mapped
        }, JsonOptions);
    }
}
