using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class BodyFetchTools(
    MessageRepository messageRepo,
    FolderRepository folderRepo,
    IEmailBackendFactory backendFactory,
    AppConfig config,
    ILogger<BodyFetchTools> logger)
{
    [McpServerTool, Description(
        "Fetch message bodies in batch for the given UIDs. " +
        "Skips UIDs that already have cached bodies. Returns counts of requested, cached, fetched, and failed.")]
    public async Task<string> FetchBodies(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs to fetch bodies for")] string uids,
        [Description("Folder path (default: INBOX)")] string folder = "INBOX")
    {
        return await McpJsonDefaults.LogToolCallAsync(logger, "fetch_bodies",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["uids"] = uids, ["folder"] = folder },
            async () =>
            {
                try
                {
                    var parsedUids = uids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => long.TryParse(s, out var v) ? v : (long?)null)
                        .Where(v => v.HasValue)
                        .Select(v => v!.Value)
                        .ToList();

                    if (parsedUids.Count == 0)
                        return McpJsonDefaults.Error("No valid UIDs provided. Pass comma-separated numeric UIDs.");

                    var folderRecord = folderRepo.GetByPath(accountId, folder);
                    if (folderRecord is null)
                        return McpJsonDefaults.Error($"Folder '{folder}' not found for account '{accountId}'.");

                    // Check which UIDs already have cached bodies
                    var uncachedUids = new List<long>();
                    var alreadyCached = 0;
                    foreach (var uid in parsedUids)
                    {
                        var msg = messageRepo.GetByUid(accountId, folderRecord.Id, uid);
                        if (msg is not null && msg.BodyFetched)
                            alreadyCached++;
                        else
                            uncachedUids.Add(uid);
                    }

                    var fetched = 0;
                    if (uncachedUids.Count > 0)
                    {
                        await using var backend = backendFactory.CreateSyncBackend(accountId);
                        fetched = await backend.FetchMessageBodiesBatchAsync(
                            accountId, folder, uncachedUids).ConfigureAwait(false);
                    }

                    var failed = uncachedUids.Count - fetched;

                    return JsonSerializer.Serialize(new
                    {
                        requested = parsedUids.Count,
                        already_cached = alreadyCached,
                        fetched,
                        failed,
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "FetchBodies failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }
}
