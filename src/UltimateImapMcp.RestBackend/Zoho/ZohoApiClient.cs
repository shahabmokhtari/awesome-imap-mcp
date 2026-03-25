using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.OAuth;

namespace UltimateImapMcp.RestBackend.Zoho;

/// <summary>
/// Low-level HTTP client for the Zoho Mail REST API.
/// Handles authentication, request construction, and response deserialization.
///
/// Zoho Mail API reference:
/// - Base URL: https://mail.zoho.com/api/
/// - Auth header: Authorization: Zoho-oauthtoken {access_token}
/// </summary>
internal sealed class ZohoApiClient
{
    private const string BaseUrl = "https://mail.zoho.com/api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthAccessTokenProvider _tokenProvider;
    private readonly ILogger<ZohoApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ZohoApiClient(
        IHttpClientFactory httpClientFactory,
        IOAuthAccessTokenProvider tokenProvider,
        ILogger<ZohoApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Account
    // ------------------------------------------------------------------

    /// <summary>Lists all Zoho mail accounts for the authenticated user.</summary>
    public async Task<List<ZohoMailAccount>> GetAccountsAsync(string accountId, CancellationToken ct = default)
    {
        var response = await GetAsync<ZohoResponse<List<ZohoMailAccount>>>(
            accountId, "/accounts", ct).ConfigureAwait(false);
        return response?.Data ?? [];
    }

    // ------------------------------------------------------------------
    // Folders
    // ------------------------------------------------------------------

    /// <summary>Lists all folders for a Zoho mail account.</summary>
    public async Task<List<ZohoFolder>> GetFoldersAsync(string accountId, string zohoAccountId,
        CancellationToken ct = default)
    {
        var response = await GetAsync<ZohoResponse<List<ZohoFolder>>>(
            accountId, $"/accounts/{zohoAccountId}/folders", ct).ConfigureAwait(false);
        return response?.Data ?? [];
    }

    // ------------------------------------------------------------------
    // Messages
    // ------------------------------------------------------------------

    /// <summary>Lists messages in a folder with pagination support.</summary>
    public async Task<List<ZohoMessageSummary>> GetMessagesAsync(
        string accountId, string zohoAccountId, string folderId,
        int limit = 100, int start = 0,
        CancellationToken ct = default)
    {
        var url = $"/accounts/{zohoAccountId}/folders/{folderId}/messages?limit={limit}&start={start}&sortBy=date";
        var response = await GetAsync<ZohoResponse<List<ZohoMessageSummary>>>(
            accountId, url, ct).ConfigureAwait(false);
        return response?.Data ?? [];
    }

    /// <summary>Gets the full details of a specific message including body content.</summary>
    public async Task<ZohoMessageDetail?> GetMessageDetailAsync(
        string accountId, string zohoAccountId, string folderId, string messageId,
        CancellationToken ct = default)
    {
        var url = $"/accounts/{zohoAccountId}/folders/{folderId}/messages/{messageId}/details";
        var response = await GetAsync<ZohoResponse<ZohoMessageDetail>>(
            accountId, url, ct).ConfigureAwait(false);
        return response?.Data;
    }

    // ------------------------------------------------------------------
    // Send
    // ------------------------------------------------------------------

    /// <summary>Sends an email via Zoho REST API.</summary>
    public async Task SendMessageAsync(string accountId, string zohoAccountId,
        ZohoSendRequest request, CancellationToken ct = default)
    {
        var url = $"/accounts/{zohoAccountId}/messages";
        await PostAsync<ZohoSendRequest>(accountId, url, request, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Move
    // ------------------------------------------------------------------

    /// <summary>Moves a message to a different folder.</summary>
    public async Task MoveMessageAsync(string accountId, string zohoAccountId,
        string folderId, string messageId, string destFolderId,
        CancellationToken ct = default)
    {
        var url = $"/accounts/{zohoAccountId}/folders/{folderId}/messages/{messageId}/folder";
        var body = new ZohoMoveRequest { DestFolderId = destFolderId };
        await PutAsync(accountId, url, body, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Delete
    // ------------------------------------------------------------------

    /// <summary>Deletes a message.</summary>
    public async Task DeleteMessageAsync(string accountId, string zohoAccountId,
        string messageId, CancellationToken ct = default)
    {
        var url = $"/accounts/{zohoAccountId}/messages/{messageId}";
        await DeleteAsync(accountId, url, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Flag / status updates
    // ------------------------------------------------------------------

    /// <summary>Updates the read/flag status of a message.</summary>
    public async Task UpdateMessageFlagsAsync(string accountId, string zohoAccountId,
        string folderId, string messageId, ZohoFlagUpdateRequest request,
        CancellationToken ct = default)
    {
        var url = $"/accounts/{zohoAccountId}/folders/{folderId}/messages/{messageId}";
        await PutAsync(accountId, url, request, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // HTTP helpers
    // ------------------------------------------------------------------

    private async Task<T?> GetAsync<T>(string accountId, string path, CancellationToken ct)
    {
        using var client = await CreateAuthenticatedClientAsync(accountId, ct).ConfigureAwait(false);
        var url = $"{BaseUrl}{path}";

        _logger.LogDebug("Zoho GET {Url}", url);
        var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "GET", url).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
    }

    private async Task PostAsync<T>(string accountId, string path, T body, CancellationToken ct)
    {
        using var client = await CreateAuthenticatedClientAsync(accountId, ct).ConfigureAwait(false);
        var url = $"{BaseUrl}{path}";

        _logger.LogDebug("Zoho POST {Url}", url);
        var response = await client.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "POST", url).ConfigureAwait(false);
    }

    private async Task PutAsync<T>(string accountId, string path, T body, CancellationToken ct)
    {
        using var client = await CreateAuthenticatedClientAsync(accountId, ct).ConfigureAwait(false);
        var url = $"{BaseUrl}{path}";

        _logger.LogDebug("Zoho PUT {Url}", url);
        var response = await client.PutAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "PUT", url).ConfigureAwait(false);
    }

    private async Task DeleteAsync(string accountId, string path, CancellationToken ct)
    {
        using var client = await CreateAuthenticatedClientAsync(accountId, ct).ConfigureAwait(false);
        var url = $"{BaseUrl}{path}";

        _logger.LogDebug("Zoho DELETE {Url}", url);
        var response = await client.DeleteAsync(url, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "DELETE", url).ConfigureAwait(false);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string accountId, CancellationToken ct)
    {
        var accessToken = await _tokenProvider.GetAccessTokenAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No OAuth access token available for Zoho account '{accountId}'. " +
                "Ensure the account has been authorized via OAuth.");

        var client = _httpClientFactory.CreateClient("ZohoMail");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Zoho-oauthtoken", accessToken);
        return client;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string method, string url)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            _logger.LogError("Zoho {Method} {Url} returned {StatusCode}: {Body}",
                method, url, (int)response.StatusCode, body);
            throw new HttpRequestException(
                $"Zoho API error: {method} {url} returned {(int)response.StatusCode}. {body}");
        }
    }
}
