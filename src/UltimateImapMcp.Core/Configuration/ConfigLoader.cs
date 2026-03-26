using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateImapMcp.Core.Configuration;

/// <summary>
/// Loads and deserialises application configuration from a JSON file,
/// performing environment-variable substitution before parsing.
/// </summary>
public static partial class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads the configuration from <paramref name="path"/>.
    /// Throws <see cref="FileNotFoundException"/> if the file does not exist.
    /// </summary>
    public static AppConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configuration file not found: {path}", path);

        var raw = File.ReadAllText(path);
        var substituted = SubstituteEnvVars(raw);

        var config = JsonSerializer.Deserialize<AppConfig>(substituted, JsonOptions)
                     ?? throw new InvalidOperationException($"Failed to deserialize config from {path}");

        return config;
    }

    /// <summary>
    /// Expands a leading <c>~/</c> in <paramref name="dbPath"/> to the current
    /// user's home directory.
    /// </summary>
    public static string ResolveDbPath(string dbPath)
    {
        if (dbPath.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, dbPath[2..]);
        }
        return dbPath;
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object SaveLock = new();

    /// <summary>
    /// Serialises <paramref name="config"/> and writes it to <paramref name="path"/>.
    /// Sensitive fields (passwords, API keys) are deliberately stripped so that
    /// resolved secrets are never persisted to disk.
    /// The write is atomic (write to .tmp then rename) and serialized with a lock.
    /// </summary>
    public static void SaveToFile(AppConfig config, string path)
    {
        // Create a sanitized copy that strips credentials
        var sanitized = new AppConfig
        {
            Server = config.Server,
            Accounts = config.Accounts.Select(a => new AccountConfig
            {
                Name = a.Name,
                ImapHost = a.ImapHost,
                ImapPort = a.ImapPort,
                SmtpHost = a.SmtpHost,
                SmtpPort = a.SmtpPort,
                SmtpUseSsl = a.SmtpUseSsl,
                Username = a.Username,
                AuthType = a.AuthType,
                Provider = a.Provider,
                ConfirmMode = a.ConfirmMode,
                UndoWindowSeconds = a.UndoWindowSeconds,
                OAuth2ClientId = a.OAuth2ClientId,
                Sync = a.Sync,
                // Password deliberately omitted — never persist resolved passwords
            }).ToList(),
            Cache = config.Cache,
            Queue = config.Queue,
            Llm = new LlmConfig
            {
                Enabled = config.Llm.Enabled,
                Provider = config.Llm.Provider,
                Model = config.Llm.Model,
                ApiKeyEnv = config.Llm.ApiKeyEnv,
                // ApiKey deliberately omitted — may have been resolved from env var
                DailyTokenBudget = config.Llm.DailyTokenBudget,
                MonthlyCostLimit = config.Llm.MonthlyCostLimit,
                AutoAnalyzeNew = config.Llm.AutoAnalyzeNew,
                Acp = config.Llm.Acp,
                ProviderApiKeys = config.Llm.ProviderApiKeys,
            },
            Sync = config.Sync,
            Metrics = config.Metrics,
            OAuthProviders = config.OAuthProviders,
        };

        var json = JsonSerializer.Serialize(sanitized, WriteOptions);
        lock (SaveLock)
        {
            var tempPath = path + ".tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                // Clean up temp file if the rename failed
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Replaces every <c>${VAR_NAME}</c> token in <paramref name="input"/>
    /// with the corresponding environment variable value.  Tokens whose
    /// variable is not set are left unchanged.
    /// </summary>
    private static string SubstituteEnvVars(string input) =>
        EnvVarPattern().Replace(input, m =>
        {
            var varName = m.Groups[1].Value;
            return Environment.GetEnvironmentVariable(varName) ?? m.Value;
        });

    [GeneratedRegex(@"\$\{(\w+)\}")]
    private static partial Regex EnvVarPattern();
}
