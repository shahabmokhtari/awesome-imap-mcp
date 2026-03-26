using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UltimateImapMcp.Core.Coordination;
using UltimateImapMcp.Core.Repositories;

namespace UltimateImapMcp.Dashboard;

public static class LogsApi
{
    public static IEndpointRouteBuilder MapLogsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/logs", (HttpContext ctx, LogsRepository logsRepo, IInstanceCoordinator coordinator) =>
        {
            var level = ctx.Request.Query["level"].FirstOrDefault();
            var category = ctx.Request.Query["category"].FirstOrDefault();
            var from = ctx.Request.Query["from"].FirstOrDefault();
            var to = ctx.Request.Query["to"].FirstOrDefault();
            var search = ctx.Request.Query["search"].FirstOrDefault();
            var scope = ctx.Request.Query["scope"].FirstOrDefault();
            var instanceId = ctx.Request.Query["instance_id"].FirstOrDefault();

            var page = int.TryParse(ctx.Request.Query["page"].FirstOrDefault(), out var p) && p >= 1 ? p : 1;
            var pageSize = int.TryParse(ctx.Request.Query["page_size"].FirstOrDefault(), out var ps) && ps >= 1 ? Math.Min(ps, 500) : 100;
            var offset = (page - 1) * pageSize;

            var liveOnly = ctx.Request.Query["live_only"].FirstOrDefault() == "true";
            IReadOnlyList<string>? liveInstanceIds = null;
            if (liveOnly && instanceId is null)
            {
                liveInstanceIds = coordinator.GetLiveInstances().Select(i => i.InstanceId).ToList();
            }

            var totalCount = logsRepo.QueryCount(level, category, from, to, search, scope, instanceId, liveInstanceIds);
            var results = logsRepo.Query(level, category, from, to, search, pageSize, scope, instanceId, offset, liveInstanceIds);

            return Results.Ok(new
            {
                count = results.Count,
                total_count = totalCount,
                page,
                page_size = pageSize,
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
