using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.Dashboard;

public static class SyncApi
{
    public static IEndpointRouteBuilder MapSyncApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sync/status", (SyncManager syncManager, AccountRepository accountRepo) =>
        {
            var result = new Dictionary<string, object>();
            foreach (var account in accountRepo.GetAll())
            {
                var status = syncManager.GetSyncStatus(account.Id);
                var label = $"{account.Name} ({account.Provider})";
                result[label] = new { accountId = account.Id, folders = status };
            }
            return Results.Ok(result);
        });

        app.MapPost("/api/sync/trigger-all", async (SyncManager syncManager, AccountRepository accountRepo) =>
        {
            var accounts = accountRepo.GetAll();
            if (accounts.Count == 0)
                return Results.Ok(new { triggered = 0, message = "No accounts found." });

            var triggered = 0;
            var errors = new List<string>();
            foreach (var account in accounts)
            {
                try
                {
                    await syncManager.TriggerSyncAsync(account.Id).ConfigureAwait(false);
                    triggered++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{account.Name}: {ex.Message}");
                }
            }

            return Results.Ok(new { triggered, total = accounts.Count, errors });
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
            catch (Exception ex)
            {
                return Results.Json(new { Error = ex.Message }, statusCode: 500);
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
