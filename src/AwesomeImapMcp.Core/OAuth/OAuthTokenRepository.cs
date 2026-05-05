using AwesomeImapMcp.Core.Configuration;

namespace AwesomeImapMcp.Core.OAuth;

/// <summary>
/// Persists OAuth token records in the accounts.json file.
/// Shares the same <see cref="AccountsStore"/> as AccountRepository.
/// </summary>
public class OAuthTokenRepository(AccountsStore store)
{
    public void Upsert(string accountId, string provider, string clientId,
        string? clientSecretEnc, string refreshTokenEnc, string? accessTokenEnc,
        string? tokenExpiry, string? scopes, string? email,
        string? apiDomain = null)
    {
        store.Write(data =>
        {
            var existing = data.OAuthTokens.Find(t => t.AccountId == accountId);
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

            if (existing is not null)
            {
                existing.Provider = provider;
                existing.ClientId = clientId;
                existing.ClientSecretEnc = clientSecretEnc;
                existing.RefreshTokenEnc = refreshTokenEnc;
                existing.AccessTokenEnc = accessTokenEnc;
                existing.TokenExpiry = tokenExpiry;
                existing.Scopes = scopes;
                existing.Email = email;
                existing.ApiDomain = apiDomain;
                existing.UpdatedAt = now;
            }
            else
            {
                data.OAuthTokens.Add(new OAuthTokenEntry
                {
                    AccountId = accountId,
                    Provider = provider,
                    ClientId = clientId,
                    ClientSecretEnc = clientSecretEnc,
                    RefreshTokenEnc = refreshTokenEnc,
                    AccessTokenEnc = accessTokenEnc,
                    TokenExpiry = tokenExpiry,
                    Scopes = scopes,
                    Email = email,
                    ApiDomain = apiDomain,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        });
    }

    public OAuthTokenRecord? GetByAccountId(string accountId)
    {
        var data = store.Read();
        var entry = data.OAuthTokens.Find(t => t.AccountId == accountId);
        return entry is null ? null : ToRecord(entry);
    }

    public List<OAuthTokenRecord> GetAll()
    {
        var data = store.Read();
        return data.OAuthTokens.Select(ToRecord).ToList();
    }

    public void UpdateAccessToken(string accountId, string? accessTokenEnc, string? tokenExpiry,
        string? apiDomain = null)
    {
        store.Write(data =>
        {
            var entry = data.OAuthTokens.Find(t => t.AccountId == accountId);
            if (entry is null) return;

            entry.AccessTokenEnc = accessTokenEnc;
            entry.TokenExpiry = tokenExpiry;
            if (apiDomain is not null)
                entry.ApiDomain = apiDomain;
            entry.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        });
    }

    public void Delete(string accountId)
    {
        store.Write(data =>
        {
            data.OAuthTokens.RemoveAll(t => t.AccountId == accountId);
        });
    }

    /// <summary>Removes oauth_tokens entries that have no matching account.</summary>
    public void CleanOrphans()
    {
        store.Write(data =>
        {
            var accountIds = new HashSet<string>(data.Accounts.Select(a => a.Id));
            data.OAuthTokens.RemoveAll(t => !accountIds.Contains(t.AccountId));
        });
    }

    private static OAuthTokenRecord ToRecord(OAuthTokenEntry e) => new(
        AccountId: e.AccountId,
        Provider: e.Provider,
        ClientId: e.ClientId,
        ClientSecretEnc: e.ClientSecretEnc,
        RefreshTokenEnc: e.RefreshTokenEnc,
        AccessTokenEnc: e.AccessTokenEnc,
        TokenExpiry: e.TokenExpiry,
        Scopes: e.Scopes,
        Email: e.Email,
        CreatedAt: e.CreatedAt,
        UpdatedAt: e.UpdatedAt,
        ApiDomain: e.ApiDomain
    );
}

public record OAuthTokenRecord(
    string AccountId, string Provider, string ClientId,
    string? ClientSecretEnc, string RefreshTokenEnc,
    string? AccessTokenEnc, string? TokenExpiry,
    string? Scopes, string? Email,
    string CreatedAt, string UpdatedAt,
    string? ApiDomain);
