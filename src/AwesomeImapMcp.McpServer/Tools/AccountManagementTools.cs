using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Encryption;
using AwesomeImapMcp.ImapClient;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public class AccountManagementTools(
    AccountsStore accountsStore,
    CredentialEncryptor encryptor,
    AppConfig config,
    ILogger<AccountManagementTools> logger)
{
    private static readonly string[] ValidOAuthProviders = ["gmail", "outlook", "yahoo"];

    [McpServerTool, Description(
        "Get the dashboard URL if the web dashboard is enabled, or a status message if disabled.")]
    public string StartDashboard()
    {
        return McpJsonDefaults.LogToolCall(logger, "start_dashboard",
            new Dictionary<string, object?>(),
            () =>
            {
                try
                {
                    if (!config.Server.DashboardEnabled)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            status = "disabled",
                            message = "Dashboard is not enabled. Set server.dashboard_enabled=true in config.json to enable it."
                        }, McpJsonDefaults.Options);
                    }

                    var url = $"http://localhost:{config.Server.DashboardPort}";
                    return JsonSerializer.Serialize(new
                    {
                        status = "enabled",
                        url,
                        message = $"Dashboard is available at {url}"
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "StartDashboard failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description(
        "Add a new IMAP email account to the configuration. " +
        "The password is encrypted before storage.")]
    public string AddAccountImap(
        [Description("Display name for the account")] string name,
        [Description("IMAP server hostname")] string imapHost,
        [Description("IMAP server port")] int imapPort = 993,
        [Description("Login username (usually email address)")] string username = "",
        [Description("App password or account password")] string password = "",
        [Description("SMTP server hostname (optional)")] string? smtpHost = null,
        [Description("SMTP server port")] int smtpPort = 587,
        [Description("Email provider (generic, gmail, outlook, yahoo, zoho)")] string provider = "generic",
        [Description("Whether SMTP uses SSL/TLS")] bool smtpUseSsl = false)
    {
        return McpJsonDefaults.LogToolCall(logger, "add_account_imap",
            new Dictionary<string, object?>
            {
                ["name"] = name,
                ["imapHost"] = imapHost,
                ["imapPort"] = imapPort,
                ["username"] = username,
                ["smtpHost"] = smtpHost,
                ["smtpPort"] = smtpPort,
                ["provider"] = provider,
                ["smtpUseSsl"] = smtpUseSsl
            },
            () =>
            {
                try
                {
                    // Validate required fields
                    if (string.IsNullOrWhiteSpace(name))
                        return McpJsonDefaults.Error("Account name is required.");
                    if (string.IsNullOrWhiteSpace(imapHost))
                        return McpJsonDefaults.Error("IMAP host is required.");
                    if (string.IsNullOrWhiteSpace(username))
                        return McpJsonDefaults.Error("Username is required.");
                    if (string.IsNullOrWhiteSpace(password))
                        return McpJsonDefaults.Error("Password is required.");

                    var id = AccountConfigMapper.DeriveIdFromName(name);

                    // Check for duplicates
                    var existing = accountsStore.Read();
                    if (existing.Accounts.Any(a =>
                            string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return McpJsonDefaults.Error($"An account with name '{name}' already exists.");
                    }

                    var credentialsEnc = encryptor.Encrypt(password);

                    var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                    var entry = new AccountEntry
                    {
                        Id = id,
                        Name = name,
                        ImapHost = imapHost,
                        ImapPort = imapPort,
                        SmtpHost = smtpHost,
                        SmtpPort = smtpPort,
                        SmtpUseSsl = smtpUseSsl,
                        Username = username,
                        AuthType = "app_password",
                        CredentialsEnc = credentialsEnc,
                        Provider = provider,
                        BackendType = "imap",
                        Enabled = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    };

                    accountsStore.Write(data => data.Accounts.Add(entry));

                    return JsonSerializer.Serialize(new
                    {
                        status = "added",
                        id,
                        name,
                        imap_host = imapHost,
                        imap_port = imapPort,
                        username,
                        provider,
                        message = $"Account '{name}' added successfully. Restart the server to begin syncing."
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "AddAccountImap failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description(
        "Start the OAuth2 authentication flow for a supported email provider. " +
        "Requires the dashboard to be enabled. Returns a URL to complete the OAuth flow.")]
    public string AddAccountOauth(
        [Description("OAuth provider: gmail, outlook, or yahoo")] string provider)
    {
        return McpJsonDefaults.LogToolCall(logger, "add_account_oauth",
            new Dictionary<string, object?> { ["provider"] = provider },
            () =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(provider))
                        return McpJsonDefaults.Error("Provider is required.");

                    var normalizedProvider = provider.Trim().ToLowerInvariant();

                    if (!ValidOAuthProviders.Contains(normalizedProvider))
                    {
                        return McpJsonDefaults.Error(
                            $"Invalid provider '{provider}'. Supported providers: {string.Join(", ", ValidOAuthProviders)}.");
                    }

                    if (!config.Server.DashboardEnabled)
                    {
                        return McpJsonDefaults.Error(
                            "OAuth flow requires the dashboard to be enabled. " +
                            "Set server.dashboard_enabled=true in config.json.");
                    }

                    var dashboardUrl = $"http://localhost:{config.Server.DashboardPort}";
                    var oauthUrl = $"{dashboardUrl}/oauth/{normalizedProvider}/start";

                    return JsonSerializer.Serialize(new
                    {
                        status = "ready",
                        provider = normalizedProvider,
                        url = oauthUrl,
                        message = $"Open the following URL to complete the {normalizedProvider} OAuth flow: {oauthUrl}"
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "AddAccountOauth failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }
}
