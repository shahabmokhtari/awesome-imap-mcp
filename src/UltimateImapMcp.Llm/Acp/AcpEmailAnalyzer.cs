using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace UltimateImapMcp.Llm.Acp;

/// <summary>
/// Analyzes emails by sending prompts through the ACP client pool.
/// </summary>
public class AcpEmailAnalyzer : IEmailAnalyzer
{
    private readonly IAcpClientPool _pool;
    private readonly ILogger<AcpEmailAnalyzer> _logger;

    public AcpEmailAnalyzer(IAcpClientPool pool, ILogger<AcpEmailAnalyzer> logger)
    {
        _pool = pool;
        _logger = logger;
    }

    public bool SupportsBackgroundAnalysis => true;

    public async Task<AnalysisResult> AnalyzeAsync(EmailContent email, AnalysisType type,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(email, type);
        var result = await _pool.SendPromptAsync(prompt, ct: ct).ConfigureAwait(false);

        if (result.Error is not null)
        {
            _logger.LogError("ACP analysis error: {Error} ({PromptMs}ms)", result.Error, result.PromptLatencyMs);
            return new AnalysisResult
            {
                Type = type,
                ResultJson = JsonSerializer.Serialize(new { error = result.Error }),
                ModelUsed = "acp"
            };
        }

        _logger.LogDebug("ACP analysis completed in {PromptMs}ms", result.PromptLatencyMs);
        var json = ApiEmailAnalyzer.ExtractJson(result.Response);
        return new AnalysisResult
        {
            Type = type,
            ResultJson = json,
            ModelUsed = "acp"
        };
    }

    private static string BuildPrompt(EmailContent email, AnalysisType type)
    {
        var systemPrompt = ApiEmailAnalyzer.BuildSystemPrompt(type);
        var body = email.Body ?? email.Snippet ?? "(no body)";
        if (body.Length > 4000)
            body = body[..4000] + "... [truncated]";

        return $"""
            {systemPrompt}

            Email to analyze:
            Subject: {email.Subject}
            From: {email.From}
            Body:
            {body}

            Respond with ONLY the JSON object, no other text.
            """;
    }
}
