namespace AwesomeImapMcp.Llm.Acp;

/// <summary>
/// Manages a pool of ACP agent processes. Requests are queued and dispatched
/// to the next available client. Each client processes one request at a time.
/// </summary>
public interface IAcpClientPool : IAsyncDisposable
{
    Task<AcpPromptResult> SendPromptAsync(string prompt, string? model = null,
        CancellationToken ct = default);

    int ActiveClients { get; }
    int QueuedRequests { get; }
}

public record AcpPromptResult
{
    public required string Response { get; init; }
    public string? Model { get; init; }
    public long SessionLatencyMs { get; init; }
    public long PromptLatencyMs { get; init; }
    public string? Error { get; init; }
}
