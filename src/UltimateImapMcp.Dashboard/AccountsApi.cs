using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.Dashboard;

public static class AccountsApi
{
    public static IEndpointRouteBuilder MapAccountsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/accounts", (AccountRepository repo) =>
        {
            var accounts = repo.GetAll();
            // Strip credentials from response
            return Results.Ok(accounts.Select(a => new
            {
                a.Id,
                a.Name,
                a.ImapHost,
                a.ImapPort,
                a.SmtpHost,
                a.SmtpPort,
                a.SmtpUseSsl,
                a.Username,
                a.AuthType,
                a.Provider,
                a.CreatedAt,
                a.UpdatedAt
            }));
        });

        app.MapPost("/api/accounts", async (HttpContext ctx, AccountRepository repo,
            CredentialEncryptor encryptor) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<CreateAccountRequest>().ConfigureAwait(false);
            if (body is null)
                return Results.BadRequest("Invalid request body");

            var id = Guid.NewGuid().ToString();
            var credentialsEnc = encryptor.Encrypt(body.Password ?? "");

            repo.Insert(id, body.Name, body.ImapHost, body.ImapPort,
                body.SmtpHost, body.SmtpPort, body.SmtpUseSsl,
                body.Username, body.AuthType, credentialsEnc, body.Provider, null);

            return Results.Created($"/api/accounts/{id}", new { Id = id });
        });

        app.MapPut("/api/accounts/{id}", async (string id, HttpContext ctx,
            AccountRepository repo) =>
        {
            var existing = repo.GetById(id);
            if (existing is null)
                return Results.NotFound();

            var body = await ctx.Request.ReadFromJsonAsync<UpdateAccountRequest>().ConfigureAwait(false);
            if (body is null)
                return Results.BadRequest("Invalid request body");

            repo.Update(id, body.Name, body.ImapHost, body.ImapPort,
                body.SmtpHost, body.SmtpPort, body.SmtpUseSsl, body.Username);

            return Results.Ok(new { Id = id, Updated = true });
        });

        app.MapDelete("/api/accounts/{id}", (string id, AccountRepository repo) =>
        {
            var existing = repo.GetById(id);
            if (existing is null)
                return Results.NotFound();

            repo.Delete(id);
            return Results.Ok(new { Id = id, Deleted = true });
        });

        app.MapPost("/api/accounts/{id}/test", async (string id, AccountRepository repo,
            CredentialEncryptor encryptor, AppConfig config) =>
        {
            var account = repo.GetById(id);
            if (account is null)
                return Results.NotFound();

            try
            {
                // Basic connection test — attempt to create and connect an IMAP client
                using var client = new MailKit.Net.Imap.ImapClient();
                var password = encryptor.Decrypt(account.CredentialsEnc);
                await client.ConnectAsync(account.ImapHost, account.ImapPort,
                    MailKit.Security.SecureSocketOptions.SslOnConnect).ConfigureAwait(false);
                await client.AuthenticateAsync(account.Username, password).ConfigureAwait(false);
                await client.DisconnectAsync(true).ConfigureAwait(false);

                return Results.Ok(new { Success = true, Message = "Connection successful" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { Success = false, Message = ex.Message }, statusCode: 502);
            }
        });

        return app;
    }
}

public record CreateAccountRequest
{
    public string Name { get; init; } = "";
    public string ImapHost { get; init; } = "";
    public int ImapPort { get; init; } = 993;
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 587;
    public bool SmtpUseSsl { get; init; }
    public string Username { get; init; } = "";
    public string AuthType { get; init; } = "app_password";
    public string? Password { get; init; }
    public string Provider { get; init; } = "generic";
}

public record UpdateAccountRequest
{
    public string? Name { get; init; }
    public string? ImapHost { get; init; }
    public int? ImapPort { get; init; }
    public string? SmtpHost { get; init; }
    public int? SmtpPort { get; init; }
    public bool? SmtpUseSsl { get; init; }
    public string? Username { get; init; }
}
