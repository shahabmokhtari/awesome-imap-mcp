using System.Text.Json;
using Microsoft.Extensions.Logging;
using MimeKit;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public class SendExecutor(AccountRepository accountRepo, CredentialEncryptor encryptor,
    IOAuthAccessTokenProvider oauthProvider,
    ILogger<SendExecutor> logger) : IOperationExecutor
{
    public IReadOnlyList<string> SupportedOperations { get; } = ["send", "reply", "forward"];

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var record = accountRepo.ResolveAccount(operation.AccountId)
            ?? throw new InvalidOperationException($"Account '{operation.AccountId}' not found in database.");
        var accountConfig = AccountConfigMapper.ToAccountConfig(record, encryptor);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(accountConfig.Username));
        message.To.AddRange(InternetAddressList.Parse(payload.GetProperty("to").GetString()!));

        if (payload.TryGetProperty("cc", out var cc) && cc.ValueKind == JsonValueKind.String)
            message.Cc.AddRange(InternetAddressList.Parse(cc.GetString()!));

        if (payload.TryGetProperty("bcc", out var bcc) && bcc.ValueKind == JsonValueKind.String)
            message.Bcc.AddRange(InternetAddressList.Parse(bcc.GetString()!));

        message.Subject = payload.GetProperty("subject").GetString() ?? "";

        var body = payload.GetProperty("body").GetString() ?? "";
        message.Body = new TextPart("plain") { Text = body };

        if (payload.TryGetProperty("in_reply_to", out var irt) && irt.ValueKind == JsonValueKind.String)
            message.InReplyTo = irt.GetString();

        if (payload.TryGetProperty("references", out var refs) && refs.ValueKind == JsonValueKind.String)
            foreach (var r in refs.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                message.References.Add(r);

        using var smtp = new SmtpConnectionManager(accountConfig, logger,
            oauthProvider, record.Id);
        await smtp.SendAsync(message, ct);
    }
}
