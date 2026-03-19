using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.ImapClient;

public sealed class SmtpConnectionManager : IDisposable
{
    private readonly AccountConfig _config;
    private SmtpClient? _client;
    private bool _disposed;

    public SmtpConnectionManager(AccountConfig config) { _config = config; }

    public async Task SendAsync(MimeMessage message, CancellationToken ct = default)
    {
        var client = await GetConnectedClientAsync(ct);
        await client.SendAsync(message, ct);
    }

    private async Task<SmtpClient> GetConnectedClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
            return _client;

        _client?.Dispose();
        _client = new SmtpClient();

        var smtpHost = _config.SmtpHost ?? _config.ImapHost.Replace("imap.", "smtp.");
        var smtpPort = _config.SmtpPort;
        var options = _config.SmtpUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

        await _client.ConnectAsync(smtpHost, smtpPort, options, ct);
        await _client.AuthenticateAsync(_config.Username, _config.Password ?? "", ct);
        return _client;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
    }
}
