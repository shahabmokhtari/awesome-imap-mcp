using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace UltimateImapMcp.Llm.Acp;

/// <summary>
/// Lightweight Agent Client Protocol client that spawns an agent process
/// (e.g., "claude --acp" or "gh copilot --acp") via stdio and communicates
/// using JSON-RPC 2.0.
///
/// This is a minimal implementation covering only the subset of ACP needed for
/// prompt-response workflows. The ACP protocol is still evolving.
/// </summary>
public class AcpClient : IAsyncDisposable
{
    private readonly string _command;
    private readonly string[] _args;
    private readonly ILogger<AcpClient> _logger;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, string>? _envVars;

    private Process? _process;
    private int _nextId = 1;
    private bool _disposed;
    private bool _initialized;

    public AcpClient(string command, string[] args, ILogger<AcpClient> logger, TimeSpan? timeout = null,
        Dictionary<string, string>? envVars = null)
    {
        _command = command;
        _args = args;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _envVars = envVars;
    }

    /// <summary>
    /// Spawns the agent process and performs the ACP initialize handshake.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
            return;

        _logger.LogDebug("Spawning ACP agent: {Command} {Args}", _command, string.Join(" ", _args));

        var startInfo = new ProcessStartInfo
        {
            FileName = _command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in _args)
            startInfo.ArgumentList.Add(arg);

        if (_envVars is not null)
        {
            foreach (var (key, value) in _envVars)
                startInfo.Environment[key] = value;
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start ACP agent process: {_command}");

        // Send initialize request (ACP protocol version is a number, not a date string)
        var initRequest = CreateRequest("initialize", new
        {
            protocolVersion = 1,
            clientInfo = new { name = "ultimate-imap-mcp", title = "Ultimate IMAP MCP", version = "0.1.0" },
            clientCapabilities = new
            {
                fs = new { readTextFile = false, writeTextFile = false },
                terminal = false,
            }
        });

        var response = await SendRequestAsync(initRequest, ct).ConfigureAwait(false);
        _logger.LogInformation("ACP agent initialized: {Response}", response);
        _initialized = true;
    }

    /// <summary>
    /// Creates a new ACP session with the given working directory.
    /// </summary>
    public async Task<AcpSession> CreateSessionAsync(string workingDir, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var request = CreateRequest("session/new", new
        {
            cwd = workingDir,
            mcpServers = Array.Empty<object>(),
        });

        var response = await SendRequestAsync(request, ct).ConfigureAwait(false);
        var sessionId = response.RootElement.TryGetProperty("result", out var result)
            && result.TryGetProperty("sessionId", out var sid)
            ? sid.GetString() ?? Guid.NewGuid().ToString()
            : Guid.NewGuid().ToString();

        return new AcpSession(sessionId, workingDir);
    }

