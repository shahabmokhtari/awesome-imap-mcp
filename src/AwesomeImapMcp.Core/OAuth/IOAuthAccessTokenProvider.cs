namespace AwesomeImapMcp.Core.OAuth;

/// <summary>
/// Provides valid OAuth2 access tokens for accounts, handling refresh transparently.
/// </summary>
public interface IOAuthAccessTokenProvider
{
    /// <summary>
    /// Returns a valid access token for the given account, refreshing if needed.
    /// Returns null if the account has no OAuth token record.
    /// </summary>
    Task<string?> GetAccessTokenAsync(string accountId, CancellationToken ct = default);
}
