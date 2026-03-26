using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.Core.Tests.Configuration;

public class LlmConfigTests
{
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
}
