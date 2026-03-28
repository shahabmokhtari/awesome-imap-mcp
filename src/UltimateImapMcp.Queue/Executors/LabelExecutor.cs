using System.Text.Json;
using MailKit;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public class LabelExecutor(AccountRepository accountRepo, CredentialEncryptor encryptor,
    IOAuthAccessTokenProvider oauthProvider) : IOperationExecutor
{
    public IReadOnlyList<string> SupportedOperations { get; } = ["label", "unlabel"];

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var uids = payload.GetProperty("uids").EnumerateArray()
            .Select(u => new UniqueId((uint)u.GetInt32())).ToList();
        var folderPath = payload.GetProperty("folder").GetString()!;
        var label = payload.GetProperty("label").GetString()!;
        var add = operation.Operation.Equals("label", StringComparison.OrdinalIgnoreCase);

        var record = accountRepo.ResolveAccount(operation.AccountId)
            ?? throw new InvalidOperationException($"Account '{operation.AccountId}' not found in database.");
        var accountConfig = AccountConfigMapper.ToAccountConfig(record, encryptor);
        using var connMgr = new ImapConnectionManager(accountConfig, encryptor,
            oauthProvider: oauthProvider, accountId: record.Id);
        await connMgr.ExecuteAsync(async client =>
        {
            var folder = await client.GetFolderAsync(folderPath, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            var keywords = new HashSet<string> { label };
            if (add)
                await folder.AddFlagsAsync(uids, MessageFlags.None, keywords, true, ct);
            else
                await folder.RemoveFlagsAsync(uids, MessageFlags.None, keywords, true, ct);
            await folder.CloseAsync(false, ct);
        }, ct);
    }
}
