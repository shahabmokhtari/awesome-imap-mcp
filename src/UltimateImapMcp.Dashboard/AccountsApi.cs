using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
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
                id = a.Id,
                name = a.Name,
                imapHost = a.ImapHost,
                imapPort = a.ImapPort,
                smtpHost = a.SmtpHost,
                smtpPort = a.SmtpPort,
                smtpUseSsl = a.SmtpUseSsl,
                username = a.Username,
                authType = a.AuthType,
                provider = a.Provider,
                enabled = a.Enabled,
                createdAt = a.CreatedAt,
                updatedAt = a.UpdatedAt
            }));
        });

        app.MapPost("/api/accounts", async (HttpContext ctx, AccountRepository repo,
            CredentialEncryptor encryptor) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<CreateAccountRequest>().ConfigureAwait(false);
            if (body is null)
                return Results.BadRequest(new { error = "Invalid request body" });

            // Password is required unless auth_type is "oauth2"
            if (string.IsNullOrEmpty(body.Password) &&
                !body.AuthType.Equals("oauth2", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Password is required" });
            }

            var id = Guid.NewGuid().ToString();
            var credentialsEnc = encryptor.Encrypt(body.Password ?? "oauth2");

            repo.Insert(id, body.Name, body.ImapHost, body.ImapPort,
                body.SmtpHost, body.SmtpPort, body.SmtpUseSsl,
                body.Username, body.AuthType, credentialsEnc, body.Provider, null);

            return Results.Created($"/api/accounts/{id}", new { id });
        });

        app.MapPut("/api/accounts/{id}", async (string id, HttpContext ctx,
            AccountRepository repo) =>
        {
            var existing = repo.GetById(id);
            if (existing is null)
                return Results.NotFound();

            var body = await ctx.Request.ReadFromJsonAsync<UpdateAccountRequest>().ConfigureAwait(false);
            if (body is null)
                return Results.BadRequest(new { error = "Invalid request body" });

            repo.Update(id, body.Name, body.ImapHost, body.ImapPort,
                body.SmtpHost, body.SmtpPort, body.SmtpUseSsl, body.Username);

            return Results.Ok(new { id, updated = true });
        });

        app.MapPost("/api/accounts/{id}/toggle-enabled", async (string id, HttpContext ctx,
            AccountRepository repo) =>
        {
            var existing = repo.GetById(id);
            if (existing is null)
                return Results.NotFound();

            var body = await ctx.Request.ReadFromJsonAsync<ToggleEnabledRequest>().ConfigureAwait(false);
            var newEnabled = body?.Enabled ?? !existing.Enabled;

            repo.SetEnabled(id, newEnabled);
            return Results.Ok(new { id, enabled = newEnabled });
        });

        app.MapDelete("/api/accounts/{id}", (string id, AccountRepository repo,
            OAuthTokenRepository oauthTokenRepo) =>
        {
            var existing = repo.GetById(id);
            if (existing is null)
                return Results.NotFound();

            // Clean up OAuth tokens first
            oauthTokenRepo.Delete(id);
            repo.Delete(id);
            return Results.Ok(new { id, deleted = true });
        });

        app.MapPost("/api/accounts/{id}/test", async (string id, AccountRepository repo,
            CredentialEncryptor encryptor, AppConfig config, OAuthTokenRepository oauthTokenRepo,
            IOAuthAccessTokenProvider oauthProvider,
            ILogger<AccountRepository> logger) =>
        {
            var account = repo.GetById(id);
            if (account is null)
                return Results.NotFound();

            logger.LogInformation("Testing connection for account {AccountName} ({AccountId})",
                account.Name, id);

            try
            {
                // Zoho OAuth uses REST API, not IMAP — test via API instead
                if (account.Provider.Equals("zoho", StringComparison.OrdinalIgnoreCase)
                    && account.AuthType.Equals("oauth2", StringComparison.OrdinalIgnoreCase))
                {
                    var accessToken = await oauthProvider.GetAccessTokenAsync(id).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("No OAuth access token available.");

                    // Get the correct Zoho domain from stored api_domain
                    var tokenRecord = oauthTokenRepo.GetByAccountId(id);
                    var mailDomain = "https://mail.zoho.com";
                    if (tokenRecord?.ApiDomain is not null)
                        mailDomain = tokenRecord.ApiDomain.Replace("www.zohoapis", "mail.zoho").TrimEnd('/');

                    using var http = new HttpClient();
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Zoho-oauthtoken", accessToken);
                    var response = await http.GetAsync($"{mailDomain}/api/accounts").ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    logger.LogDebug("Zoho test response ({Status}): {Body}", response.StatusCode, body);

                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException($"Zoho API returned {response.StatusCode}: {body}");

                    logger.LogInformation("Account test successful (Zoho REST) for {AccountName} ({AccountId})",
                        account.Name, id);
                    return Results.Ok(new { success = true, message = "Zoho REST API connection successful" });
                }

                using var client = new MailKit.Net.Imap.ImapClient();
                await client.ConnectAsync(account.ImapHost, account.ImapPort,
                    MailKit.Security.SecureSocketOptions.SslOnConnect).ConfigureAwait(false);

                if (account.AuthType.Equals("oauth2", StringComparison.OrdinalIgnoreCase))
                {
                    var accessToken = await oauthProvider.GetAccessTokenAsync(id).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("No OAuth access token available.");
                    var oauth2 = new MailKit.Security.SaslMechanismOAuth2(account.Username, accessToken);
                    await client.AuthenticateAsync(oauth2).ConfigureAwait(false);
                }
                else
                {
                    var password = encryptor.Decrypt(account.CredentialsEnc);
                    await client.AuthenticateAsync(account.Username, password).ConfigureAwait(false);
                }

                await client.DisconnectAsync(true).ConfigureAwait(false);

                logger.LogInformation("Account test successful for {AccountName} ({AccountId})",
                    account.Name, id);
                return Results.Ok(new { success = true, message = "Connection successful" });
            }
            catch (Exception ex)
            {
                var detail = ex.InnerException?.Message ?? ex.Message;
                logger.LogError(ex, "Account test failed for {AccountName} ({AccountId}): {Error}",
                    account.Name, id, detail);
                return Results.Json(new { success = false, message = $"{ex.GetType().Name}: {detail}" }, statusCode: 502);
            }
        });

        app.MapPost("/api/accounts/{id}/fetch-recent", async (string id, AccountRepository repo,
            CredentialEncryptor encryptor, IOAuthAccessTokenProvider oauthProvider,
            ILogger<AccountRepository> logger) =>
        {
            var account = repo.GetById(id);
            if (account is null)
                return Results.NotFound();

            logger.LogInformation("Fetching recent emails for account {AccountName} ({AccountId})",
                account.Name, id);

            try
            {
                using var client = new MailKit.Net.Imap.ImapClient();
                await client.ConnectAsync(account.ImapHost, account.ImapPort,
                    MailKit.Security.SecureSocketOptions.SslOnConnect).ConfigureAwait(false);

                if (account.AuthType.Equals("oauth2", StringComparison.OrdinalIgnoreCase))
                {
                    var accessToken = await oauthProvider.GetAccessTokenAsync(id).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("No OAuth access token available.");
                    var oauth2 = new MailKit.Security.SaslMechanismOAuth2(account.Username, accessToken);
                    await client.AuthenticateAsync(oauth2).ConfigureAwait(false);
                }
                else
                {
                    var password = encryptor.Decrypt(account.CredentialsEnc);
                    await client.AuthenticateAsync(account.Username, password).ConfigureAwait(false);
                }

                var inbox = client.Inbox;
                await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly).ConfigureAwait(false);

                var count = inbox.Count;
                var emails = new List<object>();

                if (count > 0)
                {
                    var start = Math.Max(0, count - 10);
                    var fetchRequest = new MailKit.FetchRequest(
                        MailKit.MessageSummaryItems.Envelope | MailKit.MessageSummaryItems.UniqueId);
                    var items = await inbox.FetchAsync(start, count - 1, fetchRequest)
                        .ConfigureAwait(false);

                    // Reverse so most recent comes first
                    foreach (var item in items.Reverse())
                    {
                        emails.Add(new
                        {
                            subject = item.Envelope?.Subject ?? "(no subject)",
                            from = item.Envelope?.From?.ToString() ?? "",
                            date = item.Envelope?.Date?.ToString("g") ?? "",
                        });
                    }
                }

                await client.DisconnectAsync(true).ConfigureAwait(false);

                logger.LogInformation("Fetched {Count} recent emails (total: {Total}) for {AccountName}",
                    emails.Count, count, account.Name);
                return Results.Ok(new { success = true, total = count, emails });
            }
            catch (Exception ex)
            {
                var detail = ex.InnerException?.Message ?? ex.Message;
                logger.LogError(ex, "Fetch recent failed for {AccountName} ({AccountId}): {Error}",
                    account.Name, id, detail);
                return Results.Json(new { success = false, message = $"{ex.GetType().Name}: {detail}" }, statusCode: 502);
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

public record ToggleEnabledRequest
{
    public bool? Enabled { get; init; }
}
