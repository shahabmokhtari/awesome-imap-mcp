using System.Text.Json;
using MailKit;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public class MoveExecutor(AccountRepository accountRepo, CredentialEncryptor encryptor,
    IOAuthAccessTokenProvider oauthProvider,
    MessageRepository messageRepo, FolderRepository folderRepo) : IOperationExecutor
{
    public IReadOnlyList<string> SupportedOperations { get; } = ["move", "bulkmove"];

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var uids = payload.GetProperty("uids").EnumerateArray()
            .Select(u => new UniqueId((uint)u.GetInt32())).ToList();
        var fromPath = payload.GetProperty("from_folder").GetString()!;
        var toPath = payload.GetProperty("to_folder").GetString()!;

        UniqueIdMap? uidMap = null;

        var record = accountRepo.ResolveAccount(operation.AccountId)
            ?? throw new InvalidOperationException($"Account '{operation.AccountId}' not found in database.");
        var accountConfig = AccountConfigMapper.ToAccountConfig(record, encryptor);
        using var connMgr = new ImapConnectionManager(accountConfig, encryptor,
            oauthProvider: oauthProvider, accountId: record.Id);
        await connMgr.ExecuteAsync(async client =>
        {
            var srcFolder = await client.GetFolderAsync(fromPath, ct);
            var dstFolder = await client.GetFolderAsync(toPath, ct);
            await srcFolder.OpenAsync(FolderAccess.ReadWrite, ct);
            uidMap = await srcFolder.MoveToAsync(uids, dstFolder, ct);
            await srcFolder.CloseAsync(false, ct);
        }, ct);

        // Best-effort cache update: move folder links from source to destination
        try
        {
            var srcFolder = folderRepo.GetByPath(operation.AccountId, fromPath);
            var dstFolder = folderRepo.GetByPath(operation.AccountId, toPath);
            if (srcFolder is null || dstFolder is null) return;

            foreach (var uid in uids)
            {
                var msg = messageRepo.GetByUid(operation.AccountId, srcFolder.Id, uid.Id);
                if (msg is null) continue;

                // Remove old folder link
                messageRepo.UnlinkFromFolder(msg.Id, srcFolder.Id);

                // Determine new UID in destination folder
                long newUid = 0;
                if (uidMap is not null)
                {
                    var mapped = uidMap.FirstOrDefault(m => m.Key.Id == uid.Id);
                    if (mapped.Value.IsValid)
                        newUid = mapped.Value.Id;
                }

                // Add new folder link (use mapped UID if available, otherwise use 0 as placeholder)
                if (newUid > 0)
                    messageRepo.LinkToFolder(msg.Id, dstFolder.Id, newUid);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MoveExecutor] Cache update warning: {ex.Message}");
        }
    }
}
