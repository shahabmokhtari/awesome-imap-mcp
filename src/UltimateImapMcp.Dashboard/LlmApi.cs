using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Llm;
using UltimateImapMcp.Llm.Acp;

namespace UltimateImapMcp.Dashboard;

public static class LlmApi
{
    private static readonly Dictionary<string, string[]> StaticModelsByProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = ["gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "o3-mini", "o4-mini"],
        ["anthropic"] = ["claude-sonnet-4-5", "claude-haiku-4-5", "claude-3-5-sonnet", "claude-3-5-haiku"],
        ["in_context"] = [],
    };

    private static Dictionary<string, string[]>? _acpModelsCache;
    private static readonly SemaphoreSlim AcpCacheSemaphore = new(1, 1);

    public static IEndpointRouteBuilder MapLlmApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/llm/models", async (HttpContext ctx) =>
        {
            var provider = ctx.Request.Query["provider"].FirstOrDefault() ?? "";

            if (StaticModelsByProvider.TryGetValue(provider, out var models))
                return Results.Ok(models);

            // For ACP providers, try to detect available models from CLI
            if (provider.StartsWith("acp_", StringComparison.OrdinalIgnoreCase))
            {
                var acpModels = await GetAcpModelsAsync(provider).ConfigureAwait(false);
                return Results.Ok(acpModels);
            }

            return Results.Ok(Array.Empty<string>());
        });

        app.MapPost("/api/llm/test", async (HttpContext ctx, LlmConfig llmConfig) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<LlmTestRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
                return Results.BadRequest(new { error = "prompt is required" });

            var effectiveProvider = body.Provider ?? llmConfig.Provider;

            var effectiveModel = body.Model ?? llmConfig.Model;

            // in_context provider has no external process — cannot be tested
            if (effectiveProvider.Equals("in_context", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(new
                {
                    response = "in_context provider analyzes emails using the MCP host's own context. No external test available.",
                    model = effectiveModel,
                    duration_ms = 0L,
                });
            }

            // ACP providers — spawn a temporary agent, send prompt, collect response
            if (effectiveProvider.StartsWith("acp_", StringComparison.OrdinalIgnoreCase))
            {
                return await TestAcpProviderAsync(effectiveProvider, effectiveModel, body.Prompt!, llmConfig, ctx.RequestServices).ConfigureAwait(false);
            }

            // API-based providers (openai, anthropic) — use ChatClient
            var resolvedKey = !string.IsNullOrEmpty(body.ApiKey)
                ? body.ApiKey
                : llmConfig.ResolveApiKey(effectiveProvider);

            var effectiveConfig = new LlmConfig
            {
                Enabled = true,
                Provider = effectiveProvider,
                Model = effectiveModel,
                ApiKey = resolvedKey,
            };

            IChatClient? client = null;
            try
            {
                client = ChatClientFactory.Create(effectiveConfig);
                var sw = Stopwatch.StartNew();
                var result = await client.GetResponseAsync(body.Prompt).ConfigureAwait(false);
                sw.Stop();

                return Results.Ok(new
                {
                    response = result.Text ?? "",
                    model = effectiveConfig.Model,
                    duration_ms = sw.ElapsedMilliseconds,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Json(new
                {
                    error = ex.Message,
                    model = effectiveConfig.Model,
                }, statusCode: 502);
            }
            finally
            {
                if (client is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    (client as IDisposable)?.Dispose();
            }
        });

        return app;
    }

    private static async Task<IResult> TestAcpProviderAsync(
        string provider, string model, string prompt,
        LlmConfig llmConfig, IServiceProvider services)
    {
        var pool = services.GetService<IAcpClientPool>();
        if (pool is null)
            return Results.Json(new
            {
                error = "ACP pool not initialized. Enable an ACP provider in config and restart.",
            }, statusCode: 500);

        var result = await pool.SendPromptAsync(prompt, model).ConfigureAwait(false);

        if (result.Error is not null)
            return Results.Json(new { error = result.Error, model }, statusCode: 502);

        return Results.Ok(new
        {
            response = result.Response,
            model,
            duration_ms = result.PromptLatencyMs,
        });
    }

    /// <summary>
    /// Gets available models for an ACP provider by parsing CLI help output.
    /// Results are cached after first call. Uses async semaphore to avoid blocking threads.
    /// </summary>
    private static async Task<string[]> GetAcpModelsAsync(string provider)
    {
        await AcpCacheSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            _acpModelsCache ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            if (_acpModelsCache.TryGetValue(provider, out var cached))
                return cached;

            var models = provider.ToLowerInvariant() switch
            {
                "acp_copilot" => await DetectModelsFromCliAsync("gh", ["copilot", "--", "--help"]).ConfigureAwait(false),
                "acp_claude" => ["sonnet", "opus", "haiku"],
                _ => []
            };

            _acpModelsCache[provider] = models;
            return models;
        }
        finally
        {
            AcpCacheSemaphore.Release();
        }
    }

    /// <summary>
    /// Runs a CLI command and parses --model choices from its help output.
    /// Reads stdout and stderr concurrently to avoid pipe-buffer deadlocks.
    /// Times out after 5 seconds to avoid hanging on unresponsive CLIs.
    /// </summary>
    private static async Task<string[]> DetectModelsFromCliAsync(string command, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return [];

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Read both streams concurrently to avoid deadlock from pipe buffer filling
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var combinedOutput = stdoutTask.Result + stderrTask.Result;

            // Parse model choices from help text. Supports two formats:
            //   choices: "model1", "model2", ...  (quoted list, typically inside CLI help parens)
            //   {model1,model2,...}                (brace-delimited bare list)
            var match = Regex.Match(combinedOutput, @"--model.*?choices:\s*(""[^)]+)", RegexOptions.Singleline);
            if (match.Success)
            {
                var choicesText = match.Groups[1].Value;
                var models = Regex.Matches(choicesText, @"""([^""]+)""")
                    .Select(m => m.Groups[1].Value)
                    .ToArray();
                if (models.Length > 0) return models;
            }

            // Fallback: brace-delimited comma-separated list, e.g. --model {model1,model2}
            var braceMatch = Regex.Match(combinedOutput, @"--model\s+\{([^}]+)\}", RegexOptions.Singleline);
            if (braceMatch.Success)
            {
                var models = braceMatch.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (models.Length > 0) return models;
            }

            return [];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or IOException or OperationCanceledException)
        {
            // CLI not found, timed out, or failed to start — return empty list
            return [];
        }
    }
}

file record LlmTestRequest
{
    public string? Prompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
}
