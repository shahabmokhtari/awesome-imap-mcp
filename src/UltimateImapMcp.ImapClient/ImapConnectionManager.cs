using MailKit.Security;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace UltimateImapMcp.ImapClient;

/// <summary>
/// Manages a single authenticated IMAP connection for an account.
/// Phase 1: one connection per manager. Pooling + backoff deferred to Phase 3.
/// </summary>
public sealed class ImapConnectionManager : IDisposable
{
    private readonly AccountConfig _config;
    private readonly CredentialEncryptor _encryptor;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private ImapClientLib? _client;
    private bool _disposed;

    public ImapConnectionManager(AccountConfig config, CredentialEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(encryptor);
        _config = config;
        _encryptor = encryptor;
    }

    /// <summary>
    /// Returns an authenticated ImapClient, creating or reconnecting as needed.
    /// Thread-safe: serialised through a semaphore.
    /// </summary>
    public async Task<ImapClientLib> GetConnectedClientAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Reuse existing connection if still healthy.
            if (_client is { IsConnected: true, IsAuthenticated: true })
                return _client;

            // Tear down stale client.
            if (_client is not null)
            {
                try { _client.Dispose(); } catch { /* best-effort */ }
                _client = null;
            }

            var client = new ImapClientLib();

            await client.ConnectAsync(
                _config.ImapHost,
                _config.ImapPort,
                SecureSocketOptions.SslOnConnect,
                ct).ConfigureAwait(false);

            var password = _config.Password ?? string.Empty;

            await client.AuthenticateAsync(
                _config.Username,
                password,
                ct).ConfigureAwait(false);

            _client = client;
            return _client;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>Gracefully disconnects the underlying IMAP client.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true })
            {
                await _client.DisconnectAsync(true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _client?.Dispose(); } catch { /* best-effort */ }
        _semaphore.Dispose();
    }
}
