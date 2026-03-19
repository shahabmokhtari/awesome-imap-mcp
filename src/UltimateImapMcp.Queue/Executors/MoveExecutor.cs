using System.Text.Json;
using MailKit;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.Queue.Models;

using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace UltimateImapMcp.Queue.Executors;

public class MoveExecutor(AppConfig config) : IOperationExecutor
{
    public string OperationType => "move";

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var uids = payload.GetProperty("uids").EnumerateArray()
            .Select(u => new UniqueId((uint)u.GetInt32())).ToList();
        var fromPath = payload.GetProperty("from_folder").GetString()!;
        var toPath = payload.GetProperty("to_folder").GetString()!;

        var accountConfig = config.Accounts.First(a =>
            a.Name.Equals(operation.AccountId, StringComparison.OrdinalIgnoreCase));
        var encryptor = Core.Encryption.CredentialEncryptor.FromMachineId();
        using var connMgr = new ImapConnectionManager(accountConfig, encryptor);
        var client = await connMgr.GetConnectedClientAsync(ct);

        var srcFolder = await client.GetFolderAsync(fromPath, ct);
        var dstFolder = await client.GetFolderAsync(toPath, ct);
        await srcFolder.OpenAsync(FolderAccess.ReadWrite, ct);
        await srcFolder.MoveToAsync(uids, dstFolder, ct);
        await srcFolder.CloseAsync(false, ct);
    }
}
