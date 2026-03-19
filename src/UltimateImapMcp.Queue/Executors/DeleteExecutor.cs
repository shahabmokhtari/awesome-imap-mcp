using System.Text.Json;
using MailKit;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.Queue.Models;

using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace UltimateImapMcp.Queue.Executors;

public class DeleteExecutor(AppConfig config) : IOperationExecutor
{
    public string OperationType => "delete";

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var uids = payload.GetProperty("uids").EnumerateArray()
            .Select(u => new UniqueId((uint)u.GetInt32())).ToList();
        var folderPath = payload.GetProperty("folder").GetString()!;

        var accountConfig = config.Accounts.FirstOrDefault(a =>
            a.Name.Equals(operation.AccountId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Account '{operation.AccountId}' not found in configuration.");
        var encryptor = Core.Encryption.CredentialEncryptor.FromMachineId();
        using var connMgr = new ImapConnectionManager(accountConfig, encryptor);
        var client = await connMgr.GetConnectedClientAsync(ct);

        var folder = await client.GetFolderAsync(folderPath, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        await folder.AddFlagsAsync(uids, MessageFlags.Deleted, true, ct);
        await folder.ExpungeAsync(ct);
        await folder.CloseAsync(false, ct);
    }
}
