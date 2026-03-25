using System.Text;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.Core.OAuth;

/// <summary>
/// Static registry of built-in OAuth provider constants.
/// User config is merged on top of defaults — user values win when present.
/// Client secrets are stored obfuscated (XOR + base64) to avoid plain-text
/// leaks in source control. This is NOT security — it's anti-scraping.
/// Desktop OAuth app secrets are public by design (same as Thunderbird, K-9, etc.).
/// </summary>
public static class OAuthProviderDefaults
{
    private static readonly Dictionary<string, OAuthProviderConfig> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail"] = new OAuthProviderConfig
        {
            ClientId = "1025431924883-5hldl1h3h22vpe08tqh72f9v60vajjku.apps.googleusercontent.com",
            ClientSecret = Deobfuscate("MiYuMiB1Sl4SXwkDWzpWAWkmNSczHgADXRYoQSIiGzYIRAc=", "uimap-gmail"),
            AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth",
            TokenUrl = "https://oauth2.googleapis.com/token",
            Scopes = ["https://mail.google.com/", "openid", "email", "profile"]
        },
        // Outlook (personal + work/school): /common/ endpoint lets user choose at login.
        // IMAP.AccessAsUser.All must be registered under Microsoft Graph in Azure Portal.
        // The outlook.office.com scope prefix maps to the same Graph permission.
        ["outlook"] = new OAuthProviderConfig
        {
            ClientId = "21f4ca91-56d1-49ba-9e86-dd85eefbdc6c",
            AuthUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
            TokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            Scopes = ["https://outlook.office.com/IMAP.AccessAsUser.All",
                       "https://outlook.office.com/SMTP.Send",
                       "offline_access"]
        },

        // Zoho Mail: OAuth is for REST API only (no XOAUTH2 for IMAP).
        // When a Zoho account uses OAuth, it routes through the REST backend
        // instead of IMAP. IMAP+app-password is still supported for Zoho.
        // Users must create a "Self Client" at https://api-console.zoho.com/
        // and supply their own client_id/client_secret in config.
        ["zoho"] = new OAuthProviderConfig
        {
            ClientId = "CONFIGURE_ME",
            AuthUrl = "https://accounts.zoho.com/oauth/v2/auth",
            TokenUrl = "https://accounts.zoho.com/oauth/v2/token",
            Scopes = [
                "ZohoMail.messages.READ",
                "ZohoMail.messages.CREATE",
                "ZohoMail.folders.READ",
                "ZohoMail.accounts.READ",
                "ZohoMail.messages.UPDATE",
                "ZohoMail.messages.DELETE",
            ]
        },
    };

    /// <summary>XOR-deobfuscates a base64 string with a repeating key.</summary>
    private static string Deobfuscate(string encoded, string key)
    {
        var data = Convert.FromBase64String(encoded);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        for (var i = 0; i < data.Length; i++)
            data[i] ^= keyBytes[i % keyBytes.Length];
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>
    /// Merges user config on top of built-in defaults for the given provider.
    /// Returns null if no usable client_id is configured (empty or "CONFIGURE_ME").
    /// </summary>
    public static OAuthProviderConfig? GetEffective(string provider, AppConfig config)
    {
        // Start with built-in defaults if they exist
        Defaults.TryGetValue(provider, out var defaults);

        // Check for user overrides
        config.OAuthProviders.TryGetValue(provider, out var userConfig);

        if (defaults is null && userConfig is null)
            return null;

        var effective = new OAuthProviderConfig
        {
            ClientId = !string.IsNullOrEmpty(userConfig?.ClientId)
                ? userConfig.ClientId
                : defaults?.ClientId ?? string.Empty,

            ClientSecret = userConfig?.ClientSecret ?? defaults?.ClientSecret,

            AuthUrl = !string.IsNullOrEmpty(userConfig?.AuthUrl)
                ? userConfig.AuthUrl
                : defaults?.AuthUrl,

            TokenUrl = !string.IsNullOrEmpty(userConfig?.TokenUrl)
                ? userConfig.TokenUrl
                : defaults?.TokenUrl,

            Scopes = userConfig?.Scopes is { Count: > 0 }
                ? userConfig.Scopes
                : defaults?.Scopes
        };

        // Not usable if client_id is missing or placeholder
        if (string.IsNullOrEmpty(effective.ClientId) ||
            effective.ClientId.Equals("CONFIGURE_ME", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return effective;
    }

    /// <summary>
    /// Returns all known providers with their effective config (null if not usable).
    /// </summary>
    public static Dictionary<string, OAuthProviderConfig?> GetAvailableProviders(AppConfig config)
    {
        var result = new Dictionary<string, OAuthProviderConfig?>(StringComparer.OrdinalIgnoreCase);

        // Include all built-in providers
        foreach (var provider in Defaults.Keys)
        {
            result[provider] = GetEffective(provider, config);
        }

        // Include any user-configured providers not in defaults
        foreach (var provider in config.OAuthProviders.Keys)
        {
            if (!result.ContainsKey(provider))
            {
                result[provider] = GetEffective(provider, config);
            }
        }

        return result;
    }
}
