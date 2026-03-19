using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.Llm;

/// <summary>
/// Creates IChatClient instances from configuration.
/// Supports OpenAI and OpenAI-compatible APIs (including Anthropic via proxy).
/// </summary>
public static class ChatClientFactory
{
    /// <summary>
    /// Creates an IChatClient from LLM configuration.
    /// For "anthropic" provider, uses OpenAI-compatible endpoint.
    /// For "openai" provider, uses native OpenAI client.
    /// </summary>
    public static IChatClient Create(LlmConfig config)
    {
        var apiKey = config.ResolveApiKey()
            ?? throw new InvalidOperationException(
                $"API key not configured. Set 'api_key' in config or '{config.ApiKeyEnv ?? "API_KEY"}' environment variable.");

        var provider = config.Provider.ToLowerInvariant();

        return provider switch
        {
            "openai" => CreateOpenAiClient(apiKey, config.Model),
            "anthropic" => CreateOpenAiClient(apiKey, config.Model, new Uri("https://api.anthropic.com/v1/")),
            _ => throw new InvalidOperationException($"Unsupported API provider: {provider}. Use 'openai' or 'anthropic'.")
        };
    }

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
