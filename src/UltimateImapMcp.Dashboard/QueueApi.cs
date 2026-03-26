using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UltimateImapMcp.Queue;

namespace UltimateImapMcp.Dashboard;

public static class QueueApi
{
    public static IEndpointRouteBuilder MapQueueApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/queue", (QueueRepository repo, HttpContext ctx) =>
        {
            var status = ctx.Request.Query["status"].FirstOrDefault();
            var operations = repo.GetAll(status);
            return Results.Ok(operations);
        });

        app.MapPost("/api/queue/{id}/cancel", (string id, QueueManager queueManager) =>
        {
            var success = queueManager.Cancel(id);
            return success
                ? Results.Ok(new { id, cancelled = true })
                : Results.BadRequest(new { error = "Operation cannot be cancelled" });
        });

        app.MapPost("/api/queue/{id}/confirm", (string id, QueueManager queueManager) =>
        {
            var success = queueManager.Confirm(id);
            return success
                ? Results.Ok(new { id, confirmed = true })
                : Results.BadRequest(new { error = "Operation cannot be confirmed" });
        });

        return app;
    }
}
