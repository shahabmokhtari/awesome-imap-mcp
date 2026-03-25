using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.Dashboard;

public record DashboardAuthRecord(int Id, string AuthType, string? Username, string Hash,
    string CreatedAt, string UpdatedAt);

public record DashboardSessionRecord(string Token, string CreatedAt, string ExpiresAt);

public class DashboardAuthRepository(AppDatabase db)
{
    // ---- Auth ----

    public DashboardAuthRecord? GetPinAuth()
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM dashboard_auth WHERE auth_type = 'pin' LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAuth(reader) : null;
    }

    public void UpsertPin(string bcryptHash)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM dashboard_auth WHERE auth_type = 'pin';
                INSERT INTO dashboard_auth (auth_type, hash) VALUES ('pin', $hash);
                """;
            cmd.Parameters.AddWithValue("$hash", bcryptHash);
            cmd.ExecuteNonQuery();
        });
    }

    public bool HasPinSet()
    {
        return GetPinAuth() is not null;
    }

    // ---- Sessions ----

    public string CreateSession(TimeSpan expiry)
    {
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            + Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var expiresAt = DateTime.UtcNow.Add(expiry).ToString("O");

        db.ExecuteWrite(conn =>
        {
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
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT token FROM dashboard_sessions
            WHERE token = $token AND expires_at > datetime('now');
            """;
        cmd.Parameters.AddWithValue("$token", token);
        return cmd.ExecuteScalar() is not null;
    }

    public void CleanExpiredSessions()
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM dashboard_sessions WHERE expires_at <= datetime('now');";
            cmd.ExecuteNonQuery();
        });
    }

    public void ClearAllSessions()
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM dashboard_sessions;";
            cmd.ExecuteNonQuery();
        });
    }

    public void DeleteSession(string token)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM dashboard_sessions WHERE token = $token;";
            cmd.Parameters.AddWithValue("$token", token);
            cmd.ExecuteNonQuery();
        });
    }

    private static DashboardAuthRecord ReadAuth(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Id: r.GetInt32(r.GetOrdinal("id")),
        AuthType: r.GetString(r.GetOrdinal("auth_type")),
        Username: r.IsDBNull(r.GetOrdinal("username")) ? null : r.GetString(r.GetOrdinal("username")),
        Hash: r.GetString(r.GetOrdinal("hash")),
        CreatedAt: r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt: r.GetString(r.GetOrdinal("updated_at"))
    );
}
