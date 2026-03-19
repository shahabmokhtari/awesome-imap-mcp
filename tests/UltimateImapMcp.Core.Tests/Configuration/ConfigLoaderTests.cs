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
            Assert.True(config.Server.DashboardEnabled);
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
}
