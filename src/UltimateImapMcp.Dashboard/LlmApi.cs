using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Llm;

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

            var effectiveConfig = new LlmConfig
            {
                Enabled = true,
                Provider = body.Provider ?? llmConfig.Provider,
                Model = body.Model ?? llmConfig.Model,
                ApiKey = llmConfig.ApiKey,
                ApiKeyEnv = llmConfig.ApiKeyEnv,
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
            catch (Exception ex)
            {
                return Results.Ok(new
                {
                    response = (string?)null,
                    model = effectiveConfig.Model,
                    duration_ms = 0L,
                    error = ex.Message,
                });
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
                "acp_claude" => await DetectModelsFromCliAsync("claude", ["--help"]).ConfigureAwait(false),
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

            // Read both streams concurrently to avoid deadlock from pipe buffer filling
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            var combinedOutput = stdoutTask.Result + stderrTask.Result;

            // Parse model choices from help text: --model <model> ... (choices: "model1", "model2", ...)
            var match = Regex.Match(combinedOutput, @"--model.*?choices:\s*(""[^)]+)", RegexOptions.Singleline);
            if (!match.Success) return [];

            var choicesText = match.Groups[1].Value;
            var models = Regex.Matches(choicesText, @"""([^""]+)""")
                .Select(m => m.Groups[1].Value)
                .ToArray();

            return models;
        }
        catch
        {
            return [];
        }
    }
}

file record LlmTestRequest
{
    public string? Prompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
}
