using System.Text.Json;
using MailKit;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public class FlagExecutor(AccountRepository accountRepo, CredentialEncryptor encryptor,
    IOAuthAccessTokenProvider oauthProvider) : IOperationExecutor
{
    public IReadOnlyList<string> SupportedOperations { get; } = ["markread", "markunread", "flag", "unflag"];

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var uids = payload.GetProperty("uids").EnumerateArray()
            .Select(u => new UniqueId((uint)u.GetInt32())).ToList();
        var folderPath = payload.GetProperty("folder").GetString()!;

        var (flags, add) = operation.Operation switch
        {
            "markread" => (MessageFlags.Seen, true),
            "markunread" => (MessageFlags.Seen, false),
            "flag" => (MessageFlags.Flagged, true),
            "unflag" => (MessageFlags.Flagged, false),
            _ => throw new InvalidOperationException($"Unknown flag operation: {operation.Operation}")
        };

        var record = accountRepo.ResolveAccount(operation.AccountId)
            ?? throw new InvalidOperationException($"Account '{operation.AccountId}' not found in database.");
        var accountConfig = AccountConfigMapper.ToAccountConfig(record, encryptor);
        using var connMgr = new ImapConnectionManager(accountConfig, encryptor,
            oauthProvider: oauthProvider, accountId: record.Id);
        await connMgr.ExecuteAsync(async client =>
        {
            var folder = await client.GetFolderAsync(folderPath, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            if (add)
                await folder.AddFlagsAsync(uids, flags, true, ct);
            else
                await folder.RemoveFlagsAsync(uids, flags, true, ct);
            await folder.CloseAsync(false, ct);
        }, ct);
    }
}
