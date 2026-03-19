using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace UltimateImapMcp.Dashboard;

/// <summary>
/// PIN-based authentication middleware for the dashboard.
/// On first access with no PIN set, requires PIN setup via /api/auth/setup.
/// Subsequent access requires PIN via /api/auth/login, returning a session token.
/// All /api/* routes (except auth) require a valid session token.
/// </summary>
public static class PinAuthMiddleware
{
    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/status", (DashboardAuthRepository authRepo) =>
        {
            var hasPinSet = authRepo.HasPinSet();
            return Results.Ok(new { HasPinSet = hasPinSet });
        });

        app.MapPost("/api/auth/setup", async (HttpContext ctx, DashboardAuthRepository authRepo) =>
        {
            if (authRepo.HasPinSet())
                return Results.BadRequest(new { Error = "PIN already set. Use /api/auth/login." });

            var body = await ctx.Request.ReadFromJsonAsync<PinRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.Pin))
                return Results.BadRequest(new { Error = "PIN is required" });

            var hash = BCrypt.Net.BCrypt.HashPassword(body.Pin);
            authRepo.UpsertPin(hash);

            var token = authRepo.CreateSession(TimeSpan.FromHours(24));
            return Results.Ok(new { Token = token, Message = "PIN set successfully" });
        });

        app.MapPost("/api/auth/login", async (HttpContext ctx, DashboardAuthRepository authRepo) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<PinRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.Pin))
                return Results.BadRequest(new { Error = "PIN is required" });

            var auth = authRepo.GetPinAuth();
            if (auth is null)
                return Results.BadRequest(new { Error = "No PIN set. Use /api/auth/setup first." });

            if (!BCrypt.Net.BCrypt.Verify(body.Pin, auth.Hash))
                return Results.Json(new { Error = "Invalid PIN" }, statusCode: 401);

            var token = authRepo.CreateSession(TimeSpan.FromHours(24));
            return Results.Ok(new { Token = token });
        });

        return app;
    }

    public static IApplicationBuilder UsePinAuth(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";

            // Allow auth endpoints, static files, and SignalR without auth
            if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var authRepo = ctx.RequestServices.GetRequiredService<DashboardAuthRepository>();

            // If no PIN set yet, block all API access except auth endpoints
            if (!authRepo.HasPinSet())
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    JsonSerializer.Serialize(new { Error = "Dashboard not configured. Set a PIN via /api/auth/setup first." })).ConfigureAwait(false);
                return;
            }

            // Check for Bearer token
            var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
            if (authHeader is null || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    JsonSerializer.Serialize(new { Error = "Authentication required" })).ConfigureAwait(false);
                return;
            }

            var token = authHeader["Bearer ".Length..].Trim();
            if (!authRepo.ValidateSession(token))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    JsonSerializer.Serialize(new { Error = "Invalid or expired session" })).ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
        });
    }
}

public record PinRequest
{
    public string Pin { get; init; } = "";
}
