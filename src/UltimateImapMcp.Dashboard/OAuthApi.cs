using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.Dashboard;

public static class OAuthApi
{
    /// <summary>
    /// Temporary storage for completed OAuth flows awaiting account creation.
    /// Entries are removed after 10 minutes or after consumption.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PendingOAuthResult> PendingResults = new();

    public static IEndpointRouteBuilder MapOAuthApi(this IEndpointRouteBuilder app)
    {
        // GET /api/oauth/providers — returns available OAuth providers with status
        app.MapGet("/api/oauth/providers", (AppConfig config) =>
        {
            var providers = OAuthProviderDefaults.GetAvailableProviders(config);
            var result = providers.Select(p => new
            {
                provider = p.Key,
                configured = p.Value is not null,
                authUrl = p.Value?.AuthUrl,
                scopes = p.Value?.Scopes
            });
            return Results.Ok(result);
        });

        // GET /api/oauth/start — generates PKCE, stores state, returns auth_url
        app.MapGet("/api/oauth/start", (HttpContext ctx, AppConfig config, OAuthStateStore stateStore,
            ILogger<OAuthTokenService> logger) =>
        {
            var provider = ctx.Request.Query["provider"].FirstOrDefault();
            if (string.IsNullOrEmpty(provider))
                return Results.BadRequest(new { error = "provider query parameter is required" });

            logger.LogInformation("Starting OAuth flow for provider {Provider}", provider);

            // Allow optional client_id/secret overrides from query params
            var clientIdOverride = ctx.Request.Query["client_id"].FirstOrDefault();
            var clientSecretOverride = ctx.Request.Query["client_secret"].FirstOrDefault();

            var effectiveConfig = OAuthProviderDefaults.GetEffective(provider, config);

            // If not configured in defaults/config, try to use provided client_id
            if (effectiveConfig is null && !string.IsNullOrEmpty(clientIdOverride))
            {
                // Build a minimal config from query params and defaults
                var allProviders = OAuthProviderDefaults.GetAvailableProviders(config);
                effectiveConfig = new OAuthProviderConfig
                {
                    ClientId = clientIdOverride,
                    ClientSecret = clientSecretOverride
                };

                // Try to get auth/token URLs from the built-in defaults
                var builtIn = OAuthProviderDefaults.GetAvailableProviders(new AppConfig());
                if (builtIn.TryGetValue(provider, out var builtInConfig) && builtInConfig is not null)
                {
                    effectiveConfig.AuthUrl = builtInConfig.AuthUrl;
                    effectiveConfig.TokenUrl = builtInConfig.TokenUrl;
                    effectiveConfig.Scopes = builtInConfig.Scopes;
                }
            }

            if (effectiveConfig is null)
                return Results.BadRequest(new { error = $"Provider '{provider}' is not configured. Set client_id in config or provide it as a query parameter." });

            // Apply overrides
            if (!string.IsNullOrEmpty(clientIdOverride))
                effectiveConfig.ClientId = clientIdOverride;
            if (!string.IsNullOrEmpty(clientSecretOverride))
                effectiveConfig.ClientSecret = clientSecretOverride;

            // Generate PKCE
            var codeVerifier = OAuthTokenService.GenerateCodeVerifier();
            var codeChallenge = OAuthTokenService.GenerateCodeChallenge(codeVerifier);

            // Build redirect URI from the current request
            var dashboardPort = config.Server.DashboardPort;
            var redirectUri = $"http://localhost:{dashboardPort}/oauth/callback";

            // Store pending flow
            var state = stateStore.Create(new OAuthPendingFlow
            {
                Provider = provider,
                CodeVerifier = codeVerifier,
                ClientId = effectiveConfig.ClientId,
                ClientSecret = effectiveConfig.ClientSecret,
                RedirectUri = redirectUri
            });

            var authUrl = OAuthTokenService.BuildAuthUrl(
                provider, effectiveConfig, redirectUri, state, codeChallenge);

            return Results.Ok(new { auth_url = authUrl, state });
        });

        // GET /oauth/callback — exchanges code for tokens, redirects to SPA
        // Note: This must NOT be under /api/ so PinAuthMiddleware allows it through
        app.MapGet("/oauth/callback", async (HttpContext ctx, AppConfig config,
            OAuthStateStore stateStore, OAuthTokenService tokenService,
            CredentialEncryptor encryptor, IHttpClientFactory httpClientFactory,
            Microsoft.Extensions.Logging.ILogger<OAuthTokenService> logger) =>
        {
            var code = ctx.Request.Query["code"].FirstOrDefault();
            var state = ctx.Request.Query["state"].FirstOrDefault();
            var error = ctx.Request.Query["error"].FirstOrDefault();

            logger.LogInformation("OAuth callback: code={HasCode}, state={HasState}, error={Error}",
                !string.IsNullOrEmpty(code), !string.IsNullOrEmpty(state), error ?? "(none)");

            if (!string.IsNullOrEmpty(error))
            {
                var errorDesc = ctx.Request.Query["error_description"].FirstOrDefault() ?? error;
                logger.LogWarning("OAuth callback error from provider: {Error}", errorDesc);
                return Results.Redirect($"/accounts/oauth-complete?error={Uri.EscapeDataString(errorDesc)}");
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.BadRequest(new { error = "Missing code or state parameter" });

            var flow = stateStore.TryConsume(state);
            if (flow is null)
                return Results.Redirect("/accounts/oauth-complete?error=Invalid+or+expired+state");

            // Resolve effective config
            var effectiveConfig = OAuthProviderDefaults.GetEffective(flow.Provider, config);
            if (effectiveConfig is null)
            {
                effectiveConfig = new OAuthProviderConfig
                {
                    ClientId = flow.ClientId,
                    ClientSecret = flow.ClientSecret
                };

                // Get token URL from built-in defaults
                var builtIn = OAuthProviderDefaults.GetAvailableProviders(new AppConfig());
                if (builtIn.TryGetValue(flow.Provider, out var builtInConfig) && builtInConfig is not null)
                {
                    effectiveConfig.TokenUrl = builtInConfig.TokenUrl;
                    effectiveConfig.Scopes = builtInConfig.Scopes;
                }
            }

            try
            {
                var redirectUri = flow.RedirectUri
                    ?? $"http://localhost:{config.Server.DashboardPort}/oauth/callback";

                logger.LogInformation("Exchanging OAuth code for {Provider}, redirect_uri={RedirectUri}",
                    flow.Provider, redirectUri);

                var tokenResponse = await tokenService.ExchangeCodeAsync(
                    effectiveConfig, code, flow.CodeVerifier, redirectUri, ctx.RequestAborted)
                    .ConfigureAwait(false);

                logger.LogInformation("OAuth token exchange for {Provider}: access_token length={AccessLen}, refresh_token length={RefreshLen}, expires_in={ExpiresIn}, scope={Scope}",
                    flow.Provider,
                    tokenResponse.AccessToken.Length,
                    tokenResponse.RefreshToken?.Length ?? 0,
                    tokenResponse.ExpiresIn,
                    tokenResponse.Scope);

                // Extract user info: try ID token first, then profile endpoints
                using var httpClient = httpClientFactory.CreateClient();
                string? email = null;
                string? name = null;

                if (!string.IsNullOrEmpty(tokenResponse.IdToken))
                {
                    (email, name) = ExtractClaimsFromJwt(tokenResponse.IdToken, logger);
                    logger.LogInformation("From ID token for {Provider}: email={Email}, name={Name}",
                        flow.Provider, email ?? "(null)", name ?? "(null)");
                }

                // Try profile endpoint for missing info
                if ((string.IsNullOrEmpty(email) || string.IsNullOrEmpty(name))
                    && !string.IsNullOrEmpty(tokenResponse.AccessToken))
                {
                    var (profileEmail, profileName) = await FetchUserInfoAsync(
                        flow.Provider, tokenResponse.AccessToken, httpClient, logger, ctx.RequestAborted)
                        .ConfigureAwait(false);
                    email ??= profileEmail;
                    name ??= profileName;
                    logger.LogInformation("From profile for {Provider}: email={Email}, name={Name}",
                        flow.Provider, email ?? "(null)", name ?? "(null)");
                }

                // For Microsoft: IMAP token can't call Graph — use refresh token to get a Graph token
                if (string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(tokenResponse.RefreshToken)
                    && (flow.Provider.Equals("outlook", StringComparison.OrdinalIgnoreCase)
                        || flow.Provider.Equals("outlook365", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var graphConfig = new OAuthProviderConfig
                        {
                            ClientId = flow.ClientId,
                            ClientSecret = flow.ClientSecret,
                            TokenUrl = effectiveConfig?.TokenUrl
                        };
                        var graphToken = await tokenService.RefreshWithScopeAsync(
                            graphConfig, tokenResponse.RefreshToken,
                            "https://graph.microsoft.com/User.Read", ctx.RequestAborted)
                            .ConfigureAwait(false);

                        var (msEmail, msName) = await FetchUserInfoAsync(
                            flow.Provider, graphToken.AccessToken, httpClient, logger, ctx.RequestAborted)
                            .ConfigureAwait(false);
                        email ??= msEmail;
                        name ??= msName;
                        logger.LogInformation("From MS Graph for {Provider}: email={Email}, name={Name}",
                            flow.Provider, email ?? "(null)", name ?? "(null)");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to get MS Graph token for user info: {Message}", ex.Message);
                    }
                }

                // Store result temporarily
                var tempId = Guid.NewGuid().ToString("N");
                PendingResults[tempId] = new PendingOAuthResult
                {
                    Provider = flow.Provider,
                    ClientId = flow.ClientId,
                    ClientSecret = flow.ClientSecret,
                    AccessToken = tokenResponse.AccessToken,
                    RefreshToken = tokenResponse.RefreshToken,
                    ExpiresIn = tokenResponse.ExpiresIn,
                    Scope = tokenResponse.Scope,
                    Email = email,
                    Name = name,
                    CreatedAt = DateTime.UtcNow
                };

                // Clean up old entries
                PurgeExpiredResults();

                var redirectUrl = $"/accounts/oauth-complete?temp_id={tempId}";
                if (!string.IsNullOrEmpty(email))
                    redirectUrl += $"&email={Uri.EscapeDataString(email)}";
                if (!string.IsNullOrEmpty(name))
                    redirectUrl += $"&name={Uri.EscapeDataString(name)}";
                return Results.Redirect(redirectUrl);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "OAuth callback failed for provider {Provider}", flow?.Provider ?? "unknown");
                return Results.Redirect($"/accounts/oauth-complete?error={Uri.EscapeDataString("Authorization failed. Please try again.")}");
            }
        });

        // POST /api/oauth/complete — creates account + stores tokens
        app.MapPost("/api/oauth/complete", async (HttpContext ctx, AppConfig config,
            AccountRepository accountRepo, OAuthTokenRepository tokenRepo,
            CredentialEncryptor encryptor,
            ILogger<OAuthTokenService> logger) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<OAuthCompleteRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrEmpty(body.TempId))
                return Results.BadRequest(new { error = "temp_id is required" });

            if (!PendingResults.TryRemove(body.TempId, out var pending))
                return Results.BadRequest(new { error = "Invalid or expired temp_id" });

            if (DateTime.UtcNow - pending.CreatedAt > TimeSpan.FromMinutes(10))
                return Results.BadRequest(new { error = "Expired temp_id" });

            logger.LogInformation("Completing OAuth account creation for {Provider}, email={Email}",
                pending.Provider, pending.Email ?? "(unknown)");

            var accountName = !string.IsNullOrEmpty(body.Name) ? body.Name : pending.Email ?? "oauth-account";
            var email = !string.IsNullOrEmpty(body.Email) ? body.Email : pending.Email ?? "";

            // Determine IMAP/SMTP settings from provider
            var (imapHost, imapPort, smtpHost, smtpPort, smtpUseSsl) = pending.Provider.ToLowerInvariant() switch
            {
                "gmail" => ("imap.gmail.com", 993, "smtp.gmail.com", 465, true),
                "outlook" or "outlook365" => ("outlook.office365.com", 993, "smtp.office365.com", 587, false),
                "zoho" => ("", 0, "", 0, false), // Zoho OAuth uses REST API, not IMAP
                _ => ("", 993, "", 587, false)
            };

            // Create account
            var accountId = Guid.NewGuid().ToString();
            var credentialsEnc = encryptor.Encrypt("oauth2"); // Placeholder — actual auth via OAuth tokens

            accountRepo.Insert(accountId, accountName, imapHost, imapPort,
                smtpHost, smtpPort, smtpUseSsl, email,
                "oauth2", credentialsEnc, pending.Provider, null);

            // Store OAuth tokens
            var refreshTokenEnc = !string.IsNullOrEmpty(pending.RefreshToken)
                ? encryptor.Encrypt(pending.RefreshToken)
                : encryptor.Encrypt(""); // Should not happen for offline_access
            var accessTokenEnc = encryptor.Encrypt(pending.AccessToken);
            var tokenExpiry = pending.ExpiresIn.HasValue
                ? DateTime.UtcNow.AddSeconds(pending.ExpiresIn.Value)
                    .ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                : null;
            var clientSecretEnc = !string.IsNullOrEmpty(pending.ClientSecret)
                ? encryptor.Encrypt(pending.ClientSecret)
                : null;

            tokenRepo.Upsert(accountId, pending.Provider, pending.ClientId,
                clientSecretEnc, refreshTokenEnc, accessTokenEnc,
                tokenExpiry, pending.Scope, pending.Email);

            logger.LogInformation("OAuth account created: {AccountId} for {Provider} ({Email})",
                accountId, pending.Provider, email);
            return Results.Ok(new { account_id = accountId, email = pending.Email ?? email });
        });

