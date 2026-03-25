using System.Net.Sockets;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
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
    private readonly IOAuthAccessTokenProvider? _oauthProvider;
    private readonly string? _accountId;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private ImapClientLib? _client;
    private bool _disposed;

    /// <summary>Maximum number of reconnection attempts before giving up.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>Maximum backoff delay between retries.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);

    public ImapConnectionManager(AccountConfig config, CredentialEncryptor encryptor,
        ILogger<ImapConnectionManager>? logger = null,
        IOAuthAccessTokenProvider? oauthProvider = null, string? accountId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(encryptor);
        _config = config;
        _encryptor = encryptor;
        _oauthProvider = oauthProvider;
        _accountId = accountId;
        _logger = logger ?? NullLogger<ImapConnectionManager>.Instance;
    }

    /// <summary>
    /// Executes an async operation on the IMAP client with exclusive access.
    /// The connection is created/reconnected as needed.
    /// Thread-safe: the semaphore is held for the entire duration of the operation,
    /// preventing concurrent IMAP commands that cause MailKit threading errors.
    /// Prefer this over GetConnectedClientAsync for all new code.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<ImapClientLib, Task<T>> operation, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = await EnsureConnectedInternalAsync(ct).ConfigureAwait(false);
            return await operation(client).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Executes an async operation on the IMAP client with exclusive access (no return value).
    /// </summary>
    public async Task ExecuteAsync(Func<ImapClientLib, Task> operation, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = await EnsureConnectedInternalAsync(ct).ConfigureAwait(false);
            await operation(client).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Returns an authenticated ImapClient, creating or reconnecting as needed.
    /// Thread-safe: serialised through a semaphore for connection setup only.
    /// WARNING: The returned client is NOT locked for subsequent operations.
    /// Use ExecuteAsync for thread-safe command execution.
    /// </summary>
    public async Task<ImapClientLib> GetConnectedClientAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await EnsureConnectedInternalAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Ensures the client is connected and authenticated. Assumes the semaphore is already held.
    /// </summary>
    private async Task<ImapClientLib> EnsureConnectedInternalAsync(CancellationToken ct)
    {
        // Reuse existing connection if still healthy.
        if (_client is { IsConnected: true, IsAuthenticated: true })
            return _client;

        // Tear down stale client.
        if (_client is not null)
        {
            try { _client.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "IMAP client cleanup failed (non-fatal)"); }
            _client = null;
        }

        // Retry loop with exponential backoff.
        var delaySeconds = 1;
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            ImapClientLib? client = null;
            try
            {
                client = new ImapClientLib();

                _logger.LogDebug(
                    "Attempting connection to {Host}:{Port} for {Account} (attempt {Attempt}/{MaxRetries})",
                    _config.ImapHost, _config.ImapPort, _config.Username, attempt, MaxRetries);

                await client.ConnectAsync(
                    _config.ImapHost, _config.ImapPort,
                    SecureSocketOptions.SslOnConnect, ct).ConfigureAwait(false);

                if (_config.AuthType.Equals("oauth2", StringComparison.OrdinalIgnoreCase))
                {
                    if (_oauthProvider is null || _accountId is null)
                        throw new InvalidOperationException(
                            $"OAuth2 configured for '{_config.Username}' but OAuth provider was not supplied.");

                    var accessToken = await _oauthProvider.GetAccessTokenAsync(_accountId, ct)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            $"No OAuth access token available for account '{_config.Username}'.");
                    var oauth2 = new SaslMechanismOAuth2(_config.Username, accessToken);
                    await client.AuthenticateAsync(oauth2, ct).ConfigureAwait(false);
                }
                else
                {
                    var password = _config.Password
                        ?? throw new InvalidOperationException(
                            $"No password configured for account '{_config.Username}'.");
                    await client.AuthenticateAsync(_config.Username, password, ct).ConfigureAwait(false);
                }

                _logger.LogInformation(
                    attempt > 1
                        ? "Reconnected to {Host}:{Port} for {Account} on attempt {Attempt}"
                        : "Connected to {Host}:{Port} for {Account}",
                    _config.ImapHost, _config.ImapPort, _config.Username, attempt);

                _client = client;
                return _client;
            }
            catch (Exception ex) when (
                ex is ImapProtocolException or IOException or SocketException
                && attempt < MaxRetries)
            {
                try { client?.Dispose(); } catch (Exception cleanupEx) { _logger.LogDebug(cleanupEx, "IMAP client cleanup failed (non-fatal)"); }
                var delay = TimeSpan.FromSeconds(Math.Min(delaySeconds, MaxBackoff.TotalSeconds));
                _logger.LogWarning(
                    "Connection attempt {Attempt}/{MaxRetries} to {Host}:{Port} failed ({Error}). Retrying in {Delay}s...",
                    attempt, MaxRetries, _config.ImapHost, _config.ImapPort,
                    ex.GetType().Name + ": " + ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delaySeconds *= 2;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Connection to {Host}:{Port} for {Account} failed permanently",
                    _config.ImapHost, _config.ImapPort, _config.Username);
                try { client?.Dispose(); } catch (Exception cleanupEx) { _logger.LogDebug(cleanupEx, "IMAP client cleanup failed (non-fatal)"); }
                throw;
            }
        }

        throw new IOException(
            $"Failed to connect to {_config.ImapHost}:{_config.ImapPort} after {MaxRetries} attempts.");
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

        try { _client?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "IMAP client cleanup failed (non-fatal)"); }
        _semaphore.Dispose();
    }
}
