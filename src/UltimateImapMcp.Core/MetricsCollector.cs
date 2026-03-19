using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Repositories;

namespace UltimateImapMcp.Core;

/// <summary>
/// Background service that samples System.Diagnostics.Metrics instruments
/// every 30 seconds and persists them to the metrics SQLite table.
/// Also prunes old entries based on configured retention.
/// </summary>
public class MetricsCollector : BackgroundService
{
    private readonly MetricsRepository _metricsRepo;
    private readonly MetricsConfig _config;
    private readonly ILogger<MetricsCollector> _logger;
    private readonly MeterListener _listener;
    private readonly List<(string Name, double Value, string? Tags)> _buffer = [];
    private readonly object _bufferLock = new();

    private static readonly TimeSpan CollectInterval = TimeSpan.FromSeconds(30);

    public MetricsCollector(MetricsRepository metricsRepo, MetricsConfig config,
        ILogger<MetricsCollector> logger)
    {
        _metricsRepo = metricsRepo;
        _config = config;
        _logger = logger;

        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "UltimateImapMcp")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.SetMeasurementEventCallback<long>(OnMeasurementLong);
        _listener.Start();
    }

    private void OnMeasurement(Instrument instrument, double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var tagStr = FormatTags(tags);
        lock (_bufferLock)
        {
            _buffer.Add((instrument.Name, value, tagStr));
        }
    }

    private void OnMeasurementLong(Instrument instrument, long value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var tagStr = FormatTags(tags);
        lock (_bufferLock)
        {
            _buffer.Add((instrument.Name, value, tagStr));
        }
    }

    private static string? FormatTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0) return null;
        var parts = new List<string>(tags.Length);
        foreach (var tag in tags)
        {
            parts.Add($"{tag.Key}={tag.Value}");
        }
        return string.Join(",", parts);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("MetricsCollector started — retention_days={RetentionDays}",
            _config.InternalRetentionDays);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CollectInterval, ct).ConfigureAwait(false);
                FlushBuffer();
                PruneOldMetrics();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MetricsCollector error during flush/prune cycle");
            }
        }

        // Final flush on shutdown
        FlushBuffer();
    }

    /// <summary>
    /// Flushes buffered measurements to the metrics table.
    /// Exposed for testing.
    /// </summary>
    public void FlushBuffer()
    {
        List<(string Name, double Value, string? Tags)> snapshot;
        lock (_bufferLock)
        {
            if (_buffer.Count == 0) return;
            snapshot = new List<(string, double, string?)>(_buffer);
            _buffer.Clear();
        }

        foreach (var (name, value, tags) in snapshot)
        {
            try
            {
                _metricsRepo.Record(name, value, tags);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record metric {Name}", name);
            }
        }
    }

    private void PruneOldMetrics()
    {
        try
        {
            var deleted = _metricsRepo.Prune(_config.InternalRetentionDays);
            if (deleted > 0)
                _logger.LogDebug("Pruned {Count} old metric entries", deleted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prune metrics");
        }
    }

    public override void Dispose()
    {
        _listener.Dispose();
        base.Dispose();
    }
}
