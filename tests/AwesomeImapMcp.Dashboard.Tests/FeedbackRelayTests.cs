using System.Net;
using System.Text.Json.Nodes;
using AwesomeImapMcp.Dashboard;
using Microsoft.Extensions.Logging.Abstractions;

namespace AwesomeImapMcp.Dashboard.Tests;

public class FeedbackRelayTests
{
    private const string ClientId = "3f2b8c1e-7a4d-4e9b-9c2a-5d6e7f8a9b0c";

    private static FeedbackRequest Request(
        string kind = "bug", string message = "The sync page freezes", string? contact = null,
        string? source = "button", string? screen = "Sync", string? language = "en-US", string? browser = "Edge 142") =>
        new(kind, message, contact, ClientId, source, screen, language, browser);

    private static FeedbackRelay Relay(StubHandler handler, bool send = true) =>
        new(new HttpClient(handler), NullLogger.Instance, send, "0.1.0 (abc1234)", "macOS 26.2");

    // ---------- body mapping ----------

    [Fact]
    public void BuildBody_MapsEveryFieldInContractOrder()
    {
        var body = Relay(new StubHandler()).BuildBody(Request(contact: "  someone@example.com "));

        Assert.Equal(["message", "title", "contact", "clientID", "labels", "metadata"],
            body.Select(p => p.Key).ToArray());
        Assert.Equal("The sync page freezes", (string?)body["message"]);
        Assert.Equal("Bug · Web: The sync page freezes", (string?)body["title"]);
        Assert.Equal("someone@example.com", (string?)body["contact"]);
        Assert.Equal(ClientId, (string?)body["clientID"]);
        Assert.Equal(["kind:bug", "platform:web", "source:button"],
            body["labels"]!.AsArray().Select(l => (string)l!).ToArray());

        var metadata = body["metadata"]!.AsObject();
        Assert.Equal(["Platform", "App version", "OS", "Device", "Language", "Screen", "Source"],
            metadata.Select(p => p.Key).ToArray());
        Assert.Equal("web", (string?)metadata["Platform"]);
        Assert.Equal("0.1.0 (abc1234)", (string?)metadata["App version"]);
        Assert.Equal("macOS 26.2", (string?)metadata["OS"]);
        Assert.Equal("Edge 142", (string?)metadata["Device"]);
        Assert.Equal("en-US", (string?)metadata["Language"]);
        Assert.Equal("Sync", (string?)metadata["Screen"]);
        Assert.Equal("button", (string?)metadata["Source"]);
    }

    [Fact]
    public void BuildBody_OmitsEmptyContactAndAbsentMetadata()
    {
        var body = Relay(new StubHandler()).BuildBody(
            Request(kind: "Idea", contact: "   ", source: null, screen: null, language: "", browser: null));

        Assert.False(body.ContainsKey("contact"));
        Assert.Equal("Idea · Web: The sync page freezes", (string?)body["title"]);
        Assert.Equal(["kind:idea", "platform:web", "source:button"],
            body["labels"]!.AsArray().Select(l => (string)l!).ToArray());
        Assert.Equal(["Platform", "App version", "OS", "Source"],
            body["metadata"]!.AsObject().Select(p => p.Key).ToArray());
    }

    [Fact]
    public void BuildBody_SendsTheFullMessageInTheTitle()
    {
        // The relay one-lines and truncates titles, so the client sends them whole.
        var longMessage = string.Join(' ', Enumerable.Repeat("word", 100));
        var body = Relay(new StubHandler()).BuildBody(Request(kind: "other", message: longMessage));
        Assert.Equal("Other · Web: " + longMessage, (string?)body["title"]);
    }

    [Fact]
    public void CleanMessage_TrimsAndStripsControlCharactersButKeepsNewlinesAndTabs()
    {
        Assert.Equal("line one\nline\ttwo\nthree", FeedbackRelay.CleanMessage("  line one\r\nline\ttwo\u0007\rthree\u0000  "));
        Assert.Equal("", FeedbackRelay.CleanMessage(null));
    }

    // ---------- validation ----------

    [Fact]
    public void Validate_AcceptsAWellFormedRequest() =>
        Assert.Null(FeedbackRelay.Validate(Request(contact: "someone@example.com")));

    [Theory]
    [InlineData("complaint", "hi", ClientId, null)]
    [InlineData("bug", "   \u0007 ", ClientId, null)]
    [InlineData("bug", "hi", "not-a-uuid", null)]
    [InlineData("bug", "hi", ClientId, "not an email")]
    public void Validate_RejectsBadInput(string kind, string message, string clientId, string? contact) =>
        Assert.NotNull(FeedbackRelay.Validate(new FeedbackRequest(kind, message, contact, clientId, "button", null, null, null)));

    [Fact]
    public void Validate_CountsCodePointsNotUtf16Units()
    {
        // 2000 emoji are 4000 UTF-16 units but exactly the 2000-code-point limit.
        var atLimit = string.Concat(Enumerable.Repeat("😀", FeedbackRelay.MaxMessage));
        Assert.Null(FeedbackRelay.Validate(Request(message: atLimit)));
        Assert.NotNull(FeedbackRelay.Validate(Request(message: atLimit + "x")));
    }

    // ---------- response classification ----------

