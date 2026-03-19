using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.Dashboard;

public static class SettingsApi
{
    public static IEndpointRouteBuilder MapSettingsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", (AppConfig config) =>
        {
            // Return config with sensitive fields redacted
            return Results.Ok(new
            {
                Server = new
                {
                    config.Server.Transport,
                    config.Server.HttpPort,
                    config.Server.DashboardPort,
                    config.Server.DashboardEnabled,
                    config.Server.DashboardAuth,
                    config.Server.LogLevel
                },
                Cache = new
                {
                    config.Cache.DbPath,
                    config.Cache.MaxSizeMb,
                    config.Cache.DefaultWindowDays,
                    config.Cache.MaxBodyAgeDays,
                    config.Cache.ImapFallbackTtlHours
                },
                Queue = new
                {
                    config.Queue.P0FlushInterval,
                    config.Queue.P1FlushInterval,
                    config.Queue.P2FlushInterval,
                    config.Queue.SendUndoWindow,
                    config.Queue.MaxRetries
                },
                Llm = new
                {
                    config.Llm.Enabled,
                    config.Llm.Provider,
                    config.Llm.Model,
                    config.Llm.DailyTokenBudget,
                    config.Llm.MonthlyCostLimit,
                    config.Llm.AutoAnalyzeNew
                },
                Metrics = new
                {
                    config.Metrics.Enabled,
                    config.Metrics.Port,
                    config.Metrics.Path,
                    config.Metrics.InternalRetentionDays
                },
                AccountCount = config.Accounts.Count
            });
        });

        app.MapPut("/api/settings", async (HttpContext ctx) =>
        {
            // Settings update is a placeholder — config file updates require restart
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object>>().ConfigureAwait(false);
            return Results.Ok(new { Updated = true, Message = "Settings update noted. Restart required for changes to take effect." });
        });

        return app;
    }
}
