using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;

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
            return Results.Ok(new { hasPinSet });
        });

        app.MapPost("/api/auth/setup", async (HttpContext ctx, DashboardAuthRepository authRepo, AppConfig config,
            ILogger<DashboardAuthRepository> logger) =>
        {
            if (authRepo.HasPinSet())
                return Results.BadRequest(new { error = "PIN already set. Use /api/auth/login." });

            var body = await ctx.Request.ReadFromJsonAsync<PinRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.Pin))
                return Results.BadRequest(new { error = "PIN is required" });

            if (body.Pin.Length < 4 || body.Pin.Length > 6 || !body.Pin.All(char.IsDigit))
                return Results.BadRequest(new { error = "PIN must be 4-6 digits" });

            var hash = BCrypt.Net.BCrypt.HashPassword(body.Pin);
            authRepo.UpsertPin(hash);

            // Enable PIN auth in config and persist
            config.Server.DashboardAuth = "pin";
            if (config.SourcePath is not null)
            {
                try { ConfigLoader.SaveToFile(config, config.SourcePath); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to persist config after PIN setup"); }
            }

            var token = authRepo.CreateSession(TimeSpan.FromMinutes(30));
            return Results.Ok(new { token, message = "PIN set successfully" });
        });

        app.MapPost("/api/auth/login", async (HttpContext ctx, DashboardAuthRepository authRepo) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<PinRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.Pin))
                return Results.BadRequest(new { error = "PIN is required" });

            var auth = authRepo.GetPinAuth();
            if (auth is null)
                return Results.BadRequest(new { error = "No PIN set. Use /api/auth/setup first." });

            if (!BCrypt.Net.BCrypt.Verify(body.Pin, auth.Hash))
                return Results.Json(new { error = "Invalid PIN" }, statusCode: 401);

            var token = authRepo.CreateSession(TimeSpan.FromMinutes(30));
            return Results.Ok(new { token });
        });

        app.MapPost("/api/auth/change-pin", async (HttpContext ctx, DashboardAuthRepository authRepo) =>
        {
            // Validate session token — this endpoint is under /api/auth/ which bypasses PIN middleware
            var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
            if (authRepo.HasPinSet())
            {
                if (authHeader is null || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    return Results.Json(new { error = "Authentication required" }, statusCode: 401);
                var sessionToken = authHeader["Bearer ".Length..].Trim();
                if (!authRepo.ValidateSession(sessionToken))
                    return Results.Json(new { error = "Invalid or expired session" }, statusCode: 401);
            }

            var body = await ctx.Request.ReadFromJsonAsync<ChangePinRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.NewPin))
                return Results.BadRequest(new { error = "new_pin is required" });

            if (body.NewPin.Length < 4 || body.NewPin.Length > 6 || !body.NewPin.All(char.IsDigit))
                return Results.BadRequest(new { error = "PIN must be 4-6 digits" });

            // If PIN already set, require old pin
            if (authRepo.HasPinSet())
            {
                if (string.IsNullOrWhiteSpace(body.OldPin))
                    return Results.BadRequest(new { error = "old_pin is required when changing an existing PIN" });

                var auth = authRepo.GetPinAuth();
                if (auth is null || !BCrypt.Net.BCrypt.Verify(body.OldPin, auth.Hash))
                    return Results.Json(new { error = "Invalid current PIN" }, statusCode: 401);
            }

            var hash = BCrypt.Net.BCrypt.HashPassword(body.NewPin);
            authRepo.UpsertPin(hash);

            var token = authRepo.CreateSession(TimeSpan.FromMinutes(30));
            return Results.Ok(new { token, message = "PIN updated successfully" });
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx, DashboardAuthRepository authRepo) =>
        {
            var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
            if (authHeader is not null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader["Bearer ".Length..].Trim();
                authRepo.DeleteSession(token);
            }
            return Results.Ok(new { message = "Logged out" });
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

            // Skip auth when dashboard_auth is not "pin" AND no PIN has been set in DB
            var config = ctx.RequestServices.GetRequiredService<AppConfig>();
            var authRepo = ctx.RequestServices.GetRequiredService<DashboardAuthRepository>();
            var pinIsConfigured = string.Equals(config.Server.DashboardAuth, "pin", StringComparison.OrdinalIgnoreCase);
            var pinExistsInDb = authRepo.HasPinSet();

            if (!pinIsConfigured && !pinExistsInDb)
            {
                await next().ConfigureAwait(false);
                return;
            }

            // If PIN exists in DB but config doesn't say "pin", auto-fix the config
            if (pinExistsInDb && !pinIsConfigured)
            {
                config.Server.DashboardAuth = "pin";
            }

            // Check for Bearer token
            var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
            if (authHeader is null || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    JsonSerializer.Serialize(new { error = "Authentication required" })).ConfigureAwait(false);
                return;
            }

            var token = authHeader["Bearer ".Length..].Trim();
            if (!authRepo.ValidateSession(token))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    JsonSerializer.Serialize(new { error = "Invalid or expired session" })).ConfigureAwait(false);
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

public record ChangePinRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("old_pin")]
    public string? OldPin { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("new_pin")]
    public string? NewPin { get; init; }
}
