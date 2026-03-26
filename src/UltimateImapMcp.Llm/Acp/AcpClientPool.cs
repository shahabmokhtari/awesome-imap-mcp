using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.Llm.Acp;

public sealed class AcpClientPool : IAcpClientPool
{
    private sealed record AcpWorkItem(string Prompt, string? Model, CancellationToken Ct)
    {
        public TaskCompletionSource<AcpPromptResult> Completion { get; } = new();
    }

    private readonly AcpConfig _config;
    private readonly ILogger<AcpClientPool> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Channel<AcpWorkItem> _queue;
    private readonly Task[] _workerTasks;
    private readonly CancellationTokenSource _shutdownCts = new();
    private volatile int _activeClients;
    private bool _disposed;

    public int ActiveClients => _activeClients;
    public int QueuedRequests => _queue.Reader.Count;

    public AcpClientPool(AcpConfig config, ILoggerFactory loggerFactory)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<AcpClientPool>();

        var poolSize = Math.Max(1, config.PoolSize);

        _queue = Channel.CreateUnbounded<AcpWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = poolSize == 1,
            SingleWriter = false,
        });

        _workerTasks = new Task[poolSize];
        for (var i = 0; i < poolSize; i++)
        {
            var workerIndex = i;
            _workerTasks[i] = Task.Run(() => WorkerLoopAsync(workerIndex, _shutdownCts.Token));
        }

        _logger.LogInformation("ACP client pool started with {PoolSize} workers (provider={Provider})",
            poolSize, config.Provider);
    }

    public async Task<AcpPromptResult> SendPromptAsync(string prompt, string? model = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var workItem = new AcpWorkItem(prompt, model, ct);

        await _queue.Writer.WriteAsync(workItem, ct).ConfigureAwait(false);
        _logger.LogDebug("Queued ACP prompt request (queue depth={Depth})", _queue.Reader.Count);

        return await workItem.Completion.Task.ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync(int workerIndex, CancellationToken shutdownToken)
    {
        var workerLogger = _loggerFactory.CreateLogger($"AcpWorker[{workerIndex}]");
        AcpClient? client = null;
        AcpSession? session = null;

        workerLogger.LogDebug("Worker {Index} started", workerIndex);

        try
        {
            await foreach (var workItem in _queue.Reader.ReadAllAsync(shutdownToken).ConfigureAwait(false))
            {
                // If the caller already cancelled, skip immediately
                if (workItem.Ct.IsCancellationRequested)
                {
                    workItem.Completion.TrySetCanceled(workItem.Ct);
                    continue;
                }

                var tags = new TagList { { "provider", _config.Provider } };
                Telemetry.AcpRequests.Add(1, tags);

                try
                {
                    (client, session) = await EnsureClientAsync(
                        workerIndex, client, session, workItem.Model, workerLogger, workItem.Ct)
                        .ConfigureAwait(false);

                    var result = await ExecutePromptAsync(
                        client, session, workItem.Prompt, workerLogger, workItem.Ct)
                        .ConfigureAwait(false);

                    workItem.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException) when (workItem.Ct.IsCancellationRequested)
                {
                    workItem.Completion.TrySetCanceled(workItem.Ct);
                }
                catch (Exception ex)
                {
                    Telemetry.AcpErrors.Add(1, tags);
                    workerLogger.LogWarning(ex, "Worker {Index} prompt failed, recycling client", workerIndex);

                    // Dispose the broken client so next request creates a fresh one
                    await DisposeClientSafelyAsync(client, workerLogger).ConfigureAwait(false);
                    client = null;
                    session = null;

                    workItem.Completion.TrySetResult(new AcpPromptResult
                    {
                        Response = string.Empty,
                        Error = ex.Message,
                    });
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (ChannelClosedException)
        {
            // Channel completed — normal shutdown path
        }
        catch (Exception ex)
        {
            workerLogger.LogError(ex, "Worker {Index} terminated unexpectedly", workerIndex);
        }
        finally
        {
            await DisposeClientSafelyAsync(client, workerLogger).ConfigureAwait(false);
            workerLogger.LogDebug("Worker {Index} stopped", workerIndex);
        }
    }

    private async Task<(AcpClient Client, AcpSession Session)> EnsureClientAsync(
        int workerIndex, AcpClient? client, AcpSession? session,
        string? model, ILogger workerLogger, CancellationToken ct)
    {
        if (client is not null && session is not null)
            return (client, session);

        var tags = new TagList { { "provider", _config.Provider } };
        var sw = Stopwatch.StartNew();

        var (command, args) = ResolveProviderCommand(model);
        var timeout = TimeSpan.FromSeconds(_config.RequestTimeoutSeconds);

        workerLogger.LogDebug("Worker {Index} creating new ACP client: {Command} {Args}",
            workerIndex, command, string.Join(" ", args));

        var newClient = new AcpClient(command, args, _loggerFactory.CreateLogger<AcpClient>(), timeout);

        try
        {
            await newClient.InitializeAsync(ct).ConfigureAwait(false);
            var workDir = Directory.GetCurrentDirectory();
            var newSession = await newClient.CreateSessionAsync(workDir, ct).ConfigureAwait(false);

            sw.Stop();
            Telemetry.AcpSessionLatency.Record(sw.ElapsedMilliseconds, tags);
            Interlocked.Increment(ref _activeClients);

            workerLogger.LogDebug("Worker {Index} ACP client ready (session={SessionId}, latency={Ms}ms)",
                workerIndex, newSession.SessionId, sw.ElapsedMilliseconds);

            return (newClient, newSession);
        }
        catch
        {
            await DisposeClientSafelyAsync(newClient, workerLogger).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AcpPromptResult> ExecutePromptAsync(
        AcpClient client, AcpSession session, string prompt,
        ILogger workerLogger, CancellationToken ct)
    {
        var tags = new TagList { { "provider", _config.Provider } };
        var sw = Stopwatch.StartNew();

        workerLogger.LogTrace("Sending prompt: {Prompt}", prompt);

        var responseBuilder = new StringBuilder();
        string? error = null;

        await foreach (var acpEvent in client.SendPromptAsync(session, prompt, ct).ConfigureAwait(false))
        {
            switch (acpEvent.Type)
            {
                case AcpEventType.TextDelta:
                    if (acpEvent.Text is not null)
                        responseBuilder.Append(acpEvent.Text);
                    break;

                case AcpEventType.Complete:
                    if (acpEvent.Text is not null)
                        responseBuilder.Append(acpEvent.Text);
                    break;

                case AcpEventType.Error:
                    error = acpEvent.Error;
                    Telemetry.AcpErrors.Add(1, tags);
                    break;

                case AcpEventType.ToolUse:
                case AcpEventType.PermissionRequest:
                    // Handled by AcpClient internally (permission auto-denied)
                    break;
            }
        }

        sw.Stop();
        Telemetry.AcpPromptLatency.Record(sw.ElapsedMilliseconds, tags);

        var response = responseBuilder.ToString();
        workerLogger.LogTrace("Received response ({Length} chars, {Ms}ms): {Response}",
            response.Length, sw.ElapsedMilliseconds, response);
        workerLogger.LogDebug("Prompt completed ({Length} chars, {Ms}ms)",
            response.Length, sw.ElapsedMilliseconds);

        return new AcpPromptResult
        {
            Response = response,
            Model = null,
            PromptLatencyMs = sw.ElapsedMilliseconds,
            Error = error,
        };
    }

    private (string Command, string[] Args) ResolveProviderCommand(string? model)
    {
        var provider = _config.Provider.ToLowerInvariant();
        string command;
        var argsList = new List<string>();

        if (provider == "copilot")
        {
            command = _config.Copilot.Command;
            argsList.AddRange(_config.Copilot.Args);
        }
        else
        {
            // Claude -- check legacy config first, then new config
            if (!string.IsNullOrEmpty(_config.Command) && _config.Command != "claude")
            {
                command = _config.Command;
                argsList.AddRange(_config.Args ?? []);
            }
            else
            {
                command = _config.Claude.Command;
                argsList.AddRange(_config.Claude.Args);
            }
        }

        if (!string.IsNullOrEmpty(model) && !argsList.Contains("--model"))
        {
            argsList.Add("--model");
            argsList.Add(model);
        }

        return (command, argsList.ToArray());
    }

    private async Task DisposeClientSafelyAsync(AcpClient? client, ILogger logger)
    {
        if (client is null)
            return;

        Interlocked.Decrement(ref _activeClients);

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error disposing ACP client");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _logger.LogDebug("Shutting down ACP client pool");

        // Signal workers to stop and complete the channel
        _queue.Writer.TryComplete();
        await _shutdownCts.CancelAsync().ConfigureAwait(false);

        // Wait for all workers to finish (with a generous timeout)
        try
        {
            await Task.WhenAll(_workerTasks).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("ACP pool workers did not shut down within 15 seconds");
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        _shutdownCts.Dispose();
        _logger.LogInformation("ACP client pool shut down");
    }
}
