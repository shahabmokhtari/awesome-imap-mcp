using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.OAuth;

namespace UltimateImapMcp.ImapClient;

public sealed class SmtpConnectionManager : IDisposable
{
    private readonly AccountConfig _config;
    private readonly ILogger _logger;
    private readonly IOAuthAccessTokenProvider? _oauthProvider;
    private readonly string? _accountId;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private SmtpClient? _client;
    private bool _disposed;

    public SmtpConnectionManager(AccountConfig config, ILogger? logger = null,
        IOAuthAccessTokenProvider? oauthProvider = null, string? accountId = null)
    {
        _config = config;
        _logger = logger ?? NullLogger.Instance;
        _oauthProvider = oauthProvider;
        _accountId = accountId;
    }

    public async Task SendAsync(MimeMessage message, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var client = await GetConnectedClientAsync(ct);
            await client.SendAsync(message, ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<SmtpClient> GetConnectedClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true, IsAuthenticated: true })
            return _client;

        _client?.Dispose();
        _client = null;

        var smtpHost = _config.SmtpHost ?? _config.ImapHost.Replace("imap.", "smtp.");
        var smtpPort = _config.SmtpPort;
        var options = _config.SmtpUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

        const int maxRetries = 2;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            SmtpClient? client = null;
            try
            {
                client = new SmtpClient();
                await client.ConnectAsync(smtpHost, smtpPort, options, ct);

                if (_config.AuthType.Equals("oauth2", StringComparison.OrdinalIgnoreCase)
                    && _oauthProvider is not null && _accountId is not null)
                {
                    var accessToken = await _oauthProvider.GetAccessTokenAsync(_accountId, ct)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            $"No OAuth access token available for SMTP account '{_config.Username}'.");
                    var oauth2 = new SaslMechanismOAuth2(_config.Username, accessToken);
                    await client.AuthenticateAsync(oauth2, ct);
                }
                else
                {
                    var password = _config.Password ?? throw new InvalidOperationException($"No password configured for SMTP account '{_config.Username}'.");
                    await client.AuthenticateAsync(_config.Username, password, ct);
                }

                if (!client.IsAuthenticated)
                {
                    throw new AuthenticationException($"SMTP authentication failed for {_config.Username}");
                }

                if (attempt > 1)
                {
                    _logger.LogInformation("SMTP reconnected to {Host}:{Port} on attempt {Attempt}",
                        smtpHost, smtpPort, attempt);
                }

                _client = client;
                return _client;
            }
            catch (Exception ex) when (
                ex is IOException or SocketException or SmtpProtocolException
                && attempt < maxRetries)
            {
                try { client?.Dispose(); } catch (Exception disposeEx) { Console.Error.WriteLine($"[SmtpConnectionManager] Cleanup failed: {disposeEx.Message}"); }

                _logger.LogWarning(
                    "SMTP connection attempt {Attempt}/{MaxRetries} to {Host}:{Port} failed ({Error}). Retrying...",
                    attempt, maxRetries, smtpHost, smtpPort,
                    ex.GetType().Name + ": " + ex.Message);

                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch
            {
                try { client?.Dispose(); } catch (Exception disposeEx) { Console.Error.WriteLine($"[SmtpConnectionManager] Cleanup failed: {disposeEx.Message}"); }
                throw;
            }
        }

        // Should not be reached, but satisfies compiler.
        throw new IOException($"Failed to connect to SMTP {smtpHost}:{smtpPort} after {maxRetries} attempts.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
        _semaphore.Dispose();
    }
}
