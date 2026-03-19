using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.ImapClient.Repositories;

public record AccountRecord(
    string Id, string Name, string ImapHost, int ImapPort,
    string? SmtpHost, int SmtpPort, bool SmtpUseSsl,
    string Username, string AuthType, string CredentialsEnc,
    string Provider, string? ConfigJson,
    string CreatedAt, string UpdatedAt);

public class AccountRepository(AppDatabase db)
{
    public void Insert(string id, string name, string imapHost, int imapPort,
        string? smtpHost, int smtpPort, bool smtpUseSsl, string username,
        string authType, string credentialsEnc, string provider, string? configJson)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO accounts (id, name, imap_host, imap_port, smtp_host, smtp_port,
                    smtp_use_ssl, username, auth_type, credentials_enc, provider, config_json)
                VALUES ($id, $name, $imapHost, $imapPort, $smtpHost, $smtpPort,
                    $smtpUseSsl, $username, $authType, $credentialsEnc, $provider, $configJson);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$imapHost", imapHost);
            cmd.Parameters.AddWithValue("$imapPort", imapPort);
            cmd.Parameters.AddWithValue("$smtpHost", (object?)smtpHost ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$smtpPort", smtpPort);
            cmd.Parameters.AddWithValue("$smtpUseSsl", smtpUseSsl ? 1 : 0);
            cmd.Parameters.AddWithValue("$username", username);
            cmd.Parameters.AddWithValue("$authType", authType);
            cmd.Parameters.AddWithValue("$credentialsEnc", credentialsEnc);
            cmd.Parameters.AddWithValue("$provider", provider);
            cmd.Parameters.AddWithValue("$configJson", (object?)configJson ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    public void Update(string id, string? name, string? imapHost, int? imapPort,
        string? smtpHost, int? smtpPort, bool? smtpUseSsl, string? username)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            var setClauses = new List<string>();
            if (name is not null) { setClauses.Add("name = $name"); cmd.Parameters.AddWithValue("$name", name); }
            if (imapHost is not null) { setClauses.Add("imap_host = $imapHost"); cmd.Parameters.AddWithValue("$imapHost", imapHost); }
            if (imapPort is not null) { setClauses.Add("imap_port = $imapPort"); cmd.Parameters.AddWithValue("$imapPort", imapPort); }
            if (smtpHost is not null) { setClauses.Add("smtp_host = $smtpHost"); cmd.Parameters.AddWithValue("$smtpHost", smtpHost); }
            if (smtpPort is not null) { setClauses.Add("smtp_port = $smtpPort"); cmd.Parameters.AddWithValue("$smtpPort", smtpPort); }
            if (smtpUseSsl is not null) { setClauses.Add("smtp_use_ssl = $smtpUseSsl"); cmd.Parameters.AddWithValue("$smtpUseSsl", smtpUseSsl.Value ? 1 : 0); }
            if (username is not null) { setClauses.Add("username = $username"); cmd.Parameters.AddWithValue("$username", username); }
            if (setClauses.Count == 0) return;
            setClauses.Add("updated_at = datetime('now')");
            cmd.CommandText = $"UPDATE accounts SET {string.Join(", ", setClauses)} WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        });
    }

    public void Delete(string id)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM accounts WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        });
    }

    public AccountRecord? GetById(string id)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM accounts WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public List<AccountRecord> GetAll()
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM accounts ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        var list = new List<AccountRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    private static AccountRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Id: r.GetString(r.GetOrdinal("id")),
        Name: r.GetString(r.GetOrdinal("name")),
        ImapHost: r.GetString(r.GetOrdinal("imap_host")),
        ImapPort: r.GetInt32(r.GetOrdinal("imap_port")),
        SmtpHost: r.IsDBNull(r.GetOrdinal("smtp_host")) ? null : r.GetString(r.GetOrdinal("smtp_host")),
        SmtpPort: r.GetInt32(r.GetOrdinal("smtp_port")),
        SmtpUseSsl: r.GetInt32(r.GetOrdinal("smtp_use_ssl")) == 1,
        Username: r.GetString(r.GetOrdinal("username")),
        AuthType: r.GetString(r.GetOrdinal("auth_type")),
        CredentialsEnc: r.GetString(r.GetOrdinal("credentials_enc")),
        Provider: r.GetString(r.GetOrdinal("provider")),
        ConfigJson: r.IsDBNull(r.GetOrdinal("config_json")) ? null : r.GetString(r.GetOrdinal("config_json")),
        CreatedAt: r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt: r.GetString(r.GetOrdinal("updated_at"))
    );
}
