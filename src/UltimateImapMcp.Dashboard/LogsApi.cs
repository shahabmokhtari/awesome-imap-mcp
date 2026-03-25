using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UltimateImapMcp.Core.Repositories;

namespace UltimateImapMcp.Dashboard;

public static class LogsApi
{
    public static IEndpointRouteBuilder MapLogsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/logs", (HttpContext ctx, LogsRepository logsRepo) =>
        {
            var level = ctx.Request.Query["level"].FirstOrDefault();
            var category = ctx.Request.Query["category"].FirstOrDefault();
            var from = ctx.Request.Query["from"].FirstOrDefault();
            var to = ctx.Request.Query["to"].FirstOrDefault();
            var search = ctx.Request.Query["search"].FirstOrDefault();
            var scope = ctx.Request.Query["scope"].FirstOrDefault();
            var instanceId = ctx.Request.Query["instance_id"].FirstOrDefault();
            var limitStr = ctx.Request.Query["limit"].FirstOrDefault();
            var limit = int.TryParse(limitStr, out var l) ? l : 100;

            var results = logsRepo.Query(level, category, from, to, search, limit, scope, instanceId);

            return Results.Ok(new
            {
                count = results.Count,
                logs = results.Select(log => new
                {
                    id = log.Id,
                    level = log.Level,
                    category = log.Category,
                    message = log.Message,
                    exception = log.Exception,
                    metadata = log.Metadata,
                    created_at = log.CreatedAt,
                    scope = log.Scope,
                    instance_id = log.InstanceId
                }).ToList()
            });
        });

        app.MapGet("/api/logs/instances", (LogsRepository logsRepo) =>
        {
            var instances = logsRepo.GetDistinctInstanceIds();
            return Results.Ok(instances);
        });

        return app;
    }
}
