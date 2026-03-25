using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Llm;

namespace UltimateImapMcp.Dashboard;

public static class LlmApi
{
    private static readonly Dictionary<string, string[]> ModelsByProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = ["gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "gpt-3.5-turbo", "o1", "o1-mini", "o3-mini"],
        ["anthropic"] = ["claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"],
        ["acp_claude"] = [],
        ["acp_copilot"] = [],
        ["in_context"] = [],
    };

    public static IEndpointRouteBuilder MapLlmApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/llm/models", (HttpContext ctx) =>
        {
            var provider = ctx.Request.Query["provider"].FirstOrDefault() ?? "";
            if (ModelsByProvider.TryGetValue(provider, out var models))
                return Results.Ok(models);
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
                (client as IDisposable)?.Dispose();
            }
        });

        return app;
    }
}

file record LlmTestRequest
{
    public string? Prompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
}
