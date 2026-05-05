using System.Text.Json;
using MailKit;
using Microsoft.Extensions.Logging;
using AwesomeImapMcp.Core.Encryption;
using AwesomeImapMcp.Core.OAuth;
using AwesomeImapMcp.ImapClient;
using AwesomeImapMcp.ImapClient.Repositories;
using AwesomeImapMcp.Queue.Models;

namespace AwesomeImapMcp.Queue.Executors;

public class DeleteExecutor(AccountRepository accountRepo, CredentialEncryptor encryptor,
    IOAuthAccessTokenProvider oauthProvider,
    MessageRepository messageRepo, FolderRepository folderRepo,
    ILogger<DeleteExecutor> logger) : IOperationExecutor
{
    public IReadOnlyList<string> SupportedOperations { get; } = ["delete", "bulkdelete"];

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var uids = payload.GetProperty("uids").EnumerateArray()
            .Select(u => new UniqueId((uint)u.GetInt32())).ToList();
        var folderPath = payload.GetProperty("folder").GetString()!;

        var record = accountRepo.ResolveAccount(operation.AccountId)
            ?? throw new InvalidOperationException($"Account '{operation.AccountId}' not found in database.");
        var accountConfig = AccountConfigMapper.ToAccountConfig(record, encryptor);
        using var connMgr = new ImapConnectionManager(accountConfig, encryptor,
            oauthProvider: oauthProvider, accountId: record.Id);
        await connMgr.ExecuteAsync(async client =>
        {
            var folder = await client.GetFolderAsync(folderPath, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            await folder.AddFlagsAsync(uids, MessageFlags.Deleted, true, ct);
            await folder.ExpungeAsync(ct);
            await folder.CloseAsync(false, ct);
        }, ct);

        // Best-effort cache update: soft-delete the messages in the local DB
        try
        {
            var folderRecord = folderRepo.GetByPath(operation.AccountId, folderPath);
            if (folderRecord is null) return;

            var uidLongs = uids.Select(u => (long)u.Id);
            messageRepo.SoftDeleteByUids(operation.AccountId, folderRecord.Id, uidLongs);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache update failed after delete operation");
        }
    }
}
