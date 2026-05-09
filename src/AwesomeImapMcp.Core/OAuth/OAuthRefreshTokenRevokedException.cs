namespace AwesomeImapMcp.Core.OAuth;

/// <summary>
/// Thrown when an OAuth token refresh is permanently rejected by the provider
/// with a non-retryable client error such as "invalid_grant" (revoked or expired
/// refresh token). Unlike transient network failures, this condition will not
/// resolve on retry — the user must re-authenticate to obtain a new refresh token.
/// </summary>
public sealed class OAuthRefreshTokenRevokedException : Exception
{
    /// <summary>The OAuth error code returned by the provider (e.g. "invalid_grant").</summary>
    public string OAuthError { get; }

    public OAuthRefreshTokenRevokedException(string oauthError, string responseBody)
        : base($"OAuth token refresh permanently failed with error '{oauthError}'. Response: {responseBody}")
    {
        OAuthError = oauthError;
    }
}
