using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UltimateImapMcp.Core.Repositories;

namespace UltimateImapMcp.Dashboard;

public static class MetricsApi
{
    public static IEndpointRouteBuilder MapMetricsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/metrics", (HttpContext ctx, MetricsRepository metricsRepo) =>
        {
            var name = ctx.Request.Query["name"].FirstOrDefault();
            var from = ctx.Request.Query["from"].FirstOrDefault();
            var to = ctx.Request.Query["to"].FirstOrDefault();
            var limitStr = ctx.Request.Query["limit"].FirstOrDefault();
            var limit = int.TryParse(limitStr, out var l) ? l : 100;

            if (name is not null)
            {
                var results = metricsRepo.Query(name, from, to, limit);
                return Results.Ok(new
                {
                    count = results.Count,
                    metrics = results.Select(m => new
                    {
                        id = m.Id,
                        name = m.Name,
                        value = m.Value,
                        tags = m.Tags,
                        recorded_at = m.RecordedAt
                    }).ToList()
                });
            }
            else
            {
                var results = metricsRepo.QueryAll(from, to, limit);
                return Results.Ok(new
                {
                    count = results.Count,
                    metrics = results.Select(m => new
                    {
                        id = m.Id,
                        name = m.Name,
                        value = m.Value,
                        tags = m.Tags,
                        recorded_at = m.RecordedAt
                    }).ToList()
                });
            }
        });

        return app;
    }
}