    /// <summary>
    /// Sends a prompt to the agent and streams back events.
    /// </summary>
    public async IAsyncEnumerable<AcpEvent> SendPromptAsync(
        AcpSession session, string prompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var request = CreateRequest("session/prompt", new
        {
            sessionId = session.SessionId,
            prompt = new[] { new { type = "text", text = prompt } }
        });

        await WriteRequestAsync(request, ct).ConfigureAwait(false);

        // Read streaming events until we get a complete or error event
        await foreach (var acpEvent in ReadEventsAsync(ct).ConfigureAwait(false))
        {
            yield return acpEvent;

            if (acpEvent.Type is AcpEventType.Complete or AcpEventType.Error)
                yield break;

            // Auto-deny permission requests
            if (acpEvent.Type == AcpEventType.PermissionRequest)
            {
                _logger.LogDebug("Auto-denying permission request from ACP agent");
                var denyRequest = CreateRequest("session/request_permission", new
                {
                    sessionId = session.SessionId,
                    allowed = false
                });
                await WriteRequestAsync(denyRequest, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Cancels an in-flight prompt.
    /// </summary>
    public async Task CancelAsync(AcpSession session, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var request = CreateRequest("session/cancel", new
        {
            sessionId = session.SessionId
        });

        await SendRequestAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans up a session. ACP doesn't define a delete method —
    /// sessions are cleaned up when the process exits.
    /// This is a no-op kept for interface compatibility.
    /// </summary>
    public Task DeleteSessionAsync(AcpSession session, CancellationToken ct = default)
    {
        _logger.LogDebug("Session {SessionId} cleanup requested (no-op, cleaned on process exit)", session.SessionId);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error killing ACP agent process");
            }
        }

        _process?.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (!_initialized)
            await InitializeAsync(ct).ConfigureAwait(false);
    }

    private string CreateRequest(string method, object? @params = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        };
        return JsonSerializer.Serialize(request);
    }

    private async Task WriteRequestAsync(string request, CancellationToken ct)
    {
        if (_process?.StandardInput is null)
            throw new InvalidOperationException("ACP agent process is not running");

        _logger.LogDebug("ACP -> {Request}", request);
        await _process.StandardInput.WriteLineAsync(request.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendRequestAsync(string request, CancellationToken ct)
    {
        await WriteRequestAsync(request, ct).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            // Read lines until we find a valid JSON-RPC response (starts with '{').
            // Some ACP bridges (e.g., claude-code-acp) emit debug logs to stdout.
            while (true)
            {
                var line = await (_process?.StandardOutput.ReadLineAsync(timeoutCts.Token)
                    ?? throw new InvalidOperationException("ACP agent process is not running")).ConfigureAwait(false);

                if (line is null)
                    throw new InvalidOperationException("ACP agent process closed stdout unexpectedly");

                // Skip non-JSON lines (debug output from bridge)
                if (!line.TrimStart().StartsWith('{'))
                {
                    _logger.LogTrace("ACP (non-JSON): {Line}", line);
                    continue;
                }

                _logger.LogDebug("ACP <- {Response}", line);
                return JsonDocument.Parse(line);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("ACP agent did not respond within {Timeout}s", _timeout.TotalSeconds);
            throw new TimeoutException($"ACP agent did not respond within {_timeout.TotalSeconds}s");
        }
    }

    private async IAsyncEnumerable<AcpEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_process?.StandardOutput is null)
            yield break;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            string? line;
            var timedOut = false;
            try
            {
                line = await _process.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                line = null;
            }

            if (timedOut)
            {
                yield return new AcpEvent
                {
                    Type = AcpEventType.Error,
                    Error = "ACP agent response timed out"
                };
                yield break;
            }

            if (line is null)
            {
                yield return new AcpEvent
                {
                    Type = AcpEventType.Error,
                    Error = "ACP agent process closed stdout"
                };
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Skip non-JSON lines (debug output from ACP bridge)
            if (!line.TrimStart().StartsWith('{'))
            {
                _logger.LogTrace("ACP (non-JSON): {Line}", line);
                continue;
            }

            _logger.LogDebug("ACP event <- {Line}", line);

            AcpEvent acpEvent;
            try
            {
                var doc = JsonDocument.Parse(line);
                acpEvent = ParseEvent(doc);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse ACP event: {Line}", line);
                continue;
            }

            // Reset timeout on each event
            timeoutCts.CancelAfter(_timeout);
            yield return acpEvent;

            if (acpEvent.Type is AcpEventType.Complete or AcpEventType.Error)
                yield break;
        }
    }

    private static AcpEvent ParseEvent(JsonDocument doc)
    {
        var root = doc.RootElement;

        // Check for JSON-RPC error
        if (root.TryGetProperty("error", out var error))
        {
            return new AcpEvent
            {
                Type = AcpEventType.Error,
                Error = error.TryGetProperty("message", out var msg) ? msg.GetString() : error.ToString()
            };
        }

        // Check for session/prompt result (final response with stopReason)
        if (root.TryGetProperty("result", out var result))
        {
            if (result.TryGetProperty("stopReason", out _))
            {
                return new AcpEvent { Type = AcpEventType.Complete };
            }

            // Legacy: check for type-based result
            var eventType = result.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (eventType is not null)
            {
                return eventType switch
                {
                    "text_delta" => new AcpEvent
                    {
                        Type = AcpEventType.TextDelta,
                        Text = result.TryGetProperty("text", out var text) ? text.GetString() : null
                    },
                    "complete" => new AcpEvent { Type = AcpEventType.Complete },
                    "error" => new AcpEvent
                    {
                        Type = AcpEventType.Error,
                        Error = result.TryGetProperty("message", out var errMsg) ? errMsg.GetString() : null
                    },
                    _ => new AcpEvent { Type = AcpEventType.TextDelta, Text = result.ToString() }
                };
            }

            return new AcpEvent { Type = AcpEventType.Complete };
        }

        // Check for session/update notification (streaming events)
        if (root.TryGetProperty("method", out var method) && method.GetString() == "session/update"
            && root.TryGetProperty("params", out var @params)
            && @params.TryGetProperty("update", out var update))
        {
            var updateType = update.TryGetProperty("sessionUpdate", out var su)
                ? su.GetString() : null;

            return updateType switch
            {
                "agent_message_chunk" => ParseAgentMessageChunk(update),
                "agent_message_start" => new AcpEvent { Type = AcpEventType.TextDelta, Text = "" },
                "agent_message_end" => new AcpEvent { Type = AcpEventType.TextDelta, Text = "" },
                "tool_use" or "tool_use_start" => new AcpEvent { Type = AcpEventType.ToolUse },
                "tool_result" => new AcpEvent { Type = AcpEventType.TextDelta, Text = "" },
                "permission_request" => new AcpEvent { Type = AcpEventType.PermissionRequest },
                _ => new AcpEvent { Type = AcpEventType.TextDelta, Text = "" }
            };
        }

        // Fallback
        return new AcpEvent { Type = AcpEventType.TextDelta, Text = "" };
    }

    private static AcpEvent ParseAgentMessageChunk(JsonElement update)
    {
        // Extract text from content block: { content: { type: "text", text: "..." } }
        if (update.TryGetProperty("content", out var content)
            && content.TryGetProperty("type", out var ct)
            && ct.GetString() == "text"
            && content.TryGetProperty("text", out var text))
        {
            return new AcpEvent { Type = AcpEventType.TextDelta, Text = text.GetString() };
        }

        return new AcpEvent { Type = AcpEventType.TextDelta, Text = "" };
    }
}

/// <summary>Represents an ACP session.</summary>
public record AcpSession(string SessionId, string WorkingDir);

/// <summary>Types of events received from an ACP agent.</summary>
public enum AcpEventType
{
    TextDelta,
    ToolUse,
    PermissionRequest,
    Complete,
    Error
}

/// <summary>An event received from an ACP agent during prompt processing.</summary>
public record AcpEvent
{
    public AcpEventType Type { get; init; }
    public string? Text { get; init; }
    public string? Error { get; init; }
}
