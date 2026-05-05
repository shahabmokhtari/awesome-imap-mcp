using System.Security.Cryptography;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Database;

namespace AwesomeImapMcp.Dashboard;

public record DashboardSessionRecord(string Token, string CreatedAt, string ExpiresAt);

public class DashboardAuthRepository(AppDatabase db, AppConfig config)
{
    // ---- PIN (stored in config file, not DB) ----

    public string? GetPinHash() => config.Server.DashboardPinHash;

    public bool HasPinSet() => !string.IsNullOrEmpty(config.Server.DashboardPinHash);

    public void UpsertPin(string bcryptHash)
    {
        config.Server.DashboardPinHash = bcryptHash;
        config.Server.DashboardAuth = "pin";

        // Persist to config file
        if (config.SourcePath is not null)
            ConfigLoader.SaveToFile(config, config.SourcePath);
    }

    // ---- Sessions (ephemeral, stored in DB — lost on cache clear, which is fine) ----

    public string CreateSession(TimeSpan expiry)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTime.UtcNow.Add(expiry).ToString("O");

        db.ExecuteWrite(conn =>
        {
            // Ensure table exists (may be missing after DB reset)
            using var createCmd = conn.CreateCommand();
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS dashboard_sessions (
                    token TEXT PRIMARY KEY,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    expires_at TEXT NOT NULL
                );
                """;
            createCmd.ExecuteNonQuery();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dashboard_sessions (token, expires_at)
                VALUES ($token, $expiresAt);
                """;
            cmd.Parameters.AddWithValue("$token", token);
            cmd.Parameters.AddWithValue("$expiresAt", expiresAt);
            cmd.ExecuteNonQuery();
        });

        return token;
    }

    public bool ValidateSession(string token)
    {
        try
        {
            using var conn = db.GetReadConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT token FROM dashboard_sessions
                WHERE token = $token AND expires_at > datetime('now');
                """;
            cmd.Parameters.AddWithValue("$token", token);
            return cmd.ExecuteScalar() is not null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Table may not exist after DB reset — treat as invalid session
            return false;
        }
    }

    public void CleanExpiredSessions()
    {
        try
        {
            db.ExecuteWrite(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM dashboard_sessions WHERE expires_at <= datetime('now');";
                cmd.ExecuteNonQuery();
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* table may not exist */ }
    }

    public void ClearAllSessions()
    {
        try
        {
            db.ExecuteWrite(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM dashboard_sessions;";
                cmd.ExecuteNonQuery();
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* table may not exist */ }
    }

    public void DeleteSession(string token)
    {
        try
        {
            db.ExecuteWrite(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM dashboard_sessions WHERE token = $token;";
                cmd.Parameters.AddWithValue("$token", token);
                cmd.ExecuteNonQuery();
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* table may not exist */ }
    }
}
