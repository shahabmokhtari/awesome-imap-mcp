using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AwesomeImapMcp.Dashboard;

/// <summary>What the dashboard sends to <c>POST /api/feedback</c>.</summary>
public sealed record FeedbackRequest(
    string? Kind,
    string? Message,
    string? Contact,
    string? ClientId,
    string? Source,
    string? Screen,
    string? Language,
    string? Browser);

/// <summary>How a relay answer is reported back to the feedback sheet.</summary>
public enum FeedbackOutcome
{
    /// <summary>The relay accepted the note (200 or 202).</summary>
    Sent,
    /// <summary>Offline, timed out, rate limited or a server error: keep the draft and retry with the same client ID.</summary>
    Retry,
    /// <summary>The relay refused the request; retrying will not help.</summary>
    Failed,
}

public sealed record FeedbackResult(FeedbackOutcome Outcome, string? Ref = null, string? Status = null);

/// <summary>
/// Files in-app feedback as GitHub issues through the shared issues.awesomehq.app relay.
/// The dashboard backend calls the relay (not the browser), so no Origin header is sent and the
/// dashboard works on any host and port. Only the app's public key is used; it can create issues
/// in this app's repo and nothing else. No GitHub token is involved.
/// </summary>
public sealed class FeedbackRelay
{
    public const string Endpoint = "https://issues.awesomehq.app/v1/issues";

    /// <summary>Public relay key for this app. Not a secret: it can only open new issues in this app's repo.</summary>
    public const string PublicKey = "pk_4M7Ex9In8bDMjSFBg5KDUEqZ";

    public const int MaxMessage = 2000;
    public const string Platform = "Web";

    /// <summary>Set to 1 to send feedback for real from a Debug build (Debug builds only log it by default).</summary>
    public const string SendInDebugVariable = "AWESOME_IMAP_FEEDBACK_SEND";

    public const string HttpClientName = "feedback";

    private static readonly string[] Kinds = ["bug", "idea", "other"];
    private static readonly string[] Sources = ["button", "shake", "page", "qr"];

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly bool _send;
    private readonly string _appVersion;
    private readonly string _os;

    public FeedbackRelay(HttpClient http, ILogger logger, bool send, string appVersion, string os)
    {
        _http = http;
        _logger = logger;
        _send = send;
        _appVersion = appVersion;
        _os = os;
    }

    /// <summary>
    /// Whether this build sends feedback. Release builds always do; Debug builds only log the
    /// request unless <see cref="SendInDebugVariable"/> is set to 1.
    /// </summary>
    public static bool ShouldSend(bool isDebugBuild, string? optIn) =>
        !isDebugBuild || optIn == "1";

    public static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Checks a request and returns an error message, or null when it can be sent.</summary>
    public static string? Validate(FeedbackRequest request)
    {
        if (request.Kind is null || !Kinds.Contains(request.Kind.ToLowerInvariant()))
            return "kind must be bug, idea or other.";
        var message = CleanMessage(request.Message);
        if (message.Length == 0)
            return "message is required.";
        if (CountCodePoints(message) > MaxMessage)
            return $"message is longer than {MaxMessage} characters.";
        if (!Guid.TryParse(request.ClientId, out _))
            return "clientId must be a UUID.";
        var contact = request.Contact?.Trim();
        if (!string.IsNullOrEmpty(contact) && !LooksLikeEmail(contact))
            return "contact must be an email address.";
        if (request.Source is not null && !Sources.Contains(request.Source))
            return "source must be button, shake, page or qr.";
        return null;
    }

