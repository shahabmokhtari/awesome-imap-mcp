using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UltimateImapMcp.Core;

/// <summary>
/// Centralized telemetry: ActivitySource for tracing, Meter for metrics.
/// Uses System.Diagnostics so zero overhead when no listener is attached.
/// </summary>
public static class Telemetry
{
    public static readonly ActivitySource ActivitySource = new("UltimateImapMcp");
    public static readonly Meter Meter = new("UltimateImapMcp");

    // Histograms
    public static readonly Histogram<double> ImapLatency = Meter.CreateHistogram<double>("imap.command_ms");
    public static readonly Histogram<double> SmtpLatency = Meter.CreateHistogram<double>("smtp.send_ms");
    public static readonly Histogram<double> LlmLatency = Meter.CreateHistogram<double>("llm.request_ms");
    public static readonly Histogram<double> McpToolLatency = Meter.CreateHistogram<double>("mcp.tool_call_ms");

    // Counters
    public static readonly Counter<long> EmailsSynced = Meter.CreateCounter<long>("sync.messages_synced");
    public static readonly Counter<long> OperationsQueued = Meter.CreateCounter<long>("queue.enqueued");
    public static readonly Counter<long> OperationsCompleted = Meter.CreateCounter<long>("queue.completed");
    public static readonly Counter<long> LlmTokensUsed = Meter.CreateCounter<long>("llm.tokens_used");
    public static readonly Counter<long> CacheEvictions = Meter.CreateCounter<long>("cache.evictions");
}