        return app;
    }

    /// <summary>
    /// Extracts the email claim from a JWT ID token without full validation.
    /// This is safe here because we received the token directly from the provider.
    /// </summary>
    /// <summary>
    /// Extracts email and name claims from a JWT (ID token or MS access token).
    /// </summary>
    private static (string? Email, string? Name) ExtractClaimsFromJwt(string jwt, ILogger? logger = null)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return (null, null);

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            var padded = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload
            };

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var doc = System.Text.Json.JsonDocument.Parse(json);

            string? email = null;
            foreach (var field in new[] { "email", "preferred_username", "upn", "unique_name" })
            {
                if (doc.RootElement.TryGetProperty(field, out var prop) &&
                    prop.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    email = prop.GetString();
                    break;
                }
            }

            string? name = null;
            foreach (var field in new[] { "name", "given_name", "family_name" })
            {
                if (doc.RootElement.TryGetProperty(field, out var prop) &&
                    prop.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    name = prop.GetString();
                    break;
                }
            }

            return (email, name);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to extract claims from JWT");
            return (null, null);
        }
    }

    /// <summary>
    /// Fetches the user's email and display name from provider-specific sources.
    /// </summary>
    private static async Task<(string? Email, string? Name)> FetchUserInfoAsync(
        string provider, string accessToken, HttpClient http,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            // For Microsoft, the IMAP access token is opaque — try extracting from it anyway
            if (provider.Equals("outlook", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("outlook365", StringComparison.OrdinalIgnoreCase))
            {
                var msResult = ExtractClaimsFromJwt(accessToken, logger);
                if (!string.IsNullOrEmpty(msResult.Email))
                    return msResult;

                // Fall through to Graph API with a separate token request
                // (IMAP tokens can't call Graph, but we can try)
            }

            var url = provider.ToLowerInvariant() switch
            {
                "google" or "gmail" => "https://www.googleapis.com/oauth2/v2/userinfo",
                "zoho" => "https://accounts.zoho.com/oauth/user/info",
                "outlook" or "outlook365" => "https://graph.microsoft.com/v1.0/me",
                _ => null
            };

            if (url is null) return (null, null);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Zoho uses "Zoho-oauthtoken" prefix instead of "Bearer"
            if (provider.Equals("zoho", StringComparison.OrdinalIgnoreCase))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Zoho-oauthtoken", accessToken);
            else
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            logger.LogDebug("Profile response for {Provider}: {Json}", provider, json);

            var doc = System.Text.Json.JsonDocument.Parse(json);

            string? email = null;
            foreach (var field in new[] { "email", "Email", "mail", "userPrincipalName",
                                          "DISPLAY_NAME_EMAIL", "primary_email" })
            {
                if (doc.RootElement.TryGetProperty(field, out var prop) &&
                    prop.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    email = prop.GetString();
                    break;
                }
            }

            string? name = null;
            foreach (var field in new[] { "name", "Name", "displayName", "display_name",
                                          "given_name", "Display_Name", "DISPLAY_NAME" })
            {
                if (doc.RootElement.TryGetProperty(field, out var prop) &&
                    prop.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    name = prop.GetString();
                    break;
                }
            }

            return (email, name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch user profile for {Provider}", provider);
            return (null, null);
        }
    }

    private static void PurgeExpiredResults()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var (key, value) in PendingResults)
        {
            if (value.CreatedAt < cutoff)
                PendingResults.TryRemove(key, out _);
        }
    }
}

public record OAuthCompleteRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("temp_id")]
    public string TempId { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("email")]
    public string? Email { get; init; }
}

internal record PendingOAuthResult
{
    public required string Provider { get; init; }
    public required string ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public int? ExpiresIn { get; init; }
    public string? Scope { get; init; }
    public string? Email { get; init; }
    public string? Name { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
