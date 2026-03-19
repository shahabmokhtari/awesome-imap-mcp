using System.Net.Sockets;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace UltimateImapMcp.ImapClient;

/// <summary>
/// Manages a single authenticated IMAP connection for an account.
/// Includes reconnection with exponential backoff on transient failures.
/// </summary>
public sealed class ImapConnectionManager : IDisposable
{
    private readonly AccountConfig _config;
    private readonly CredentialEncryptor _encryptor;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private ImapClientLib? _client;
    private bool _disposed;

    /// <summary>Maximum number of reconnection attempts before giving up.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>Maximum backoff delay between retries.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);

    public ImapConnectionManager(AccountConfig config, CredentialEncryptor encryptor,
        ILogger<ImapConnectionManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(encryptor);
        _config = config;
        // Phase 1: _encryptor is injected but intentionally not used here.
        // Passwords are read directly from config.Password (plaintext from the config
        // file with environment-variable substitution applied by ConfigLoader).
        // Encrypted credential support — calling _encryptor.Decrypt() on the
        // credentials_enc column from the database — will be wired up in Phase 4
        // when the Dashboard's account-management UI is implemented.
        _encryptor = encryptor;
        _logger = logger ?? NullLogger<ImapConnectionManager>.Instance;
    }

    /// <summary>
    /// Returns an authenticated ImapClient, creating or reconnecting as needed.
    /// Thread-safe: serialised through a semaphore.
    /// Retries with exponential backoff (1s, 2s, 4s, 8s, ...) on transient failures.
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
                try { _client.Dispose(); } catch (Exception ex) { Console.Error.WriteLine($"[ImapConnectionManager] Cleanup failed: {ex.Message}"); }
                _client = null;
            }

            // Retry loop with exponential backoff.
            var password = _config.Password
                ?? throw new InvalidOperationException(
                    $"No password configured for account '{_config.Username}'. Set password in config or via environment variable.");

            var delaySeconds = 1;
            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                ImapClientLib? client = null;
                try
                {
                    client = new ImapClientLib();

                    await client.ConnectAsync(
                        _config.ImapHost,
                        _config.ImapPort,
                        SecureSocketOptions.SslOnConnect,
                        ct).ConfigureAwait(false);

                    await client.AuthenticateAsync(
                        _config.Username,
                        password,
                        ct).ConfigureAwait(false);

                    if (attempt > 1)
                    {
                        _logger.LogInformation(
                            "Reconnected to {Host}:{Port} for {Account} on attempt {Attempt}",
                            _config.ImapHost, _config.ImapPort, _config.Username, attempt);
                    }

                    _client = client;
                    return _client;
                }
                catch (Exception ex) when (
                    ex is ImapProtocolException or IOException or SocketException
                    && attempt < MaxRetries)
                {
                    try { client?.Dispose(); } catch (Exception disposeEx) { Console.Error.WriteLine($"[ImapConnectionManager] Cleanup failed: {disposeEx.Message}"); }

                    var delay = TimeSpan.FromSeconds(Math.Min(delaySeconds, MaxBackoff.TotalSeconds));
                    _logger.LogWarning(
                        "Connection attempt {Attempt}/{MaxRetries} to {Host}:{Port} failed ({Error}). Retrying in {Delay}s...",
                        attempt, MaxRetries, _config.ImapHost, _config.ImapPort,
                        ex.GetType().Name + ": " + ex.Message, delay.TotalSeconds);

                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delaySeconds *= 2;
                }
                catch
                {
                    try { client?.Dispose(); } catch (Exception disposeEx) { Console.Error.WriteLine($"[ImapConnectionManager] Cleanup failed: {disposeEx.Message}"); }
                    throw;
                }
            }

            // Should not be reached (loop covers MaxRetries attempts), but satisfies compiler.
            throw new IOException(
                $"Failed to connect to {_config.ImapHost}:{_config.ImapPort} after {MaxRetries} attempts.");
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

        try { _client?.Dispose(); } catch (Exception ex) { Console.Error.WriteLine($"[ImapConnectionManager] Cleanup failed: {ex.Message}"); }
        _semaphore.Dispose();
    }
}
