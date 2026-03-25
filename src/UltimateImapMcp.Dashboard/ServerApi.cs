using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Repositories;

namespace UltimateImapMcp.Dashboard;

/// <summary>
/// Wraps the root host's <see cref="IHostApplicationLifetime"/> so the dashboard
/// web-app can request a full process shutdown without depending on its own lifetime.
/// </summary>
public sealed class RootLifetime(IHostApplicationLifetime lifetime)
{
    public void StopApplication() => lifetime.StopApplication();
}

public static class ServerApi
{
    public static IEndpointRouteBuilder MapServerApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/server/info", (InstanceInfo instanceInfo, AppConfig config) =>
        {
            using var process = Process.GetCurrentProcess();
            var startTime = process.StartTime.ToUniversalTime();
            var uptime = DateTime.UtcNow - startTime;

            return Results.Ok(new
            {
                instance_id = instanceInfo.Id,
                uptime_seconds = (int)uptime.TotalSeconds,
                process_id = process.Id,
                version = "0.1.0",
                started_at = startTime.ToString("o"),
                transport = config.Server.Transport,
                dashboard_port = config.Server.DashboardPort,
                http_port = config.Server.HttpPort,
            });
        });

        app.MapGet("/api/server/instances", (InstanceInfo instanceInfo, LogsRepository logsRepo) =>
        {
            var instances = logsRepo.GetDistinctInstanceIds();
            return Results.Ok(new
            {
                current = instanceInfo.Id,
                instances,
            });
        });

        app.MapPost("/api/server/shutdown", async (HttpContext ctx, RootLifetime rootLifetime,
            ILogger<RootLifetime> logger) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<ShutdownRequest>().ConfigureAwait(false);
            var delaySeconds = body?.DelaySeconds ?? 2;
            if (delaySeconds < 0) delaySeconds = 0;
            if (delaySeconds > 30) delaySeconds = 30;

            logger.LogWarning("Shutdown requested via dashboard API (delay: {Delay}s)", delaySeconds);

            // Schedule shutdown after delay so the HTTP response reaches the browser
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
                    rootLifetime.StopApplication();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during scheduled shutdown");
                }
            });

            return Results.Ok(new
            {
                message = "Shutting down...",
                shutting_down = true,
            });
        });

        return app;
    }
}

file record ShutdownRequest
{
    public int? DelaySeconds { get; init; }
}
