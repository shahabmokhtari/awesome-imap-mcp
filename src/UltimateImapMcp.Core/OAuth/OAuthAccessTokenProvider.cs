using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;

namespace UltimateImapMcp.Core.OAuth;

/// <summary>
/// Provides valid OAuth2 access tokens, automatically refreshing expired ones.
/// Uses per-account SemaphoreSlim to prevent concurrent refreshes.
/// </summary>
public sealed class OAuthAccessTokenProvider(
    OAuthTokenRepository tokenRepo,
    OAuthTokenService tokenService,
    CredentialEncryptor encryptor,
    AppConfig config,
    ILogger<OAuthAccessTokenProvider> logger) : IOAuthAccessTokenProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>Margin before actual expiry at which we refresh proactively.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    public async Task<string?> GetAccessTokenAsync(string accountId, CancellationToken ct = default)
    {
        var record = tokenRepo.GetByAccountId(accountId);
        if (record is null)
            return null;

        // Check if existing access token is still valid
        if (record.AccessTokenEnc is not null && record.TokenExpiry is not null)
        {
            if (DateTime.TryParse(record.TokenExpiry, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry)
                && expiry > DateTime.UtcNow + RefreshMargin)
            {
                return encryptor.Decrypt(record.AccessTokenEnc);
            }
        }

        // Need to refresh — acquire per-account lock
        var sem = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock (another thread may have refreshed)
            record = tokenRepo.GetByAccountId(accountId);
            if (record is null)
                return null;

            if (record.AccessTokenEnc is not null && record.TokenExpiry is not null)
            {
                if (DateTime.TryParse(record.TokenExpiry, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry2)
                    && expiry2 > DateTime.UtcNow + RefreshMargin)
                {
                    return encryptor.Decrypt(record.AccessTokenEnc);
                }
            }

            // Resolve effective config for the provider
            var effectiveConfig = OAuthProviderDefaults.GetEffective(record.Provider, config);
            if (effectiveConfig is null)
            {
                // Fall back to stored client_id/secret
                effectiveConfig = new OAuthProviderConfig
                {
                    ClientId = record.ClientId,
                    ClientSecret = record.ClientSecretEnc is not null
                        ? encryptor.Decrypt(record.ClientSecretEnc)
                        : null,
                    TokenUrl = OAuthProviderDefaults.GetAvailableProviders(config)
                        .Where(p => p.Key.Equals(record.Provider, StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.Value?.TokenUrl)
                        .FirstOrDefault()
                };

                if (string.IsNullOrEmpty(effectiveConfig.TokenUrl))
                {
                    throw new InvalidOperationException(
                        $"Cannot refresh OAuth token for account '{accountId}': no token_url configured for provider '{record.Provider}'. " +
                        "Add a token_url to the oauth_providers section of your config.");
                }
            }

            var refreshToken = encryptor.Decrypt(record.RefreshTokenEnc);

            logger.LogDebug("Refreshing OAuth token for account {AccountId} (provider: {Provider})",
                accountId, record.Provider);

            var tokenResponse = await tokenService.RefreshTokenAsync(effectiveConfig, refreshToken, ct)
                .ConfigureAwait(false);

            // Store updated tokens
            var newAccessTokenEnc = encryptor.Encrypt(tokenResponse.AccessToken);
            var newExpiry = tokenResponse.ExpiresIn.HasValue
                ? DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn.Value)
                    .ToString("o", CultureInfo.InvariantCulture)
                : null;

            // If the refresh response includes a new refresh token, update it too
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                var newRefreshTokenEnc = encryptor.Encrypt(tokenResponse.RefreshToken);
                tokenRepo.Upsert(accountId, record.Provider, record.ClientId,
                    record.ClientSecretEnc, newRefreshTokenEnc, newAccessTokenEnc,
                    newExpiry, record.Scopes, record.Email,
                    apiDomain: tokenResponse.ApiDomain);
            }
            else
            {
                tokenRepo.UpdateAccessToken(accountId, newAccessTokenEnc, newExpiry,
                    apiDomain: tokenResponse.ApiDomain);
            }

            return tokenResponse.AccessToken;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Transient network error refreshing OAuth token for account {AccountId} — caller will retry", accountId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh OAuth token for account {AccountId}", accountId);
            throw new InvalidOperationException(
                $"OAuth token refresh failed for account '{accountId}': {ex.Message}", ex);
        }
        finally
        {
            sem.Release();
        }
    }

    public void Dispose()
    {
        foreach (var (_, sem) in _locks)
            sem.Dispose();
        _locks.Clear();
    }
}
