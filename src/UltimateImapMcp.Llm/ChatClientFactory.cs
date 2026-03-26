using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.Llm;

/// <summary>
/// Creates IChatClient instances from configuration.
/// Supports OpenAI and OpenAI-compatible APIs (including Anthropic via proxy).
/// ACP and in_context providers do not use ChatClient and are not handled here.
/// </summary>
public static class ChatClientFactory
{
    /// <summary>Providers that don't require an API key (they use external agents).</summary>
    private static readonly HashSet<string> KeylessProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "acp_claude", "acp_copilot", "in_context"
    };

    /// <summary>
    /// Creates an IChatClient from LLM configuration.
    /// For "anthropic" provider, uses OpenAI-compatible endpoint.
    /// For "openai" provider, uses native OpenAI client.
    /// For ACP/in_context providers, throws <see cref="InvalidOperationException"/>.
    /// Guard by calling <see cref="RequiresApiKey"/>: only call Create when it returns true.
    /// </summary>
    public static IChatClient Create(LlmConfig config)
    {
        var provider = config.Provider.ToLowerInvariant();

        if (KeylessProviders.Contains(provider))
            throw new InvalidOperationException(
                $"Provider '{provider}' does not use a ChatClient. Use the appropriate ACP/in-context handler instead.");

        var apiKey = config.ResolveApiKey(config.Provider)
            ?? throw new InvalidOperationException(
                $"API key not configured for provider '{provider}'. Set 'api_key' in config, add a provider-specific key in 'provider_api_keys', or set '{config.ApiKeyEnv ?? "API_KEY"}' environment variable.");

        return provider switch
        {
            "openai" => CreateOpenAiClient(apiKey, config.Model),
            "anthropic" => CreateOpenAiClient(apiKey, config.Model, new Uri("https://api.anthropic.com/v1/")),
            _ => throw new InvalidOperationException($"Unsupported API provider: {provider}. Use 'openai', 'anthropic', 'acp_claude', 'acp_copilot', or 'in_context'.")
        };
    }

    /// <summary>Returns true if the given provider requires an API key to create a ChatClient.</summary>
    public static bool RequiresApiKey(string provider) =>
        !KeylessProviders.Contains(provider);

    private static IChatClient CreateOpenAiClient(string apiKey, string model, Uri? endpoint = null)
    {
        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions();
        if (endpoint is not null)
            options.Endpoint = endpoint;

        var openAiClient = new OpenAIClient(credential, options);
        var chatClient = openAiClient.GetChatClient(model);
        return chatClient.AsIChatClient();
    }
}
