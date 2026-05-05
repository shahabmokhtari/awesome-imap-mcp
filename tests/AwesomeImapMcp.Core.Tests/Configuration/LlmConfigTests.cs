using System.Text.Json;
using AwesomeImapMcp.Core.Configuration;

namespace AwesomeImapMcp.Core.Tests.Configuration;

public class LlmConfigTests
{
    // ---------------------------------------------------------------
    // ProviderApiKeys redaction helpers (mirrors SettingsApi GET logic)
    // ---------------------------------------------------------------

    [Fact]
    public void ProviderApiKeys_Redaction_MasksNonEmptyKeys()
    {
        // Mirrors SettingsApi.cs GET: kvp => string.IsNullOrEmpty(kvp.Value) ? "" : "***"
        var keys = new Dictionary<string, string> { ["openai"] = "sk-real-secret", ["anthropic"] = "ant-key" };
        var redacted = keys.ToDictionary(kvp => kvp.Key, kvp => string.IsNullOrEmpty(kvp.Value) ? "" : "***");

        Assert.Equal("***", redacted["openai"]);
        Assert.Equal("***", redacted["anthropic"]);
    }

    [Fact]
    public void ProviderApiKeys_Redaction_PreservesEmptyAsEmpty()
    {
        var keys = new Dictionary<string, string> { ["openai"] = "" };
        var redacted = keys.ToDictionary(kvp => kvp.Key, kvp => string.IsNullOrEmpty(kvp.Value) ? "" : "***");

        Assert.Equal("", redacted["openai"]);
    }

    [Fact]
    public void ProviderApiKeys_SentinelSkip_DoesNotOverwriteRealKey()
    {
        // Mirrors SettingsApi.cs PUT logic
        var existing = new Dictionary<string, string> { ["openai"] = "sk-real-key" };
        var updates = new Dictionary<string, string> { ["openai"] = "***" };

        foreach (var (provider, key) in updates)
        {
            if (key == "***") continue;
            existing[provider] = key;
        }

        Assert.Equal("sk-real-key", existing["openai"]); // Unchanged
    }

    [Fact]
    public void ProviderApiKeys_EmptyValue_RemovesKey()
    {
        // Mirrors SettingsApi.cs PUT logic
        var existing = new Dictionary<string, string> { ["openai"] = "sk-real-key" };
        var updates = new Dictionary<string, string> { ["openai"] = "" };

        foreach (var (provider, key) in updates)
        {
            if (key == "***") continue;
            if (string.IsNullOrEmpty(key))
                existing.Remove(provider);
            else
                existing[provider] = key;
        }

        Assert.False(existing.ContainsKey("openai")); // Removed
    }

    [Fact]
    public void ProviderApiKeys_NewValue_OverwritesExisting()
    {
        var existing = new Dictionary<string, string> { ["openai"] = "sk-old-key" };
        var updates = new Dictionary<string, string> { ["openai"] = "sk-new-key" };

        foreach (var (provider, key) in updates)
        {
            if (key == "***") continue;
            if (string.IsNullOrEmpty(key))
                existing.Remove(provider);
            else
                existing[provider] = key;
        }

        Assert.Equal("sk-new-key", existing["openai"]);
    }

    [Fact]
    public void ResolveApiKey_ProviderSpecificKey_TakesPrecedenceOverGlobal()
    {
        var config = new LlmConfig
        {
            ApiKey = "global-key",
            ProviderApiKeys = new() { ["openai"] = "openai-key" },
        };

        Assert.Equal("openai-key", config.ResolveApiKey("openai"));
    }

    [Fact]
    public void ResolveApiKey_FallsBackToGlobalApiKey_WhenProviderKeyAbsent()
    {
        var config = new LlmConfig
        {
            ApiKey = "global-key",
            ProviderApiKeys = new() { ["anthropic"] = "anthropic-key" },
        };

        Assert.Equal("global-key", config.ResolveApiKey("openai"));
    }

    [Fact]
    public void ResolveApiKey_FallsBackToGlobalApiKey_WhenNoProvider()
    {
        var config = new LlmConfig { ApiKey = "global-key" };

        Assert.Equal("global-key", config.ResolveApiKey());
        Assert.Equal("global-key", config.ResolveApiKey(null));
    }

    [Fact]
    public void ResolveApiKey_ReturnsNull_WhenAllSourcesAbsent()
    {
        var config = new LlmConfig();

        Assert.Null(config.ResolveApiKey("openai"));
        Assert.Null(config.ResolveApiKey());
    }

    [Fact]
    public void ResolveApiKey_SkipsEmptyProviderKey_FallsBackToGlobal()
    {
        var config = new LlmConfig
        {
            ApiKey = "global-key",
            ProviderApiKeys = new() { ["openai"] = "" },
        };

        Assert.Equal("global-key", config.ResolveApiKey("openai"));
    }

    [Fact]
    public void ResolveApiKey_SkipsSentinelValue_FallsBackToGlobal()
    {
        var config = new LlmConfig
        {
            ApiKey = "global-key",
            ProviderApiKeys = new() { ["openai"] = "***" },
        };

        Assert.Equal("global-key", config.ResolveApiKey("openai"));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("OpenAI")]
    [InlineData("OPENAI")]
    public void ResolveApiKey_ProviderLookup_IsCaseInsensitive(string providerCasing)
    {
        var config = new LlmConfig
        {
            ProviderApiKeys = new() { ["openai"] = "the-key" },
        };

        Assert.Equal("the-key", config.ResolveApiKey(providerCasing));
    }

    [Fact]
    public void ResolveApiKey_EnvVarFallback_WhenApiKeyAndProviderAbsent()
    {
        var envVar = $"TEST_LLM_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envVar, "env-key-value");
        try
        {
            var config = new LlmConfig { ApiKeyEnv = envVar };
            Assert.Equal("env-key-value", config.ResolveApiKey("openai"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public void ResolveApiKey_EmptyEnvVar_ReturnsNull()
    {
        var envVar = $"TEST_EMPTY_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envVar, "");
        try
        {
            var config = new LlmConfig { ApiKeyEnv = envVar };
            Assert.Null(config.ResolveApiKey("openai"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }
}
