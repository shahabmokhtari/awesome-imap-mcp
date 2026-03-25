using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UltimateImapMcp.Core.OAuth;

/// <summary>
/// Background service that proactively refreshes OAuth tokens approaching expiry.
/// Runs every 5 minutes and refreshes tokens expiring within 10 minutes.
/// </summary>
public sealed class OAuthTokenRefreshService(
    OAuthTokenRepository tokenRepo,
    IOAuthAccessTokenProvider tokenProvider,
    ILogger<OAuthTokenRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExpiryThreshold = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("OAuth token refresh service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RefreshExpiringTokensAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error during scheduled OAuth token refresh");
            }
        }
    }

    private async Task RefreshExpiringTokensAsync(CancellationToken ct)
    {
        var allTokens = tokenRepo.GetAll();
        var threshold = DateTime.UtcNow + ExpiryThreshold;

        foreach (var record in allTokens)
        {
            ct.ThrowIfCancellationRequested();

            // Check if token is expiring soon
            if (record.TokenExpiry is not null)
            {
                if (DateTime.TryParse(record.TokenExpiry, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry)
                    && expiry > threshold)
                {
                    // Token is still valid, skip
                    continue;
                }
            }

            // Token is expired or expiring soon — refresh it
            try
            {
                logger.LogDebug("Proactively refreshing OAuth token for account {AccountId}",
                    record.AccountId);

                // GetAccessTokenAsync handles the refresh logic internally
                await tokenProvider.GetAccessTokenAsync(record.AccountId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to proactively refresh OAuth token for account {AccountId}",
                    record.AccountId);
            }
        }
    }
}
