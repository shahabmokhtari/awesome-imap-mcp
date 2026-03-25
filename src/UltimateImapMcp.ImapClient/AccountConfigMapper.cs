using System.Text.Json;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.ImapClient;

/// <summary>
/// Maps an <see cref="AccountRecord"/> from the SQLite database into an
/// <see cref="AccountConfig"/> that the IMAP/SMTP connection managers expect.
/// Decrypts stored credentials as needed.
/// </summary>
public static class AccountConfigMapper
{
    /// <summary>Default sync config applied when the DB account has no explicit config_json.</summary>
    private static readonly SyncConfig DefaultSyncConfig = new()
    {
        IdleFolders = ["INBOX"],
        PollInterval = 300,
        Folders = [new FolderSyncConfig { Path = "INBOX", CacheWindowDays = 60 }]
    };

    public static AccountConfig ToAccountConfig(AccountRecord record, CredentialEncryptor encryptor,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(encryptor);

        string? password = null;
        if (!record.AuthType.Equals("oauth2", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(record.CredentialsEnc))
        {
            password = encryptor.Decrypt(record.CredentialsEnc);
        }

        // Attempt to deserialize config from config_json
        AccountConfigJson? parsed = null;
        if (!string.IsNullOrEmpty(record.ConfigJson))
        {
            try
            {
                parsed = JsonSerializer.Deserialize<AccountConfigJson>(record.ConfigJson);
            }
            catch (JsonException ex)
            {
                logger?.LogWarning(ex, "Malformed config_json for account '{AccountName}', using defaults", record.Name);
            }
        }

        return new AccountConfig
        {
            Name = record.Name,
            ImapHost = record.ImapHost,
            ImapPort = record.ImapPort,
            SmtpHost = record.SmtpHost,
            SmtpPort = record.SmtpPort,
            SmtpUseSsl = record.SmtpUseSsl,
            Username = record.Username,
            AuthType = record.AuthType,
            Provider = record.Provider,
            Password = password,
            Sync = parsed?.Sync ?? DefaultSyncConfig,
            ConfirmMode = parsed?.ConfirmMode ?? "implicit",
            UndoWindowSeconds = parsed?.UndoWindowSeconds ?? 10
        };
    }

    /// <summary>
    /// Generates a deterministic ID from an account name (for config-file imports).
    /// Lowercased, spaces replaced with dashes.
    /// </summary>
    public static string DeriveIdFromName(string name)
    {
        return name.Trim().ToLowerInvariant().Replace(' ', '-');
    }

    /// <summary>
    /// Partial DTO to extract configuration from the config_json column.
    /// Supports both PascalCase (C# serialization) and snake_case (JSON convention) property names.
    /// </summary>
    private sealed class AccountConfigJson
    {
        public SyncConfig? Sync { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("confirm_mode")]
        public string? ConfirmMode { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("undo_window_seconds")]
        public int? UndoWindowSeconds { get; set; }
    }
}
