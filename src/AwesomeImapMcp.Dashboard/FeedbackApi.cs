using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace AwesomeImapMcp.Dashboard;

public static class FeedbackApi
{
    public static IEndpointRouteBuilder MapFeedbackApi(this IEndpointRouteBuilder app)
    {
        // Relays a note from the feedback sheet to issues.awesomehq.app.
        // 200 = sent; 503 = keep the draft and retry with the same clientId; 502 = refused by the relay.
        app.MapPost("/api/feedback", async (HttpContext ctx, IHttpClientFactory httpFactory,
            ILoggerFactory loggerFactory) =>
        {
            FeedbackRequest? request;
            try
            {
                request = await ctx.Request.ReadFromJsonAsync<FeedbackRequest>(ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(new { error = "Request body must be JSON." });
            }
            if (request is null)
                return Results.BadRequest(new { error = "Request body is required." });

            var invalid = FeedbackRelay.Validate(request);
            if (invalid is not null)
                return Results.BadRequest(new { error = invalid });

            var relay = new FeedbackRelay(
                httpFactory.CreateClient(FeedbackRelay.HttpClientName),
                loggerFactory.CreateLogger("Feedback"),
                FeedbackRelay.ShouldSend(FeedbackRelay.IsDebugBuild,
                    Environment.GetEnvironmentVariable(FeedbackRelay.SendInDebugVariable)),
                FeedbackRelay.AppVersion(Assembly.GetEntryAssembly()),
                FeedbackRelay.CurrentOs());

            var result = await relay.SendAsync(request, ctx.RequestAborted).ConfigureAwait(false);
            return result.Outcome switch
            {
                FeedbackOutcome.Sent => Results.Ok(new { @ref = result.Ref, status = result.Status }),
                FeedbackOutcome.Retry => Results.Json(
                    new { error = "Couldn't send — check your connection and try again.", retry = true },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Json(
                    new { error = "Couldn't send your feedback.", retry = false },
                    statusCode: StatusCodes.Status502BadGateway),
            };
        });

        return app;
    }
}
