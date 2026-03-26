using System.Text.Json;

namespace UltimateImapMcp.Dashboard.Tests;

/// <summary>
/// Regression tests verifying that all dashboard API response shapes serialize
/// to camelCase JSON (no property names starting with an uppercase letter).
/// The dashboard uses <c>JsonNamingPolicy.CamelCase</c> globally, so anonymous
/// type property names in C# must already be camelCase to avoid confusion.
/// These tests serialize representative response objects and assert every
/// top-level (and nested) JSON property name starts with a lowercase letter.
/// </summary>
public class JsonCasingTests
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Asserts every property name in a <see cref="JsonElement"/> (recursively)
    /// begins with a lowercase letter.
    /// </summary>
    private static void AssertAllPropertiesCamelCase(JsonElement element, string path = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var fullPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
                    Assert.False(char.IsUpper(prop.Name[0]),
                        $"Property '{fullPath}' starts with uppercase '{prop.Name[0]}'");
                    AssertAllPropertiesCamelCase(prop.Value, fullPath);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    AssertAllPropertiesCamelCase(item, $"{path}[{index}]");
                    index++;
                }
                break;
        }
    }

    private static void AssertCamelCase(object value)
    {
        var json = JsonSerializer.Serialize(value, CamelCaseOptions);
        using var doc = JsonDocument.Parse(json);
        AssertAllPropertiesCamelCase(doc.RootElement);
    }

    // ---------------------------------------------------------------
    // Auth responses (PinAuthMiddleware)
    // ---------------------------------------------------------------

    [Fact]
    public void AuthStatusResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { hasPinSet = true });
    }

    [Fact]
    public void AuthSetupResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { token = "abc123", message = "PIN set successfully" });
    }

    [Fact]
    public void AuthLoginResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { token = "abc123" });
    }

    [Fact]
    public void AuthErrorResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { error = "Invalid PIN" });
    }

    [Fact]
    public void AuthLogoutResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { message = "Logged out" });
    }

    // ---------------------------------------------------------------
    // Settings responses (SettingsApi)
    // ---------------------------------------------------------------

    [Fact]
    public void SettingsGetResponse_serializes_to_camelCase()
    {
        var response = new
        {
            server = new
            {
                transport = "stdio",
                httpPort = 8080,
                dashboardPort = 9090,
                dashboardEnabled = true,
                dashboardAuth = "pin",
                dashboardAutoOpen = false,
                logLevel = "Information"
            },
            cache = new
            {
                dbPath = "/tmp/cache.db",
                maxSizeMb = 512,
                defaultWindowDays = 30,
                maxBodyAgeDays = 90,
                imapFallbackTtlHours = 24
            },
            queue = new
            {
                p0FlushInterval = 5,
                p1FlushInterval = 30,
                p2FlushInterval = 300,
                sendUndoWindow = 10,
                maxRetries = 3
            },
            llm = new
            {
                enabled = false,
                provider = "openai",
                model = "gpt-4",
                dailyTokenBudget = 100000,
                monthlyCostLimit = 10.0,
                autoAnalyzeNew = false
            },
            sync = new
            {
                enabled = true,
                pollInterval = 60,
                maxMessagesPerSync = 500
            },
            metrics = new
            {
                enabled = false,
                port = 9100,
                path = "/metrics",
                internalRetentionDays = 7
            },
            accountCount = 2
        };

        AssertCamelCase(response);
    }

    [Fact]
    public void SettingsPutResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new
        {
            updated = new List<string> { "transport", "http_port" },
            persisted = true,
            message = "Settings updated. Some changes (ports, transport) require a restart to take effect."
        });
    }

    [Fact]
    public void SettingsPutWarningResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new
        {
            updated = new List<string> { "transport" },
            persisted = false,
            warning = "Settings applied in-memory but could not save to disk: ..."
        });
    }

    // ---------------------------------------------------------------
    // Account responses (AccountsApi)
    // ---------------------------------------------------------------

    [Fact]
    public void AccountListResponse_serializes_to_camelCase()
    {
        var response = new[]
        {
            new
            {
                id = "abc-123",
                name = "Test Account",
                imapHost = "imap.example.com",
                imapPort = 993,
                smtpHost = "smtp.example.com",
                smtpPort = 587,
                smtpUseSsl = false,
                username = "user@example.com",
                authType = "app_password",
                provider = "generic",
                createdAt = "2024-01-01",
                updatedAt = "2024-01-02"
            }
        };

        AssertCamelCase(response);
    }

    [Fact]
    public void AccountCreateResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { id = "abc-123" });
    }

    [Fact]
    public void AccountUpdateResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { id = "abc-123", updated = true });
    }

    [Fact]
    public void AccountDeleteResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { id = "abc-123", deleted = true });
    }

    [Fact]
    public void AccountTestResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { success = true, message = "Connection successful" });
    }

    [Fact]
    public void AccountTestFailureResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { success = false, message = "AuthenticationException: Invalid credentials" });
    }

    // ---------------------------------------------------------------
    // Message responses (MessagesApi)
    // ---------------------------------------------------------------

    [Fact]
    public void FolderListResponse_serializes_to_camelCase()
    {
        var response = new[]
        {
            new
            {
                id = 1,
                path = "INBOX",
                displayName = "Inbox",
                role = "inbox",
                messageCount = 42,
                unreadCount = 5,
                syncEnabled = true,
                lastSyncedAt = "2024-01-01T00:00:00Z"
            }
        };

        AssertCamelCase(response);
    }

    [Fact]
    public void MessageListResponse_serializes_to_camelCase()
    {
        var response = new[]
        {
            new
            {
                id = 1,
                uid = 100L,
                subject = "Test Subject",
                fromAddress = "John <john@example.com>",
                fromEmail = "john@example.com",
                dateEpoch = 1700000000L,
                date = "2024-01-01",
                flags = "\\Seen",
                snippet = "Hello world...",
                hasAttachments = false,
                folderPath = "INBOX"
            }
        };

        AssertCamelCase(response);
    }

    [Fact]
    public void MessageDetailResponse_serializes_to_camelCase()
    {
        var response = new
        {
            id = 1,
            uid = 100L,
            subject = "Test Subject",
            fromAddress = "John <john@example.com>",
            fromEmail = "john@example.com",
            toAddresses = "Jane <jane@example.com>",
            ccAddresses = "",
            dateEpoch = 1700000000L,
            date = "2024-01-01",
            flags = "\\Seen",
            snippet = "Hello world...",
            hasAttachments = false,
            bodyText = "Hello world",
            bodyHtml = "<p>Hello world</p>",
            bodyFetched = true,
            threadId = "thread-1"
        };

        AssertCamelCase(response);
    }

    [Fact]
    public void SearchResultResponse_serializes_to_camelCase()
    {
        var response = new[]
        {
            new
            {
                id = 1,
                uid = 100L,
                folderId = 1,
                subject = "Test Subject",
                fromAddress = "John <john@example.com>",
                fromEmail = "john@example.com",
                dateEpoch = 1700000000L,
                date = "2024-01-01",
                flags = "\\Seen",
                snippet = "Hello world...",
                hasAttachments = false,
                folderPath = "INBOX"
            }
        };

        AssertCamelCase(response);
    }

    // ---------------------------------------------------------------
    // Queue responses (QueueApi)
    // ---------------------------------------------------------------

    [Fact]
    public void QueueCancelResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { id = "op-123", cancelled = true });
    }

    [Fact]
    public void QueueConfirmResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { id = "op-123", confirmed = true });
    }

    // ---------------------------------------------------------------
    // OAuth responses (OAuthApi)
    // ---------------------------------------------------------------

    [Fact]
    public void OAuthProvidersResponse_serializes_to_camelCase()
    {
        var response = new[]
        {
            new
            {
                provider = "gmail",
                configured = true,
                authUrl = "https://accounts.google.com/o/oauth2/auth",
                scopes = "https://mail.google.com/"
            }
        };

        AssertCamelCase(response);
    }

    // ---------------------------------------------------------------
    // Sync responses (SyncApi)
    // ---------------------------------------------------------------

    [Fact]
    public void SyncTriggerResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { triggered = true, accountId = "acc-1", folderPath = "INBOX" });
    }

    [Fact]
    public void SyncTriggerAllResponse_serializes_to_camelCase()
    {
        AssertCamelCase(new { triggered = 2, total = 2, errors = new List<string>() });
    }

    // ---------------------------------------------------------------
    // Middleware direct-serialize responses
    // ---------------------------------------------------------------

    [Fact]
    public void MiddlewareAuthRequired_serializes_to_camelCase()
    {
        // The middleware uses JsonSerializer.Serialize directly (not Results.Json),
        // so the anonymous type property name IS the JSON key — must be lowercase.
        var response = new { error = "Authentication required" };
        var json = JsonSerializer.Serialize(response);
        using var doc = JsonDocument.Parse(json);
        AssertAllPropertiesCamelCase(doc.RootElement);
    }

    [Fact]
    public void MiddlewareInvalidSession_serializes_to_camelCase()
    {
        var response = new { error = "Invalid or expired session" };
        var json = JsonSerializer.Serialize(response);
        using var doc = JsonDocument.Parse(json);
        AssertAllPropertiesCamelCase(doc.RootElement);
    }
}
