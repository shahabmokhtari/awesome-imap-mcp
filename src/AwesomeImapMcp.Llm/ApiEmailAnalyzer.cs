using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AwesomeImapMcp.Llm;

/// <summary>
/// Analyzes emails by calling an external LLM API via Microsoft.Extensions.AI's IChatClient.
/// Works with any provider that supports the IChatClient abstraction (OpenAI, Anthropic, etc.).
/// </summary>
public class ApiEmailAnalyzer : IEmailAnalyzer
{
    private readonly IChatClient _chatClient;
    private readonly string _model;
    private readonly ILogger<ApiEmailAnalyzer> _logger;
    private readonly Dictionary<string, string>? _customPrompts;

    public ApiEmailAnalyzer(IChatClient chatClient, string model, ILogger<ApiEmailAnalyzer> logger, Dictionary<string, string>? customPrompts = null)
    {
        _chatClient = chatClient;
        _model = model;
        _logger = logger;
        _customPrompts = customPrompts;
    }

    public bool SupportsBackgroundAnalysis => true;

    public async Task<AnalysisResult> AnalyzeAsync(EmailContent email, AnalysisType type, CancellationToken ct = default)
    {
        var systemPrompt = BuildSystemPrompt(type, _customPrompts);
        var userPrompt = BuildUserPrompt(email, type);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        _logger.LogDebug("Sending {AnalysisType} analysis request to {Model}", type, _model);

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);

        var text = response.Text ?? "{}";

        // Extract JSON from the response (handle cases where model wraps in markdown)
        var json = ExtractJson(text);

        var usage = response.Usage;

        return new AnalysisResult
        {
            Type = type,
            ResultJson = json,
            ModelUsed = response.ModelId ?? _model,
            TokensInput = (int?)usage?.InputTokenCount,
            TokensOutput = (int?)usage?.OutputTokenCount,
            CostUsd = EstimateCost(_model, (int?)usage?.InputTokenCount, (int?)usage?.OutputTokenCount)
        };
    }

    public static string BuildSystemPrompt(AnalysisType type, Dictionary<string, string>? customPrompts = null)
    {
        // Check for user-configured prompt override
        var typeKey = type switch
        {
            AnalysisType.SpamScore => "spam_score",
            AnalysisType.Category => "category",
            AnalysisType.Priority => "priority",
            AnalysisType.Summary => "summary",
            AnalysisType.Custom => "custom",
            _ => null
        };

        if (typeKey is not null && customPrompts is not null
            && customPrompts.TryGetValue(typeKey, out var customPrompt)
            && !string.IsNullOrWhiteSpace(customPrompt))
        {
            return customPrompt;
        }

        // Default prompts
        return type switch
        {
            AnalysisType.SpamScore =>
                "You are an email spam analysis assistant. Analyze the given email and return ONLY a JSON object " +
                "with the following structure: { \"score\": <0-100 integer>, \"label\": \"spam\"|\"likely_spam\"|\"not_spam\", " +
                "\"explanation\": \"<brief explanation>\" }. Score 0 = definitely not spam, 100 = definitely spam.",

            AnalysisType.Category =>
                "You are an email categorization assistant. Categorize the given email and return ONLY a JSON object " +
                "with the following structure: { \"category\": \"newsletter\"|\"transactional\"|\"personal\"|\"work\"" +
                "|\"spam\"|\"social\"|\"promotions\"|\"updates\", \"confidence\": <0.0-1.0>, " +
                "\"explanation\": \"<brief explanation>\" }.",

            AnalysisType.Priority =>
                "You are an email priority assessment assistant. Assess the priority of the given email and return " +
                "ONLY a JSON object with the following structure: { \"priority\": \"low\"|\"normal\"|\"high\"|\"urgent\", " +
                "\"explanation\": \"<brief explanation>\" }.",

            AnalysisType.Summary =>
                "You are an email summarization assistant. Summarize the given email and return ONLY a JSON object " +
                "with the following structure: { \"summary\": \"<one paragraph summary>\", " +
                "\"key_points\": [\"<point1>\", \"<point2>\", ...] }.",

            AnalysisType.Custom =>
                "You are an email analysis assistant. Analyze the given email according to the user's instructions " +
                "and return ONLY a JSON object with your analysis results.",

            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown analysis type")
        };
    }

    public static string BuildUserPrompt(EmailContent email, AnalysisType type)
    {
        var body = email.Body ?? email.Snippet ?? "(no body available)";
        if (body.Length > 4000)
            body = body[..4000] + "... [truncated]";

        return $"""
            Analyze this email:

            Subject: {email.Subject}
            From: {email.From}
            Body:
            {body}
            """;
    }

    /// <summary>
    /// Extracts JSON from a response that might be wrapped in markdown code fences.
    /// </summary>
    public static string ExtractJson(string text)
    {
        text = text.Trim();

        // Handle ```json ... ``` wrapping
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
            {
                text = text[(firstNewline + 1)..];
                var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0)
                    text = text[..lastFence];
            }
        }

        text = text.Trim();

        // Validate it's parseable JSON
        try
        {
            JsonDocument.Parse(text);
            return text;
        }
        catch (JsonException)
        {
            // Return as-is, wrapped in an error object
            return JsonSerializer.Serialize(new { raw_response = text, parse_error = true });
        }
    }

    /// <summary>
    /// Rough cost estimation per model. Returns null if model is unknown.
    /// </summary>
    public static decimal? EstimateCost(string model, int? tokensInput, int? tokensOutput)
    {
        if (tokensInput is null && tokensOutput is null)
            return null;

        var (inputPricePer1M, outputPricePer1M) = model.ToLowerInvariant() switch
        {
            var m when m.Contains("gpt-4o-mini") => (0.15m, 0.60m),
            var m when m.Contains("gpt-4o") => (2.50m, 10.00m),
            var m when m.Contains("gpt-4") => (30.00m, 60.00m),
            var m when m.Contains("claude-haiku") => (0.25m, 1.25m),
            var m when m.Contains("claude-sonnet") => (3.00m, 15.00m),
            var m when m.Contains("claude-opus") => (15.00m, 75.00m),
            _ => (0m, 0m)
        };

        if (inputPricePer1M == 0 && outputPricePer1M == 0)
            return null;

        var inputCost = (tokensInput ?? 0) * inputPricePer1M / 1_000_000m;
        var outputCost = (tokensOutput ?? 0) * outputPricePer1M / 1_000_000m;
        return inputCost + outputCost;
    }
}
