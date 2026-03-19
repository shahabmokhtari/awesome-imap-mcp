using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace UltimateImapMcp.Llm.Acp;

/// <summary>
/// Analyzes emails by spawning an ACP-compatible agent (Claude Code, GitHub Copilot)
/// and sending structured prompts via the Agent Client Protocol.
/// </summary>
public class AcpEmailAnalyzer : IEmailAnalyzer, IAsyncDisposable
{
    private readonly AcpClient _client;
    private readonly ILogger<AcpEmailAnalyzer> _logger;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private AcpSession? _session;

    public AcpEmailAnalyzer(AcpClient client, ILogger<AcpEmailAnalyzer> logger)
    {
        _client = client;
        _logger = logger;
    }

    public bool SupportsBackgroundAnalysis => true;

    public async Task<AnalysisResult> AnalyzeAsync(EmailContent email, AnalysisType type, CancellationToken ct = default)
    {
        await EnsureSessionAsync(ct).ConfigureAwait(false);

        var prompt = BuildAcpPrompt(email, type);
        var responseText = new StringBuilder();

        _logger.LogDebug("Sending {AnalysisType} analysis via ACP", type);

        await foreach (var acpEvent in _client.SendPromptAsync(_session!, prompt, ct).ConfigureAwait(false))
        {
            switch (acpEvent.Type)
            {
                case AcpEventType.TextDelta:
                    responseText.Append(acpEvent.Text);
                    break;
                case AcpEventType.Complete:
                    if (acpEvent.Text is not null)
                        responseText.Append(acpEvent.Text);
                    break;
                case AcpEventType.Error:
                    _logger.LogError("ACP analysis error: {Error}", acpEvent.Error);
                    return new AnalysisResult
                    {
                        Type = type,
                        ResultJson = JsonSerializer.Serialize(new { error = acpEvent.Error }),
                        ModelUsed = "acp"
                    };
            }
        }

        var json = ApiEmailAnalyzer.ExtractJson(responseText.ToString());

        return new AnalysisResult
        {
            Type = type,
            ResultJson = json,
            ModelUsed = "acp"
        };
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is not null)
            return;

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is not null)
                return;

            await _client.InitializeAsync(ct).ConfigureAwait(false);
            _session = await _client.CreateSessionAsync(
                Path.GetTempPath(), ct).ConfigureAwait(false);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private static string BuildAcpPrompt(EmailContent email, AnalysisType type)
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

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            try
            {
                await _client.DeleteSessionAsync(_session).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up ACP session");
            }
        }

        await _client.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