    /// <summary>Trims the message and removes control characters except newlines and tabs.</summary>
    public static string CleanMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var normalized = message.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\t' || !Rune.IsControl(rune))
                sb.Append(rune.ToString());
        }
        return sb.ToString().Trim();
    }

    public static int CountCodePoints(string s) => s.EnumerateRunes().Count();

    /// <summary>Builds the relay request body. Assumes <see cref="Validate"/> passed.</summary>
    public JsonObject BuildBody(FeedbackRequest request)
    {
        var kind = request.Kind!.ToLowerInvariant();
        var kindTitle = char.ToUpperInvariant(kind[0]) + kind[1..];
        var message = CleanMessage(request.Message);
        var source = request.Source ?? "button";
        var platform = Platform.ToLowerInvariant();

        var body = new JsonObject
        {
            ["message"] = message,
            ["title"] = $"{kindTitle} · {Platform}: {message}",
        };
        var contact = request.Contact?.Trim();
        if (!string.IsNullOrEmpty(contact))
            body["contact"] = contact;
        body["clientID"] = Guid.Parse(request.ClientId!).ToString();
        body["labels"] = new JsonArray($"kind:{kind}", $"platform:{platform}", $"source:{source}");

        var metadata = new JsonObject { ["Platform"] = platform };
        AddIfPresent(metadata, "App version", _appVersion);
        AddIfPresent(metadata, "OS", _os);
        AddIfPresent(metadata, "Device", Coarse(request.Browser));
        AddIfPresent(metadata, "Language", Coarse(request.Language));
        AddIfPresent(metadata, "Screen", Coarse(request.Screen));
        metadata["Source"] = source;
        body["metadata"] = metadata;
        return body;
    }

    /// <summary>Maps a relay HTTP status (null when the relay could not be reached) to an outcome.</summary>
    public static FeedbackOutcome Classify(HttpStatusCode? status) => status switch
    {
        null => FeedbackOutcome.Retry,
        HttpStatusCode.OK or HttpStatusCode.Accepted => FeedbackOutcome.Sent,
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => FeedbackOutcome.Retry,
        >= HttpStatusCode.InternalServerError => FeedbackOutcome.Retry,
        _ => FeedbackOutcome.Failed,
    };

    public async Task<FeedbackResult> SendAsync(FeedbackRequest request, CancellationToken ct)
    {
        var body = BuildBody(request);

        if (!_send)
        {
            _logger.LogInformation(
                "Feedback not sent (Debug build; set {Variable}=1 to send for real): {Body}",
                SendInDebugVariable, body.ToJsonString());
            return new FeedbackResult(FeedbackOutcome.Sent, Ref: "DEBUG", Status: "logged");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", PublicKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Feedback relay could not be reached");
            return new FeedbackResult(FeedbackOutcome.Retry);
        }

        using (response)
        {
            var outcome = Classify(response.StatusCode);
            string? reference = null, status = null, error = null;
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                reference = ReadString(doc.RootElement, "ref");
                status = ReadString(doc.RootElement, "status");
                error = ReadString(doc.RootElement, "error");
            }
            catch (JsonException)
            {
                // A non-JSON answer (e.g. a proxy error page) is classified by status alone.
            }

            if (outcome == FeedbackOutcome.Sent)
                _logger.LogInformation("Feedback sent: {Ref} ({Status})", reference, status);
            else
                _logger.LogWarning("Feedback relay answered {Code} {Error}", (int)response.StatusCode, error);
            return new FeedbackResult(outcome, reference, status);
        }
    }

    /// <summary>"0.1.0 (abc1234)" from the entry assembly's informational version, or "0.1.0".</summary>
    public static string AppVersion(Assembly? assembly)
    {
        var info = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly?.GetName().Version?.ToString(3)
                   ?? "unknown";
        var plus = info.IndexOf('+');
        if (plus < 0) return info;
        var build = info[(plus + 1)..];
        return build.Length == 0 ? info[..plus] : $"{info[..plus]} ({build[..Math.Min(7, build.Length)]})";
    }

    /// <summary>Coarse OS name and version where the server runs, e.g. "macOS 26.2".</summary>
    public static string CurrentOs()
    {
        var v = Environment.OSVersion.Version;
        if (OperatingSystem.IsMacOS()) return $"macOS {v.Major}.{v.Minor}";
        if (OperatingSystem.IsWindows()) return $"Windows {v.Major}.{v.Minor}.{v.Build}";
        if (OperatingSystem.IsLinux()) return "Linux";
        return "Other";
    }

    private static void AddIfPresent(JsonObject obj, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) obj[key] = value;
    }

    /// <summary>Keeps client-supplied diagnostics short and printable (the relay drops anything else).</summary>
    private static string? Coarse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Trim().Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= 40 ? cleaned : cleaned[..40];
    }

    private static bool LooksLikeEmail(string s)
    {
        if (s.Length > 254 || s.Any(char.IsWhiteSpace)) return false;
        var at = s.IndexOf('@');
        return at > 0 && at == s.LastIndexOf('@') && s.IndexOf('.', at) > at + 1 && !s.EndsWith('.');
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
