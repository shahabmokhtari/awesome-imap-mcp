using System.Text.Json;
using MailKit;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public class FlagExecutor(AccountRepository accountRepo, CredentialEncryptor encryptor,
    IOAuthAccessTokenProvider oauthProvider,
    MessageRepository messageRepo, FolderRepository folderRepo,
    ILogger<FlagExecutor> logger) : IOperationExecutor
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

        // Best-effort cache update: reflect the flag change in the local DB
        try
        {
            var flagStr = flags switch
            {
                MessageFlags.Seen => "\\Seen",
                MessageFlags.Flagged => "\\Flagged",
                _ => null
            };
            if (flagStr is null) return;

            var folderRecord = folderRepo.GetByPath(operation.AccountId, folderPath);
            if (folderRecord is null) return;

            foreach (var uid in uids)
            {
                var msg = messageRepo.GetByUid(operation.AccountId, folderRecord.Id, uid.Id);
                if (msg is null) continue;

                var currentFlags = ParseFlags(msg.Flags);
                if (add)
                    currentFlags.Add(flagStr);
                else
                    currentFlags.Remove(flagStr);

                var newFlagsStr = currentFlags.Count > 0 ? string.Join(" ", currentFlags) : null;
                messageRepo.UpdateFlags(msg.Id, newFlagsStr);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache update failed after flag operation");
        }
    }

    private static HashSet<string> ParseFlags(string? flags)
    {
        if (string.IsNullOrWhiteSpace(flags))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(flags.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
    }
}
