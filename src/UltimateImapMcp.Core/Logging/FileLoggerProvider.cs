using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace UltimateImapMcp.Core.Logging;

/// <summary>
/// ILoggerProvider that writes log entries to files organized by scope.
/// Each scope gets a subdirectory, and each instance gets its own log file.
/// Directory structure:
///   {logDir}/mail/{instance_id}.log
///   {logDir}/accounts/{instance_id}.log
///   {logDir}/api/{instance_id}.log
///   etc.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider, IDisposable
{
    private readonly string _logDir;
    private readonly string _instanceId;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly ConcurrentDictionary<string, StreamWriter> _writers = new();
    private readonly ConcurrentQueue<(string Scope, string Line)> _buffer = new();
    private readonly Timer _flushTimer;
    private readonly object _writerLock = new();
    private bool _disposed;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    public FileLoggerProvider(string logDir, string instanceId)
    {
        _logDir = logDir;
        _instanceId = instanceId;
        _flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this, _instanceId));
    }

    internal void Enqueue(string scope, string line)
    {
        if (_disposed) return;
        _buffer.Enqueue((scope, line));
    }

    private void Flush()
    {
        if (_buffer.IsEmpty) return;

        var entries = new List<(string Scope, string Line)>();
        while (_buffer.TryDequeue(out var entry))
        {
            entries.Add(entry);
        }

        // Group by scope to minimize file operations
        var grouped = entries.GroupBy(e => e.Scope);
        foreach (var group in grouped)
        {
            try
            {
                var writer = GetOrCreateWriter(group.Key);
                foreach (var (_, line) in group)
                {
                    writer.WriteLine(line);
                }
                writer.Flush();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FileLoggerProvider] Failed to write logs for scope '{group.Key}': {ex.Message}");
            }
        }
    }

    private StreamWriter GetOrCreateWriter(string scope)
    {
        if (_writers.TryGetValue(scope, out var existing))
            return existing;

        lock (_writerLock)
        {
            if (_writers.TryGetValue(scope, out existing))
                return existing;

            var dir = Path.Combine(_logDir, scope);
            Directory.CreateDirectory(dir);

            var filePath = Path.Combine(dir, $"{_instanceId}.log");
            var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            var writer = new StreamWriter(stream) { AutoFlush = false };
            _writers[scope] = writer;
            return writer;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer.Dispose();

        // Final flush
        Flush();

        // Close all writers
        foreach (var (_, writer) in _writers)
        {
            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FileLoggerProvider] Failed to close writer: {ex.Message}");
            }
        }
        _writers.Clear();
    }
}

/// <summary>
/// Individual logger instance for file-based logging.
/// </summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerProvider _provider;
    private readonly string _scope;
    private readonly string _instanceId;

    public FileLogger(string category, FileLoggerProvider provider, string instanceId)
    {
        _category = category;
        _provider = provider;
        _scope = LogScopeMapper.MapCategoryToScope(category);
        _instanceId = instanceId;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var level = logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };

        var line = $"[{timestamp}] [{level}] [{_category}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        _provider.Enqueue(_scope, line);
    }
}
