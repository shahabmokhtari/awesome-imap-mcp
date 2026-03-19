using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Repositories;

namespace UltimateImapMcp.Core.Logging;

/// <summary>
/// ILoggerProvider that writes log entries to the SQLite logs table.
/// Buffers entries in memory and batch-writes every 5 seconds.
/// Auto-prunes: debug 7 days, info 30 days, warn/error 90 days.
/// </summary>
public sealed class SqliteLoggerProvider : ILoggerProvider, IDisposable
{
    private readonly LogsRepository _logsRepo;
    private readonly ConcurrentQueue<(string Level, string Category, string Message,
        string? Exception, string? Metadata)> _buffer = new();
    private readonly Timer _flushTimer;
    private readonly Timer _pruneTimer;
    private readonly ConcurrentDictionary<string, SqliteLogger> _loggers = new();
    private bool _disposed;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    public SqliteLoggerProvider(LogsRepository logsRepo)
    {
        _logsRepo = logsRepo;
        _flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
        _pruneTimer = new Timer(_ => Prune(), null, PruneInterval, PruneInterval);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new SqliteLogger(name, this));
    }

    internal void Enqueue(string level, string category, string message,
        string? exception, string? metadata)
    {
        if (_disposed) return;
        _buffer.Enqueue((level, category, message, exception, metadata));
    }

    /// <summary>
    /// Flushes all buffered log entries to the database.
    /// Exposed for testing.
    /// </summary>
    public void Flush()
    {
        if (_buffer.IsEmpty) return;

        var entries = new List<(string Level, string Category, string Message,
            string? Exception, string? Metadata)>();

        while (_buffer.TryDequeue(out var entry))
        {
            entries.Add(entry);
        }

        if (entries.Count > 0)
        {
            try
            {
                _logsRepo.WriteBatch(entries);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SqliteLoggerProvider] Failed to flush {entries.Count} log entries: {ex.Message}");
            }
        }
    }

    private void Prune()
    {
        try
        {
            _logsRepo.Prune(debugDays: 7, infoDays: 30, errorDays: 90);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SqliteLoggerProvider] Failed to prune logs: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer.Dispose();
        _pruneTimer.Dispose();

        // Final flush
        Flush();
    }
}

/// <summary>
/// Individual logger instance that writes to the SqliteLoggerProvider buffer.
/// </summary>
internal sealed class SqliteLogger(string category, SqliteLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var level = logLevel switch
        {
            LogLevel.Trace => "Trace",
            LogLevel.Debug => "Debug",
            LogLevel.Information => "Information",
            LogLevel.Warning => "Warning",
            LogLevel.Error => "Error",
            LogLevel.Critical => "Critical",
            _ => "None"
        };

        provider.Enqueue(level, category, message, exception?.ToString(), null);
    }
}
