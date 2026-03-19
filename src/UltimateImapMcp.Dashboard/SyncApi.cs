using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.ImapClient;

namespace UltimateImapMcp.Dashboard;

public static class SyncApi
{
    public static IEndpointRouteBuilder MapSyncApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sync/status", (SyncManager syncManager, AppConfig config) =>
        {
            var result = new Dictionary<string, object>();
            foreach (var account in config.Accounts)
            {
                var accountId = account.Name.ToLowerInvariant().Replace(' ', '-');
                var status = syncManager.GetSyncStatus(accountId);
                result[accountId] = status;
            }
            return Results.Ok(result);
        });

        app.MapPost("/api/sync/trigger", async (HttpContext ctx, SyncManager syncManager) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<SyncTriggerRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrEmpty(body.AccountId))
                return Results.BadRequest("accountId is required");

            try
            {
                await syncManager.TriggerSyncAsync(body.AccountId, body.FolderPath).ConfigureAwait(false);
                return Results.Ok(new { Triggered = true, body.AccountId, body.FolderPath });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        return app;
    }
}

public record SyncTriggerRequest
{
    public string AccountId { get; init; } = "";
    public string? FolderPath { get; init; }
}
