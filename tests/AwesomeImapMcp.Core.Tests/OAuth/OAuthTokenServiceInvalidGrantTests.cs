using System.Net;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.OAuth;

namespace AwesomeImapMcp.Core.Tests.OAuth;

/// <summary>
/// Tests that OAuthTokenService correctly distinguishes permanent OAuth errors
/// (invalid_grant → OAuthRefreshTokenRevokedException) from transient errors
/// (5xx, 429 → InvalidOperationException).
/// </summary>
public class OAuthTokenServiceInvalidGrantTests
{
    private static OAuthProviderConfig MakeConfig(string tokenUrl) => new()
    {
        ClientId = "test-client-id",
        TokenUrl = tokenUrl
    };

    private static IHttpClientFactory MakeFactory(HttpResponseMessage response)
    {
        var handler = new FakeHttpHandler(response);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
        return new FakeHttpClientFactory(client);
    }

    [Fact]
    public async Task RefreshTokenAsync_InvalidGrant_ThrowsOAuthRefreshTokenRevokedException()
    {
        var responseJson = """{"error":"invalid_grant","error_description":"Bad Request"}""";
        var factory = MakeFactory(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(responseJson)
        });

        var service = new OAuthTokenService(factory);
        var config = MakeConfig("https://oauth2.googleapis.com/token");

        var ex = await Assert.ThrowsAsync<OAuthRefreshTokenRevokedException>(
            () => service.RefreshTokenAsync(config, "revoked-refresh-token"));

        Assert.Equal("invalid_grant", ex.OAuthError);
    }

    [Fact]
    public async Task RefreshTokenAsync_UnauthorizedClient_ThrowsOAuthRefreshTokenRevokedException()
    {
        var responseJson = """{"error":"unauthorized_client"}""";
        var factory = MakeFactory(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(responseJson)
        });

        var service = new OAuthTokenService(factory);
        var config = MakeConfig("https://oauth2.googleapis.com/token");

        var ex = await Assert.ThrowsAsync<OAuthRefreshTokenRevokedException>(
            () => service.RefreshTokenAsync(config, "bad-token"));

        Assert.Equal("unauthorized_client", ex.OAuthError);
    }

    [Fact]
    public async Task RefreshTokenAsync_ServerError_ThrowsInvalidOperationException_NotRevoked()
    {
        // 5xx is transient — must NOT throw OAuthRefreshTokenRevokedException
        var factory = MakeFactory(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Internal Server Error")
        });

        var service = new OAuthTokenService(factory);
        var config = MakeConfig("https://oauth2.googleapis.com/token");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RefreshTokenAsync(config, "some-token"));

        Assert.IsNotType<OAuthRefreshTokenRevokedException>(ex);
    }

    [Fact]
    public async Task RefreshTokenAsync_TooManyRequests_ThrowsInvalidOperationException_NotRevoked()
    {
        // 429 is transient (rate-limit) — must NOT throw OAuthRefreshTokenRevokedException
        var factory = MakeFactory(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"rate_limit_exceeded"}""")
        });

        var service = new OAuthTokenService(factory);
        var config = MakeConfig("https://oauth2.googleapis.com/token");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RefreshTokenAsync(config, "some-token"));

        Assert.IsNotType<OAuthRefreshTokenRevokedException>(ex);
    }

    [Fact]
    public async Task RefreshTokenAsync_NonJsonBody_StillThrowsRevoked_WithStatusCodeAsError()
    {
        // 400 with non-JSON body — should still detect as permanent
        var factory = MakeFactory(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Bad Request")
        });

        var service = new OAuthTokenService(factory);
        var config = MakeConfig("https://oauth2.googleapis.com/token");

        var ex = await Assert.ThrowsAsync<OAuthRefreshTokenRevokedException>(
            () => service.RefreshTokenAsync(config, "revoked-token"));

        // OAuthError falls back to the status code string
        Assert.Equal("BadRequest", ex.OAuthError);
    }

    [Fact]
    public async Task RefreshWithScopeAsync_InvalidGrant_ThrowsOAuthRefreshTokenRevokedException()
    {
        var responseJson = """{"error":"invalid_grant"}""";
        var factory = MakeFactory(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(responseJson)
        });

        var service = new OAuthTokenService(factory);
        var config = MakeConfig("https://oauth2.googleapis.com/token");

        var ex = await Assert.ThrowsAsync<OAuthRefreshTokenRevokedException>(
            () => service.RefreshWithScopeAsync(config, "revoked-token", "https://graph.microsoft.com/.default"));

        Assert.Equal("invalid_grant", ex.OAuthError);
    }

    [Fact]
    public void OAuthRefreshTokenRevokedException_PropertiesCorrectlySet()
    {
        var ex = new OAuthRefreshTokenRevokedException("invalid_grant", """{"error":"invalid_grant"}""");

        Assert.Equal("invalid_grant", ex.OAuthError);
        Assert.Contains("invalid_grant", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OAuthRefreshTokenRevokedException_IsException()
    {
        Assert.True(typeof(OAuthRefreshTokenRevokedException).IsSubclassOf(typeof(Exception)));
    }
}

file sealed class FakeHttpHandler(HttpResponseMessage response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(response);
}

file sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
