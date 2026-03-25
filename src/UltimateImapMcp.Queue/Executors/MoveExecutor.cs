using System.Text.Json;
using MailKit;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public class MoveExecutor(AccountRepository accountRepo, CredentialEncryptor encryptor,
    IOAuthAccessTokenProvider oauthProvider) : IOperationExecutor
{
    public IReadOnlyList<string> SupportedOperations { get; } = ["move", "bulkmove"];

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var uids = payload.GetProperty("uids").EnumerateArray()
            .Select(u => new UniqueId((uint)u.GetInt32())).ToList();
        var fromPath = payload.GetProperty("from_folder").GetString()!;
        var toPath = payload.GetProperty("to_folder").GetString()!;

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
            await srcFolder.MoveToAsync(uids, dstFolder, ct);
            await srcFolder.CloseAsync(false, ct);
        }, ct);
    }
}
