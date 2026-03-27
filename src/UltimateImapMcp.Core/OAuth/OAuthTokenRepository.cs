using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.Core.OAuth;

/// <summary>
/// Persists OAuth token records in the oauth_tokens SQLite table.
/// Follows the same raw ADO.NET pattern as AccountRepository.
/// </summary>
public class OAuthTokenRepository(AppDatabase db)
{
    public void Upsert(string accountId, string provider, string clientId,
        string? clientSecretEnc, string refreshTokenEnc, string? accessTokenEnc,
        string? tokenExpiry, string? scopes, string? email,
        string? apiDomain = null)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO oauth_tokens (account_id, provider, client_id, client_secret_enc,
                    refresh_token_enc, access_token_enc, token_expiry, scopes, email, api_domain)
                VALUES ($accountId, $provider, $clientId, $clientSecretEnc,
                    $refreshTokenEnc, $accessTokenEnc, $tokenExpiry, $scopes, $email, $apiDomain)
                ON CONFLICT(account_id) DO UPDATE SET
                    provider = excluded.provider,
                    client_id = excluded.client_id,
                    client_secret_enc = excluded.client_secret_enc,
                    refresh_token_enc = excluded.refresh_token_enc,
                    access_token_enc = excluded.access_token_enc,
                    token_expiry = excluded.token_expiry,
                    scopes = excluded.scopes,
                    email = excluded.email,
                    api_domain = excluded.api_domain,
                    updated_at = datetime('now');
                """;
            cmd.Parameters.AddWithValue("$accountId", accountId);
            cmd.Parameters.AddWithValue("$provider", provider);
            cmd.Parameters.AddWithValue("$clientId", clientId);
            cmd.Parameters.AddWithValue("$clientSecretEnc", (object?)clientSecretEnc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$refreshTokenEnc", refreshTokenEnc);
            cmd.Parameters.AddWithValue("$accessTokenEnc", (object?)accessTokenEnc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tokenExpiry", (object?)tokenExpiry ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$scopes", (object?)scopes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$email", (object?)email ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$apiDomain", (object?)apiDomain ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    public OAuthTokenRecord? GetByAccountId(string accountId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM oauth_tokens WHERE account_id = $accountId;";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public List<OAuthTokenRecord> GetAll()
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM oauth_tokens;";
        using var reader = cmd.ExecuteReader();
        var list = new List<OAuthTokenRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    public void UpdateAccessToken(string accountId, string? accessTokenEnc, string? tokenExpiry,
        string? apiDomain = null)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            if (apiDomain is not null)
            {
                cmd.CommandText = """
                    UPDATE oauth_tokens
                    SET access_token_enc = $accessTokenEnc,
                        token_expiry = $tokenExpiry,
                        api_domain = $apiDomain,
                        updated_at = datetime('now')
                    WHERE account_id = $accountId;
                    """;
                cmd.Parameters.AddWithValue("$apiDomain", apiDomain);
            }
            else
            {
                cmd.CommandText = """
                    UPDATE oauth_tokens
                    SET access_token_enc = $accessTokenEnc,
                        token_expiry = $tokenExpiry,
                        updated_at = datetime('now')
                    WHERE account_id = $accountId;
                    """;
            }
            cmd.Parameters.AddWithValue("$accountId", accountId);
            cmd.Parameters.AddWithValue("$accessTokenEnc", (object?)accessTokenEnc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tokenExpiry", (object?)tokenExpiry ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    public void Delete(string accountId)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM oauth_tokens WHERE account_id = $accountId;";
            cmd.Parameters.AddWithValue("$accountId", accountId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Removes oauth_tokens rows that have no matching account.</summary>
    public void CleanOrphans()
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM oauth_tokens WHERE account_id NOT IN (SELECT id FROM accounts);";
            cmd.ExecuteNonQuery();
        });
    }

    private static OAuthTokenRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        AccountId: r.GetString(r.GetOrdinal("account_id")),
        Provider: r.GetString(r.GetOrdinal("provider")),
        ClientId: r.GetString(r.GetOrdinal("client_id")),
        ClientSecretEnc: r.IsDBNull(r.GetOrdinal("client_secret_enc")) ? null : r.GetString(r.GetOrdinal("client_secret_enc")),
        RefreshTokenEnc: r.GetString(r.GetOrdinal("refresh_token_enc")),
        AccessTokenEnc: r.IsDBNull(r.GetOrdinal("access_token_enc")) ? null : r.GetString(r.GetOrdinal("access_token_enc")),
        TokenExpiry: r.IsDBNull(r.GetOrdinal("token_expiry")) ? null : r.GetString(r.GetOrdinal("token_expiry")),
        Scopes: r.IsDBNull(r.GetOrdinal("scopes")) ? null : r.GetString(r.GetOrdinal("scopes")),
        Email: r.IsDBNull(r.GetOrdinal("email")) ? null : r.GetString(r.GetOrdinal("email")),
        CreatedAt: r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt: r.GetString(r.GetOrdinal("updated_at")),
        ApiDomain: r.IsDBNull(r.GetOrdinal("api_domain")) ? null : r.GetString(r.GetOrdinal("api_domain"))
    );
}

public record OAuthTokenRecord(
    string AccountId, string Provider, string ClientId,
    string? ClientSecretEnc, string RefreshTokenEnc,
    string? AccessTokenEnc, string? TokenExpiry,
    string? Scopes, string? Email,
    string CreatedAt, string UpdatedAt,
    string? ApiDomain);