    [Theory]
    [InlineData(HttpStatusCode.OK, FeedbackOutcome.Sent)]
    [InlineData(HttpStatusCode.Accepted, FeedbackOutcome.Sent)]
    [InlineData(HttpStatusCode.RequestTimeout, FeedbackOutcome.Retry)]
    [InlineData(HttpStatusCode.TooManyRequests, FeedbackOutcome.Retry)]
    [InlineData(HttpStatusCode.InternalServerError, FeedbackOutcome.Retry)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FeedbackOutcome.Retry)]
    [InlineData(HttpStatusCode.BadRequest, FeedbackOutcome.Failed)]
    [InlineData(HttpStatusCode.Unauthorized, FeedbackOutcome.Failed)]
    [InlineData(HttpStatusCode.Forbidden, FeedbackOutcome.Failed)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, FeedbackOutcome.Failed)]
    public void Classify_FollowsTheRelayContract(HttpStatusCode status, FeedbackOutcome expected) =>
        Assert.Equal(expected, FeedbackRelay.Classify(status));

    [Fact]
    public void Classify_UnreachableRelayIsRetry() =>
        Assert.Equal(FeedbackOutcome.Retry, FeedbackRelay.Classify(null));

    // ---------- sending (mocked HTTP) ----------

    [Fact]
    public async Task SendAsync_PostsToTheRelayWithThePublicKey()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted, """{"ref":"IS-TEST0001","status":"queued"}""");
        var result = await Relay(handler).SendAsync(Request(), CancellationToken.None);

        Assert.Equal(new FeedbackResult(FeedbackOutcome.Sent, "IS-TEST0001", "queued"), result);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(FeedbackRelay.Endpoint, sent.Uri);
        Assert.Equal("Bearer " + FeedbackRelay.PublicKey, sent.Authorization);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Null(sent.Origin);
        Assert.Equal(ClientId, (string?)JsonNode.Parse(sent.Body)!["clientID"]);
    }

    [Fact]
    public async Task SendAsync_RetryReusesTheSameClientId()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""");
        var relay = Relay(handler);

        Assert.Equal(FeedbackOutcome.Retry, (await relay.SendAsync(Request(), CancellationToken.None)).Outcome);
        handler.Respond(HttpStatusCode.OK, """{"ref":"IS-TEST0002","status":"filed"}""");
        Assert.Equal(FeedbackOutcome.Sent, (await relay.SendAsync(Request(), CancellationToken.None)).Outcome);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(ClientId, (string?)JsonNode.Parse(r.Body)!["clientID"]));
    }

    [Fact]
    public async Task SendAsync_OfflineIsRetry()
    {
        var handler = new StubHandler { Throw = new HttpRequestException("no route to host") };
        Assert.Equal(FeedbackOutcome.Retry, (await Relay(handler).SendAsync(Request(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task SendAsync_TimeoutIsRetry()
    {
        var handler = new StubHandler { Throw = new TaskCanceledException("timed out") };
        Assert.Equal(FeedbackOutcome.Retry, (await Relay(handler).SendAsync(Request(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task SendAsync_NonJsonErrorPageIsClassifiedByStatus()
    {
        var handler = new StubHandler(HttpStatusCode.BadGateway, "<html>bad gateway</html>");
        Assert.Equal(new FeedbackResult(FeedbackOutcome.Retry), await Relay(handler).SendAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_RefusalIsFailed()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, """{"error":"app_disabled"}""");
        Assert.Equal(FeedbackOutcome.Failed, (await Relay(handler).SendAsync(Request(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task SendAsync_DebugDryRunNeverTouchesTheNetwork()
    {
        var handler = new StubHandler();
        var result = await Relay(handler, send: false).SendAsync(Request(), CancellationToken.None);

        Assert.Equal(FeedbackOutcome.Sent, result.Outcome);
        Assert.Equal("logged", result.Status);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "0", true)]
    [InlineData(true, null, false)]
    [InlineData(true, "", false)]
    [InlineData(true, "true", false)]
    [InlineData(true, "1", true)]
    public void ShouldSend_DebugBuildsOnlySendWhenOptedIn(bool debug, string? optIn, bool expected) =>
        Assert.Equal(expected, FeedbackRelay.ShouldSend(debug, optIn));

    [Theory]
    [InlineData("0.1.0+abc1234def5678", "0.1.0 (abc1234)")]
    [InlineData("0.1.0+abc", "0.1.0 (abc)")]
    [InlineData("0.1.0", "0.1.0")]
    public void AppVersion_FormatsTheInformationalVersion(string informational, string expected)
    {
        var name = new System.Reflection.AssemblyName("FeedbackVersionProbe");
        var builder = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(name, System.Reflection.Emit.AssemblyBuilderAccess.Run);
        builder.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(
            typeof(System.Reflection.AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informational]));
        Assert.Equal(expected, FeedbackRelay.AppVersion(builder));
    }

    private sealed record SentRequest(HttpMethod Method, string Uri, string? Authorization, string? ContentType, string? Origin, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status;
        private string _body;

        public StubHandler(HttpStatusCode status = HttpStatusCode.Accepted, string body = """{"ref":"IS-TEST0000","status":"queued"}""")
        {
            _status = status;
            _body = body;
        }

        public List<SentRequest> Requests { get; } = [];
        public Exception? Throw { get; init; }

        public void Respond(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new SentRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.TryGetValues("Origin", out var o) ? o.Single() : null,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            if (Throw is not null) throw Throw;
            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }
}
