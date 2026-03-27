using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.Core.OAuth;

/// <summary>
/// Core OAuth2 PKCE logic: code challenge generation, authorization URL building,
/// token exchange, and token refresh.
/// </summary>
public class OAuthTokenService(IHttpClientFactory httpClientFactory)
{
    /// <summary>Generates a random 43-character URL-safe code verifier for PKCE.</summary>
    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    /// <summary>Computes the S256 code challenge from a code verifier.</summary>
    public static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// Builds the full authorization URL with PKCE parameters.
    /// </summary>
    public static string BuildAuthUrl(string provider, OAuthProviderConfig effectiveConfig,
        string redirectUri, string state, string codeChallenge)
    {
        var authUrl = effectiveConfig.AuthUrl
            ?? throw new InvalidOperationException($"No auth_url configured for provider '{provider}'.");

        var scopes = effectiveConfig.Scopes is { Count: > 0 }
            ? string.Join(" ", effectiveConfig.Scopes)
            : "";

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = effectiveConfig.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scopes,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["access_type"] = "offline",
            ["prompt"] = "consent"
        };

        var query = string.Join("&", queryParams
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return $"{authUrl}?{query}";
    }

    /// <summary>
    /// Exchanges an authorization code for tokens using the PKCE verifier.
    /// </summary>
    public async Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthProviderConfig effectiveConfig,
        string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        var tokenUrl = effectiveConfig.TokenUrl
            ?? throw new InvalidOperationException("No token_url configured.");

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = effectiveConfig.ClientId,
            ["code_verifier"] = codeVerifier
        };

        if (!string.IsNullOrEmpty(effectiveConfig.ClientSecret))
            formData["client_secret"] = effectiveConfig.ClientSecret;

        var client = httpClientFactory.CreateClient("OAuth");
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(formData)
        };

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Token exchange failed ({response.StatusCode}): {json}");
        }

        return JsonSerializer.Deserialize<OAuthTokenResponse>(json)
            ?? throw new InvalidOperationException("Failed to deserialize token response.");
    }

    /// <summary>
    /// Refreshes an access token using a refresh token.
    /// </summary>
    public async Task<OAuthTokenResponse> RefreshTokenAsync(OAuthProviderConfig effectiveConfig,
        string refreshToken, CancellationToken ct = default)
    {
        var tokenUrl = effectiveConfig.TokenUrl
            ?? throw new InvalidOperationException("No token_url configured.");

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = effectiveConfig.ClientId
        };

        if (!string.IsNullOrEmpty(effectiveConfig.ClientSecret))
            formData["client_secret"] = effectiveConfig.ClientSecret;

        var client = httpClientFactory.CreateClient("OAuth");
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(formData)
        };

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Token refresh failed ({response.StatusCode}): {json}");
        }

        return JsonSerializer.Deserialize<OAuthTokenResponse>(json)
            ?? throw new InvalidOperationException("Failed to deserialize token response.");
    }

    /// <summary>
    /// Refreshes a token with a different scope (e.g., to get a Graph API token
    /// from a refresh token originally issued for IMAP scopes).
    /// </summary>
    public async Task<OAuthTokenResponse> RefreshWithScopeAsync(OAuthProviderConfig effectiveConfig,
        string refreshToken, string scope, CancellationToken ct = default)
    {
        var tokenUrl = effectiveConfig.TokenUrl
            ?? throw new InvalidOperationException("No token_url configured.");

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = effectiveConfig.ClientId,
            ["scope"] = scope
        };

        if (!string.IsNullOrEmpty(effectiveConfig.ClientSecret))
            formData["client_secret"] = effectiveConfig.ClientSecret;

        var client = httpClientFactory.CreateClient("OAuth");
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(formData)
        };

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Scoped token refresh failed ({response.StatusCode}): {json}");
        }

        return JsonSerializer.Deserialize<OAuthTokenResponse>(json)
            ?? throw new InvalidOperationException("Failed to deserialize token response.");
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

/// <summary>
/// Represents the response from an OAuth2 token endpoint.
/// </summary>
public record OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    /// <summary>Zoho-specific: the API domain for this user's datacenter (e.g., https://www.zohoapis.com.au)</summary>
    [JsonPropertyName("api_domain")]
    public string? ApiDomain { get; init; }
}
