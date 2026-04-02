using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.ImapClient.Repositories;

public record AccountRecord(
    string Id, string Name, string ImapHost, int ImapPort,
    string? SmtpHost, int SmtpPort, bool SmtpUseSsl,
    string Username, string AuthType, string CredentialsEnc,
    string Provider, string? ConfigJson,
    string CreatedAt, string UpdatedAt,
    string BackendType = "imap",
    bool Enabled = true);

public class AccountRepository(AccountsStore store)
{
    public void Insert(string id, string name, string imapHost, int imapPort,
        string? smtpHost, int smtpPort, bool smtpUseSsl, string username,
        string authType, string credentialsEnc, string provider, string? configJson)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        store.Write(data =>
        {
            data.Accounts.Add(new AccountEntry
            {
                Id = id,
                Name = name,
                ImapHost = imapHost,
                ImapPort = imapPort,
                SmtpHost = smtpHost,
                SmtpPort = smtpPort,
                SmtpUseSsl = smtpUseSsl,
                Username = username,
                AuthType = authType,
                CredentialsEnc = credentialsEnc,
                Provider = provider,
                ConfigJson = configJson,
                CreatedAt = now,
                UpdatedAt = now,
            });
        });
    }

    public void Update(string id, string? name, string? imapHost, int? imapPort,
        string? smtpHost, int? smtpPort, bool? smtpUseSsl, string? username, string? configJson)
    {
        store.Write(data =>
        {
            var entry = data.Accounts.Find(a => a.Id == id);
            if (entry is null) return;

            if (name is not null) entry.Name = name;
            if (imapHost is not null) entry.ImapHost = imapHost;
            if (imapPort is not null) entry.ImapPort = imapPort.Value;
            if (smtpHost is not null) entry.SmtpHost = smtpHost;
            if (smtpPort is not null) entry.SmtpPort = smtpPort.Value;
            if (smtpUseSsl is not null) entry.SmtpUseSsl = smtpUseSsl.Value;
            if (username is not null) entry.Username = username;
            if (configJson is not null) entry.ConfigJson = configJson;
            entry.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        });
    }

    public void Delete(string id)
    {
        store.Write(data =>
        {
            data.Accounts.RemoveAll(a => a.Id == id);
        });
    }

    public AccountRecord? GetById(string id)
    {
        var data = store.Read();
        var entry = data.Accounts.Find(a => a.Id == id);
        return entry is null ? null : ToRecord(entry);
    }

    public AccountRecord? GetByName(string name)
    {
        var data = store.Read();
        var entry = data.Accounts.Find(a =>
            string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : ToRecord(entry);
    }

    /// <summary>
    /// Resolves an account by ID first, then falls back to name-based lookup for
    /// backwards compatibility with config-derived account names.
    /// </summary>
    public AccountRecord? ResolveAccount(string idOrName)
    {
        return GetById(idOrName) ?? GetByName(idOrName);
    }

    public List<AccountRecord> GetAll()
    {
        var data = store.Read();
        return data.Accounts
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToRecord)
            .ToList();
    }

    /// <summary>
    /// Resolves an account and throws if it is disabled.
    /// Returns the account record when the account exists and is enabled.
    /// </summary>
    public AccountRecord? ResolveEnabledAccount(string idOrName)
    {
        var account = ResolveAccount(idOrName);
        if (account is not null && !account.Enabled)
            throw new InvalidOperationException($"Account '{account.Name}' is disabled. Re-enable it from the dashboard or config to use it.");
        return account;
    }

    public void SetEnabled(string id, bool enabled)
    {
        store.Write(data =>
        {
            var entry = data.Accounts.Find(a => a.Id == id);
            if (entry is null) return;
            entry.Enabled = enabled;
            entry.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        });
    }

    private static AccountRecord ToRecord(AccountEntry e) => new(
        Id: e.Id,
        Name: e.Name,
        ImapHost: e.ImapHost,
        ImapPort: e.ImapPort,
        SmtpHost: e.SmtpHost,
        SmtpPort: e.SmtpPort,
        SmtpUseSsl: e.SmtpUseSsl,
        Username: e.Username,
        AuthType: e.AuthType,
        CredentialsEnc: e.CredentialsEnc,
        Provider: e.Provider,
        ConfigJson: e.ConfigJson,
        CreatedAt: e.CreatedAt,
        UpdatedAt: e.UpdatedAt,
        BackendType: e.BackendType,
        Enabled: e.Enabled
    );
}
