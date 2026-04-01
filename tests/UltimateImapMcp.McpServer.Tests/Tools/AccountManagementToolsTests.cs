using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.McpServer.Tools;

namespace UltimateImapMcp.McpServer.Tests.Tools;

public class AccountManagementToolsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static AppConfig MakeConfig(bool dashboardEnabled = false, int dashboardPort = 3847) =>
        new() { Server = new ServerConfig { DashboardEnabled = dashboardEnabled, DashboardPort = dashboardPort } };

    private static (AccountsStore store, string path) MakeTempStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"accounts-test-{Guid.NewGuid()}.json");
        File.WriteAllText(path, """{"accounts":[],"oauth_tokens":[]}""");
        return (new AccountsStore(path), path);
    }

    private static CredentialEncryptor MakeEncryptor() => new("test-passphrase");

    [Fact]
    public void StartDashboard_WhenDisabled_ReturnsStatus()
    {
        var config = MakeConfig(dashboardEnabled: false);
        var (store, path) = MakeTempStore();
        try
        {
            var tools = new AccountManagementTools(store, null, config,
                NullLogger<AccountManagementTools>.Instance);

            var result = Parse(tools.StartDashboard());

            Assert.Equal("disabled", result.GetProperty("status").GetString());
            Assert.Contains("not enabled", result.GetProperty("message").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StartDashboard_WhenEnabled_ReturnsUrl()
    {
        var config = MakeConfig(dashboardEnabled: true, dashboardPort: 4000);
        var (store, path) = MakeTempStore();
        try
        {
            var tools = new AccountManagementTools(store, null, config,
                NullLogger<AccountManagementTools>.Instance);

            var result = Parse(tools.StartDashboard());

            Assert.Equal("enabled", result.GetProperty("status").GetString());
            Assert.Equal("http://localhost:4000", result.GetProperty("url").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddAccountImap_MissingName_ReturnsError()
    {
        var config = MakeConfig();
        var (store, path) = MakeTempStore();
        try
        {
            var tools = new AccountManagementTools(store, MakeEncryptor(), config,
                NullLogger<AccountManagementTools>.Instance);

            var result = Parse(tools.AddAccountImap("", "imap.example.com", username: "user", password: "pass"));

            Assert.True(result.TryGetProperty("error", out var err));
            Assert.Contains("name", err.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddAccountImap_MissingHost_ReturnsError()
    {
        var config = MakeConfig();
        var (store, path) = MakeTempStore();
        try
        {
            var tools = new AccountManagementTools(store, MakeEncryptor(), config,
                NullLogger<AccountManagementTools>.Instance);

            var result = Parse(tools.AddAccountImap("Test", "", username: "user", password: "pass"));

            Assert.True(result.TryGetProperty("error", out var err));
            Assert.Contains("host", err.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddAccountImap_MissingUsername_ReturnsError()
    {
        var config = MakeConfig();
        var (store, path) = MakeTempStore();
        try
        {
            var tools = new AccountManagementTools(store, MakeEncryptor(), config,
                NullLogger<AccountManagementTools>.Instance);

            var result = Parse(tools.AddAccountImap("Test", "imap.example.com", username: "", password: "pass"));

            Assert.True(result.TryGetProperty("error", out var err));
            Assert.Contains("username", err.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddAccountImap_MissingPassword_ReturnsError()
    {
        var config = MakeConfig();
        var (store, path) = MakeTempStore();
        try
        {
            var tools = new AccountManagementTools(store, MakeEncryptor(), config,
                NullLogger<AccountManagementTools>.Instance);

            var result = Parse(tools.AddAccountImap("Test", "imap.example.com", username: "user", password: ""));

            Assert.True(result.TryGetProperty("error", out var err));
            Assert.Contains("password", err.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddAccountImap_ValidParams_AddsAccount()
    {
        var config = MakeConfig();
        var (store, path) = MakeTempStore();
        var encryptor = MakeEncryptor();
        try
        {
            var tools = new AccountManagementTools(store, encryptor, config,
                NullLogger<AccountManagementTools>.Instance);

            var result = Parse(tools.AddAccountImap(
                "My Work Email",
                "imap.work.com",
                imapPort: 993,
                username: "user@work.com",
                password: "secret123",
                smtpHost: "smtp.work.com",
                smtpPort: 465,
                provider: "generic",
                smtpUseSsl: true));

            Assert.Equal("added", result.GetProperty("status").GetString());
            Assert.Equal("my-work-email", result.GetProperty("id").GetString());
            Assert.Equal("My Work Email", result.GetProperty("name").GetString());

            // Verify account was persisted in the store
            var data = store.Read();
            Assert.Single(data.Accounts);
            var account = data.Accounts[0];
            Assert.Equal("my-work-email", account.Id);
            Assert.Equal("My Work Email", account.Name);
            Assert.Equal("imap.work.com", account.ImapHost);
            Assert.Equal(993, account.ImapPort);
            Assert.Equal("user@work.com", account.Username);
            Assert.Equal("smtp.work.com", account.SmtpHost);
            Assert.Equal(465, account.SmtpPort);
            Assert.True(account.SmtpUseSsl);
            Assert.Equal("generic", account.Provider);
            Assert.Equal("app_password", account.AuthType);
            Assert.True(account.Enabled);

            // Verify password was encrypted
            Assert.NotEqual("secret123", account.CredentialsEnc);
            Assert.Equal("secret123", encryptor.Decrypt(account.CredentialsEnc));

            // Verify it was written to disk
            var fileContent = File.ReadAllText(path);
            Assert.Contains("my-work-email", fileContent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddAccountImap_DuplicateName_ReturnsError()
    {
        var config = MakeConfig();
        var (store, path) = MakeTempStore();
        try
        {
            var tools = new AccountManagementTools(store, MakeEncryptor(), config,
                NullLogger<AccountManagementTools>.Instance);

            // Add first account
            tools.AddAccountImap("Test", "imap.example.com", username: "user", password: "pass");

            // Try to add duplicate
            var result = Parse(tools.AddAccountImap("Test", "imap.other.com", username: "user2", password: "pass2"));

            Assert.True(result.TryGetProperty("error", out var err));
            Assert.Contains("already exists", err.GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddAccountOauth_InvalidProvider_ReturnsError()
    {
        var config = MakeConfig(dashboardEnabled: true);
        var (store, path) = MakeTempStore();
        try
        {
            var tools = new AccountManagementTools(store, null, config,
                NullLogger<AccountManagementTools>.Instance);

            var result = Parse(tools.AddAccountOauth("invalid_provider"));

            Assert.True(result.TryGetProperty("error", out var err));
            Assert.Contains("Invalid provider", err.GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddAccountOauth_DashboardDisabled_ReturnsError()
    {
        var config = MakeConfig(dashboardEnabled: false);
        var (store, path) = MakeTempStore();
        try
        {
            var tools = new AccountManagementTools(store, null, config,
                NullLogger<AccountManagementTools>.Instance);

            var result = Parse(tools.AddAccountOauth("gmail"));

            Assert.True(result.TryGetProperty("error", out var err));
            Assert.Contains("dashboard", err.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddAccountOauth_ValidProvider_ReturnsUrl()
    {
        var config = MakeConfig(dashboardEnabled: true, dashboardPort: 3847);
        var (store, path) = MakeTempStore();
        try
        {
            var tools = new AccountManagementTools(store, null, config,
                NullLogger<AccountManagementTools>.Instance);

            var result = Parse(tools.AddAccountOauth("Gmail"));

            Assert.Equal("ready", result.GetProperty("status").GetString());
            Assert.Equal("gmail", result.GetProperty("provider").GetString());
            Assert.Equal("http://localhost:3847/oauth/gmail/start", result.GetProperty("url").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
