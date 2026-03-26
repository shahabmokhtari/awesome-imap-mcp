using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.Dashboard;

public static class CacheApi
{
    public static IEndpointRouteBuilder MapCacheApi(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/api/cache", (MessageRepository messageRepo) =>
        {
            var deleted = messageRepo.DeleteAll();
            return Results.Ok(new { deleted, message = "All cached messages cleared." });
        });

        app.MapDelete("/api/cache/{accountId}", (string accountId, MessageRepository messageRepo) =>
        {
            var deleted = messageRepo.DeleteByAccount(accountId);
            return Results.Ok(new { deleted, accountId, message = $"Cache cleared for account." });
        });

        app.MapDelete("/api/cache/{accountId}/{folderId:int}", (string accountId, int folderId, MessageRepository messageRepo) =>
        {
            var deleted = messageRepo.DeleteByFolder(accountId, folderId);
            return Results.Ok(new { deleted, accountId, folderId, message = $"Cache cleared for folder." });
        });

        return app;
    }
}
