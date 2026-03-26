using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.Core.Tests.Configuration;

public class ConfigLoaderTests
{
    [Fact]
    public void Load_ValidJson_ReturnsConfig()
    {
        var json = """
        {
          "server": { "transport": "stdio", "dashboard_port": 3847, "dashboard_enabled": false },
          "accounts": [{ "name": "personal", "imap_host": "imap.gmail.com", "imap_port": 993, "username": "test@gmail.com", "auth_type": "app_password", "provider": "gmail" }],
          "cache": { "db_path": "/tmp/test.db", "max_size_mb": 500 }
        }
        """;
        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, json);
        try
        {
            var config = ConfigLoader.LoadFromFile(tmpFile);
            Assert.Equal("stdio", config.Server.Transport);
            Assert.Equal(3847, config.Server.DashboardPort);
            Assert.False(config.Server.DashboardEnabled);
            Assert.Single(config.Accounts);
            Assert.Equal("personal", config.Accounts[0].Name);
            Assert.Equal("imap.gmail.com", config.Accounts[0].ImapHost);
            Assert.Equal(500, config.Cache.MaxSizeMb);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => ConfigLoader.LoadFromFile("/nonexistent/config.json"));
    }

    [Fact]
    public void Load_EmptyAccounts_DefaultsApplied()
    {
        var json = """{ "accounts": [] }""";
        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, json);
        try
        {
            var config = ConfigLoader.LoadFromFile(tmpFile);
            Assert.Equal("stdio", config.Server.Transport);
            Assert.Equal(3847, config.Server.DashboardPort);
            Assert.False(config.Server.DashboardEnabled);
            Assert.Equal(500, config.Cache.MaxSizeMb);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void Load_EnvVarSubstitution_ResolvesVariable()
    {
        Environment.SetEnvironmentVariable("TEST_IMAP_PASSWORD", "secret123");
        var json = """
        {
          "accounts": [{ "name": "test", "imap_host": "imap.test.com", "imap_port": 993, "username": "test@test.com", "auth_type": "password", "provider": "generic", "password": "${TEST_IMAP_PASSWORD}" }]
        }
        """;
        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, json);
        try
        {
            var config = ConfigLoader.LoadFromFile(tmpFile);
            Assert.Equal("secret123", config.Accounts[0].Password);
        }
        finally { File.Delete(tmpFile); Environment.SetEnvironmentVariable("TEST_IMAP_PASSWORD", null); }
    }

    [Fact]
    public void ResolveDbPath_TildePath_ExpandsToHome()
    {
        var result = ConfigLoader.ResolveDbPath("~/data/cache.db");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.StartsWith(home, result);
        Assert.EndsWith(Path.Combine("data", "cache.db"), result);
        Assert.DoesNotContain("~", result);
    }

    [Fact]
    public void ResolveDbPath_AbsolutePath_ReturnsUnchanged()
    {
        var absolute = "/var/data/cache.db";
        var result = ConfigLoader.ResolveDbPath(absolute);
        Assert.Equal(absolute, result);
    }

    [Fact]
    public void Load_NullDeserialization_ThrowsInvalidOperation()
    {
        // A JSON file that deserializes to null (the literal word "null")
        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, "null");
        try
        {
            Assert.Throws<InvalidOperationException>(() => ConfigLoader.LoadFromFile(tmpFile));
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void Load_GlobalSyncConfig_DeserializesCorrectly()
    {
        var json = """
        {
          "sync": { "enabled": false, "poll_interval": 120, "max_messages_per_sync": 250 }
        }
        """;
        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, json);
        try
        {
            var config = ConfigLoader.LoadFromFile(tmpFile);
            Assert.False(config.Sync.Enabled);
            Assert.Equal(120, config.Sync.PollInterval);
            Assert.Equal(250, config.Sync.MaxMessagesPerSync);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void Load_ProviderApiKeys_DeserializesCorrectly()
    {
        var json = """
        {
          "llm": {
            "enabled": true,
            "provider": "openai",
            "provider_api_keys": { "openai": "sk-test", "anthropic": "ant-test" }
          }
        }
        """;
        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, json);
        try
        {
            var config = ConfigLoader.LoadFromFile(tmpFile);
            Assert.Equal("sk-test", config.Llm.ProviderApiKeys["openai"]);
            Assert.Equal("ant-test", config.Llm.ProviderApiKeys["anthropic"]);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void SaveToFile_PreservesProviderApiKeys()
    {
        var config = new AppConfig
        {
            Llm = new LlmConfig
            {
                Provider = "openai",
                ProviderApiKeys = new() { ["openai"] = "sk-abc", ["anthropic"] = "ant-xyz" }
            }
        };
        var tmpFile = Path.GetTempFileName();
        try
        {
            ConfigLoader.SaveToFile(config, tmpFile);
            var reloaded = ConfigLoader.LoadFromFile(tmpFile);
            Assert.Equal("sk-abc", reloaded.Llm.ProviderApiKeys["openai"]);
            Assert.Equal("ant-xyz", reloaded.Llm.ProviderApiKeys["anthropic"]);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void SaveToFile_StripsGlobalApiKey_PreservesProviderKeys()
    {
        var config = new AppConfig
        {
            Llm = new LlmConfig
            {
                ApiKey = "resolved-secret-should-not-persist",
                ProviderApiKeys = new() { ["openai"] = "sk-persist-me" }
            }
        };
        var tmpFile = Path.GetTempFileName();
        try
        {
            ConfigLoader.SaveToFile(config, tmpFile);
            var reloaded = ConfigLoader.LoadFromFile(tmpFile);
            Assert.Null(reloaded.Llm.ApiKey);
            Assert.Equal("sk-persist-me", reloaded.Llm.ProviderApiKeys["openai"]);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void SaveToFile_PreservesGlobalSyncConfig()
    {
        var config = new AppConfig
        {
            Sync = new GlobalSyncConfig { Enabled = false, PollInterval = 120, MaxMessagesPerSync = 250 }
        };
        var tmpFile = Path.GetTempFileName();
        try
        {
            ConfigLoader.SaveToFile(config, tmpFile);
            var reloaded = ConfigLoader.LoadFromFile(tmpFile);
            Assert.False(reloaded.Sync.Enabled);
            Assert.Equal(120, reloaded.Sync.PollInterval);
            Assert.Equal(250, reloaded.Sync.MaxMessagesPerSync);
        }
        finally { File.Delete(tmpFile); }
    }
}
