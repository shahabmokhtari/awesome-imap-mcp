using System.Text.Json;
using MailKit;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.Queue.Models;

using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace UltimateImapMcp.Queue.Executors;

public class FlagExecutor(AppConfig config, CredentialEncryptor encryptor) : IOperationExecutor
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

        var accountConfig = config.Accounts.FirstOrDefault(a =>
            a.Name.Equals(operation.AccountId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Account '{operation.AccountId}' not found in configuration.");
        using var connMgr = new ImapConnectionManager(accountConfig, encryptor);
        var client = await connMgr.GetConnectedClientAsync(ct);

        var folder = await client.GetFolderAsync(folderPath, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        if (add)
            await folder.AddFlagsAsync(uids, flags, true, ct);
        else
            await folder.RemoveFlagsAsync(uids, flags, true, ct);
        await folder.CloseAsync(false, ct);
    }
}
