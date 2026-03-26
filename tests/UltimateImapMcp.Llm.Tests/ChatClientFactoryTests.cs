using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Llm;

namespace UltimateImapMcp.Llm.Tests;

public class ChatClientFactoryTests
{
    [Theory]
    [InlineData("acp_claude", false)]
    [InlineData("acp_copilot", false)]
    [InlineData("in_context", false)]
    [InlineData("openai", true)]
    [InlineData("anthropic", true)]
    public void RequiresApiKey_ReturnsExpectedValue(string provider, bool expected)
    {
        Assert.Equal(expected, ChatClientFactory.RequiresApiKey(provider));
    }

    [Theory]
    [InlineData("ACP_CLAUDE")]
    [InlineData("Acp_Copilot")]
    [InlineData("IN_CONTEXT")]
    public void RequiresApiKey_IsCaseInsensitive(string provider)
    {
        Assert.False(ChatClientFactory.RequiresApiKey(provider));
    }

    [Theory]
    [InlineData("acp_claude")]
    [InlineData("acp_copilot")]
    [InlineData("in_context")]
    public void Create_KeylessProvider_ThrowsInvalidOperationException(string provider)
    {
        var config = new LlmConfig { Provider = provider };
        var ex = Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(config));
        Assert.Contains("does not use a ChatClient", ex.Message);
    }

    [Fact]
    public void Create_MissingApiKey_ThrowsInvalidOperationException()
    {
        var config = new LlmConfig { Provider = "openai", ApiKey = null };
        var ex = Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(config));
        Assert.Contains("API key not configured", ex.Message);
    }

    [Fact]
    public void Create_UnsupportedProvider_ThrowsInvalidOperationException()
    {
        var config = new LlmConfig { Provider = "gemini", ApiKey = "key" };
        var ex = Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(config));
        Assert.Contains("Unsupported API provider", ex.Message);
    }

    [Fact]
    public void Create_OpenAiProvider_ReturnsIChatClient()
    {
        var config = new LlmConfig { Provider = "openai", ApiKey = "sk-test-key", Model = "gpt-4o" };
        var client = ChatClientFactory.Create(config);
        Assert.NotNull(client);
        (client as IDisposable)?.Dispose();
    }

    [Fact]
    public void Create_AnthropicProvider_ReturnsIChatClient()
    {
        var config = new LlmConfig { Provider = "anthropic", ApiKey = "ant-test-key", Model = "claude-3-5-sonnet" };
        var client = ChatClientFactory.Create(config);
        Assert.NotNull(client);
        (client as IDisposable)?.Dispose();
    }

    [Fact]
    public void Create_UsesProviderSpecificKey()
    {
        var config = new LlmConfig
        {
            Provider = "openai",
            ApiKey = null,
            ProviderApiKeys = new() { ["openai"] = "sk-provider-specific" },
            Model = "gpt-4o"
        };
        var client = ChatClientFactory.Create(config);
        Assert.NotNull(client);
        (client as IDisposable)?.Dispose();
    }
}
