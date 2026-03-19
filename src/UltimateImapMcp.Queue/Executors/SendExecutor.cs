using System.Text.Json;
using MimeKit;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public class SendExecutor(AppConfig config) : IOperationExecutor
{
    public IReadOnlyList<string> SupportedOperations { get; } = ["send", "reply", "forward"];

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var accountConfig = config.Accounts.FirstOrDefault(a =>
            a.Name.Equals(operation.AccountId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Account '{operation.AccountId}' not found in configuration.");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(accountConfig.Username));
        message.To.Add(MailboxAddress.Parse(payload.GetProperty("to").GetString()!));

        if (payload.TryGetProperty("cc", out var cc) && cc.ValueKind == JsonValueKind.String)
            message.Cc.Add(MailboxAddress.Parse(cc.GetString()!));

        message.Subject = payload.GetProperty("subject").GetString() ?? "";

        var body = payload.GetProperty("body").GetString() ?? "";
        message.Body = new TextPart("plain") { Text = body };

        if (payload.TryGetProperty("in_reply_to", out var irt) && irt.ValueKind == JsonValueKind.String)
            message.InReplyTo = irt.GetString();

        if (payload.TryGetProperty("references", out var refs) && refs.ValueKind == JsonValueKind.String)
            foreach (var r in refs.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                message.References.Add(r);

        using var smtp = new SmtpConnectionManager(accountConfig);
        await smtp.SendAsync(message, ct);
    }
}
