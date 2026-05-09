using System.Text.Json;
using System.Text.Json.Serialization;

namespace AwesomeImapMcp.Core.Configuration;

/// <summary>
/// JSON file entries that mirror the fields of AccountRecord/OAuthTokenRecord
/// but use <see cref="JsonPropertyName"/> for snake_case serialization.
/// </summary>
public class AccountEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("imap_host")]
    public string ImapHost { get; set; } = string.Empty;

    [JsonPropertyName("imap_port")]
    public int ImapPort { get; set; } = 993;

    [JsonPropertyName("smtp_host")]
    public string? SmtpHost { get; set; }

    [JsonPropertyName("smtp_port")]
    public int SmtpPort { get; set; } = 587;

    [JsonPropertyName("smtp_use_ssl")]
    public bool SmtpUseSsl { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("auth_type")]
    public string AuthType { get; set; } = "app_password";

    [JsonPropertyName("credentials_enc")]
    public string CredentialsEnc { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "generic";

    [JsonPropertyName("config_json")]
    public string? ConfigJson { get; set; }

    [JsonPropertyName("backend_type")]
    public string BackendType { get; set; } = "imap";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// True when the OAuth refresh token was revoked or expired permanently (e.g. provider
    /// returned invalid_grant). Sync stops for this account until the user re-authenticates.
    /// Cleared on successful OAuth re-authorization.
    /// </summary>
    [JsonPropertyName("requires_reauth")]
    public bool RequiresReauth { get; set; } = false;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

public class OAuthTokenEntry
{
    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_secret_enc")]
    public string? ClientSecretEnc { get; set; }

    [JsonPropertyName("refresh_token_enc")]
    public string RefreshTokenEnc { get; set; } = string.Empty;

    [JsonPropertyName("access_token_enc")]
    public string? AccessTokenEnc { get; set; }

    [JsonPropertyName("token_expiry")]
    public string? TokenExpiry { get; set; }

    [JsonPropertyName("scopes")]
    public string? Scopes { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("api_domain")]
    public string? ApiDomain { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>
/// Root JSON model for accounts.json, containing both accounts and OAuth tokens.
/// </summary>
public class AccountsData
{
    [JsonPropertyName("accounts")]
    public List<AccountEntry> Accounts { get; set; } = [];

    [JsonPropertyName("oauth_tokens")]
    public List<OAuthTokenEntry> OAuthTokens { get; set; } = [];
}

/// <summary>
/// Thread-safe in-memory store backed by accounts.json.
/// Shared by <c>AccountRepository</c> and <c>OAuthTokenRepository</c>.
/// </summary>
public class AccountsStore : IDisposable
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private AccountsData _data;
    private readonly FileSystemWatcher? _watcher;
    private DateTime _lastWriteByUs;

    public AccountsStore(string filePath)
    {
        _filePath = filePath;
        _data = Load(filePath);

        // Watch for external modifications to accounts.json so the in-memory
        // store stays in sync when another process (or manual edit) changes it.
        var dir = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileName(filePath);
        if (dir is not null && Directory.Exists(dir))
        {
            _watcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            // Skip reloads triggered by our own Save() calls (within 2 seconds)
            if ((DateTime.UtcNow - _lastWriteByUs).TotalSeconds < 2)
                return;

            try
            {
                // Small delay to let the writer finish flushing
                Thread.Sleep(100);
                _data = Load(_filePath);
            }
            catch
            {
                // File might be mid-write; ignore and try next time
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of the current data. Callers should not mutate the returned object;
    /// use <see cref="Write"/> for mutations.
    /// </summary>
    public AccountsData Read()
    {
        lock (_lock)
        {
            return _data;
        }
    }

    /// <summary>
    /// Applies a mutation to the data under a lock and persists to disk.
    /// </summary>
    public void Write(Action<AccountsData> mutator)
    {
        lock (_lock)
        {
            mutator(_data);
            _lastWriteByUs = DateTime.UtcNow;
            Save();
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }

    /// <summary>
    /// Derives the accounts.json file path from the main config.json path.
    /// </summary>
    public static string ResolveAccountsPath(string? configPath)
    {
        if (configPath is not null)
        {
            var dir = Path.GetDirectoryName(configPath);
            if (dir is not null)
                return Path.Combine(dir, "accounts.json");
        }

        // Fallback: use default config directory
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".awesome-imap-mcp", "accounts.json");
    }

    private static AccountsData Load(string filePath)
    {
        if (!File.Exists(filePath))
            return new AccountsData();

        var json = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(json))
            return new AccountsData();

        return JsonSerializer.Deserialize<AccountsData>(json, ReadOptions)
               ?? new AccountsData();
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_data, WriteOptions);
        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tempPath = _filePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* best effort cleanup */ }
        }
    }
}
