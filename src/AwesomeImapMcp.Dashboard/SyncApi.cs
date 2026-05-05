using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using AwesomeImapMcp.ImapClient;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.Dashboard;

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

        app.MapPost("/api/sync/trigger-all", (SyncManager syncManager) =>
        {
            // Stop current sync, then start fresh — polling loop will pick up all accounts
            syncManager.StopSync();
            syncManager.StartSync();
            return Results.Ok(new { message = "Sync restarted for all accounts." });
        });

        app.MapGet("/api/sync/logs", (HttpContext ctx, SyncLogRepository syncLogRepo) =>
        {
            var accountId = ctx.Request.Query["account_id"].FirstOrDefault();
            var limitStr = ctx.Request.Query["limit"].FirstOrDefault();
            var limit = 50;
            if (!string.IsNullOrEmpty(limitStr) && int.TryParse(limitStr, out var parsed))
                limit = Math.Clamp(parsed, 1, 200);

            var logs = syncLogRepo.GetRecent(accountId, limit, true);
            return Results.Ok(logs.Select(l => new
            {
                id = l.Id,
                accountId = l.AccountId,
                folderId = l.FolderId,
                syncType = l.SyncType,
                status = l.Status,
                messagesSynced = l.MessagesSynced,
                errorMessage = l.ErrorMessage,
                startedAt = l.StartedAt,
                completedAt = l.CompletedAt,
                durationMs = l.DurationMs,
            }));
        });

        app.MapPost("/api/sync/stop", (SyncManager syncManager) =>
        {
            syncManager.StopSync();
            return Results.Ok(new { stopped = true, message = "Sync stopped. Use /api/sync/start to resume." });
        });

        app.MapPost("/api/sync/start", (SyncManager syncManager) =>
        {
            syncManager.StartSync();
            return Results.Ok(new { stopped = false, message = "Sync started." });
        });

        app.MapGet("/api/sync/state", (SyncManager syncManager) =>
        {
            return Results.Ok(new { stopped = syncManager.IsPaused, syncing = syncManager.IsSyncing });
        });

        // Trigger sync for an account or folder: stops current sync, runs manual sync, resumes periodic
        app.MapPost("/api/sync/trigger", async (HttpContext ctx, SyncManager syncManager) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<SyncTriggerRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrEmpty(body.AccountId))
                return Results.BadRequest(new { error = "accountId is required" });

            try
            {
                // Stop ongoing sync to avoid conflicts
                syncManager.StopSync();

                // Start fresh CTS for this manual operation
                syncManager.StartSync();

                // Sync the specific folder or all folders for the account
                await syncManager.TriggerSyncAsync(body.AccountId, body.FolderPath).ConfigureAwait(false);

                // Periodic sync is already resumed by StartSync above
                return Results.Ok(new
                {
                    triggered = true,
                    accountId = body.AccountId,
                    folderPath = body.FolderPath ?? "(all folders)"
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                syncManager.StartSync(); // ensure sync resumes on error
                return Results.Json(new { error = ex.Message }, statusCode: 500);
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
