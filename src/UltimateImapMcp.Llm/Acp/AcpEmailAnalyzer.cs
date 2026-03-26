using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace UltimateImapMcp.Llm.Acp;

/// <summary>
/// Analyzes emails by spawning an ACP-compatible agent (Claude Code, GitHub Copilot)
/// and sending structured prompts via the Agent Client Protocol.
/// Automatically recovers by spawning a new process if the agent crashes.
/// </summary>
public class AcpEmailAnalyzer : IEmailAnalyzer, IAsyncDisposable
{
    private readonly string _command;
    private readonly string[] _args;
    private readonly ILogger<AcpEmailAnalyzer> _logger;
    private readonly ILogger<AcpClient> _clientLogger;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private AcpClient? _client;
    private AcpSession? _session;

    public AcpEmailAnalyzer(string command, string[] args,
        ILogger<AcpEmailAnalyzer> logger, ILogger<AcpClient> clientLogger)
    {
        _command = command;
        _args = args;
        _logger = logger;
        _clientLogger = clientLogger;
    }

    /// <summary>Legacy constructor for backward compat — extracts command/args from existing client.</summary>
    public AcpEmailAnalyzer(AcpClient client, ILogger<AcpEmailAnalyzer> logger)
    {
        _client = client;
        _command = "";
        _args = [];
        _logger = logger;
        _clientLogger = logger as ILogger<AcpClient> ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AcpClient>.Instance;
    }

    public bool SupportsBackgroundAnalysis => true;

    public async Task<AnalysisResult> AnalyzeAsync(EmailContent email, AnalysisType type, CancellationToken ct = default)
    {
        await EnsureSessionAsync(ct).ConfigureAwait(false);

        var prompt = BuildAcpPrompt(email, type);
        var responseText = new StringBuilder();

        _logger.LogDebug("Sending {AnalysisType} analysis via ACP", type);

        try
        {
            await foreach (var acpEvent in _client!.SendPromptAsync(_session!, prompt, ct).ConfigureAwait(false))
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
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            // Process died or timed out — reset so next call spawns a fresh process
            _logger.LogWarning(ex, "ACP agent process failed, will respawn on next call");
            await ResetAsync().ConfigureAwait(false);
            throw;
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
        if (_session is not null && _client is not null)
            return;

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is not null && _client is not null)
                return;

            // Create a fresh client (old one may have been disposed after a crash)
            if (_client is null && !string.IsNullOrEmpty(_command))
                _client = new AcpClient(_command, _args, _clientLogger);

            if (_client is null)
                throw new InvalidOperationException("ACP client not configured. Set llm.acp.command in config.");

            await _client.InitializeAsync(ct).ConfigureAwait(false);
            _session = await _client.CreateSessionAsync(
                Path.GetTempPath(), ct).ConfigureAwait(false);

            _logger.LogInformation("ACP session created: {SessionId}", _session.SessionId);
        }
        catch
        {
            // If init fails, dispose the client so next attempt creates a fresh one
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
            _session = null;
            throw;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task ResetAsync()
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
            _session = null;
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
        if (_client is not null)
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
        }
        GC.SuppressFinalize(this);
    }
}
