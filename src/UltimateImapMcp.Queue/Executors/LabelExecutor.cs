using System.Text.Json;
using MailKit;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.Core.Repositories;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public class LabelExecutor(AccountRepository accountRepo, CredentialEncryptor encryptor,
    IOAuthAccessTokenProvider oauthProvider,
    MessageRepository messageRepo, FolderRepository folderRepo,
    LocalLabelRepository localLabelRepo,
    ILogger<LabelExecutor> logger) : IOperationExecutor
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

        var usedImap = false;
        await connMgr.ExecuteAsync(async client =>
        {
            var folder = await client.GetFolderAsync(folderPath, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            try
            {
                if (folder.PermanentFlags.HasFlag(MessageFlags.UserDefined))
                {
                    // Server supports custom keywords — use IMAP STORE
                    var keywords = new HashSet<string> { label };
                    if (add)
                        await folder.AddFlagsAsync(uids, MessageFlags.None, keywords, true, ct);
                    else
                        await folder.RemoveFlagsAsync(uids, MessageFlags.None, keywords, true, ct);
                    usedImap = true;
                }
                else
                {
                    logger.LogInformation(
                        "IMAP server for {AccountId} doesn't support keywords — using local label storage for '{Label}'",
                        operation.AccountId, label);
                }
            }
            finally
            {
                try { await folder.CloseAsync(false, ct); }
                catch (Exception ex) when (ex is MailKit.ServiceNotConnectedException
                    or MailKit.ServiceNotAuthenticatedException
                    or IOException or OperationCanceledException) { }
            }
        }, ct);

        // For accounts without IMAP keyword support: persist in local labels DB
        if (!usedImap)
        {
            var folderRecord = folderRepo.GetByPath(operation.AccountId, folderPath);
            if (folderRecord is not null)
            {
                foreach (var uid in uids)
                {
                    var msg = messageRepo.GetByUid(operation.AccountId, folderRecord.Id, uid.Id);
                    if (msg?.MessageId is null) continue;
                    if (add)
                        localLabelRepo.AddLabel(operation.AccountId, msg.MessageId, label);
                    else
                        localLabelRepo.RemoveLabel(operation.AccountId, msg.MessageId, label);
                }
            }
        }

        // Best-effort cache update: reflect the label change in the local message cache
        try
        {
            var folderRecord = folderRepo.GetByPath(operation.AccountId, folderPath);
            if (folderRecord is null) return;

            foreach (var uid in uids)
            {
                var msg = messageRepo.GetByUid(operation.AccountId, folderRecord.Id, uid.Id);
                if (msg is null) continue;

                var currentFlags = ParseFlags(msg.Flags);
                if (add)
                    currentFlags.Add(label);
                else
                    currentFlags.Remove(label);

                var newFlagsStr = currentFlags.Count > 0 ? string.Join(" ", currentFlags) : null;
                messageRepo.UpdateFlags(msg.Id, newFlagsStr);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache update failed after label operation");
        }
    }

    private static HashSet<string> ParseFlags(string? flags)
    {
        if (string.IsNullOrWhiteSpace(flags))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(flags.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
    }
}
