using System.Text.Json.Serialization;

namespace UltimateImapMcp.Core.Configuration;

/// <summary>Top-level application configuration.</summary>
public class AppConfig
{
    /// <summary>
    /// The file path this config was loaded from. Not serialised — set at runtime by Program.cs.
    /// Used by the dashboard to write settings back to disk.
    /// </summary>
    [JsonIgnore]
    public string? SourcePath { get; set; }

    [JsonPropertyName("server")]
    public ServerConfig Server { get; set; } = new();

    [JsonPropertyName("accounts")]
    public List<AccountConfig> Accounts { get; set; } = [];

    [JsonPropertyName("cache")]
    public CacheConfig Cache { get; set; } = new();

    [JsonPropertyName("queue")]
    public QueueConfig Queue { get; set; } = new();

    [JsonPropertyName("llm")]
    public LlmConfig Llm { get; set; } = new();

    [JsonPropertyName("sync")]
    public GlobalSyncConfig Sync { get; set; } = new();

    [JsonPropertyName("metrics")]
    public MetricsConfig Metrics { get; set; } = new();

    [JsonPropertyName("oauth_providers")]
    public Dictionary<string, OAuthProviderConfig> OAuthProviders { get; set; } = new();

    [JsonPropertyName("labels")]
    public LabelsConfig Labels { get; set; } = new();
}

/// <summary>MCP server transport and dashboard settings.</summary>
public class ServerConfig
{
    [JsonPropertyName("transport")]
    public string Transport { get; set; } = "stdio";

    [JsonPropertyName("http_port")]
    public int HttpPort { get; set; } = 3846;

    [JsonPropertyName("dashboard_port")]
    public int DashboardPort { get; set; } = 3847;

    [JsonPropertyName("dashboard_enabled")]
    public bool DashboardEnabled { get; set; } = false;

    [JsonPropertyName("dashboard_auth")]
    public string? DashboardAuth { get; set; }

    [JsonPropertyName("dashboard_auto_open")]
    public bool DashboardAutoOpen { get; set; } = false;

    [JsonPropertyName("log_level")]
    public string LogLevel { get; set; } = "Information";

    [JsonPropertyName("log_file")]
    public string? LogFile { get; set; }

    [JsonPropertyName("log_dir")]
    public string? LogDir { get; set; }

    [JsonPropertyName("heartbeat_interval")]
    public int HeartbeatInterval { get; set; } = 10;

    [JsonPropertyName("heartbeat_stale_after")]
    public int HeartbeatStaleAfter { get; set; } = 5;
}

/// <summary>IMAP/SMTP account configuration.</summary>
public class AccountConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("imap_host")]
    public string ImapHost { get; set; } = string.Empty;

    [JsonPropertyName("imap_port")]
    public int ImapPort { get; set; } = 993;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("auth_type")]
    public string AuthType { get; set; } = "app_password";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "generic";

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("oauth2_client_id")]
    public string? OAuth2ClientId { get; set; }

    [JsonPropertyName("oauth2_client_secret")]
    public string? OAuth2ClientSecret { get; set; }

    [JsonPropertyName("oauth2_refresh_token")]
    public string? OAuth2RefreshToken { get; set; }

    [JsonPropertyName("smtp_host")]
    public string? SmtpHost { get; set; }

    [JsonPropertyName("smtp_port")]
    public int SmtpPort { get; set; } = 587;

    [JsonPropertyName("smtp_use_ssl")]
    public bool SmtpUseSsl { get; set; } = false;

    [JsonPropertyName("confirm_mode")]
    public string ConfirmMode { get; set; } = "implicit";

    [JsonPropertyName("undo_window_seconds")]
    public int UndoWindowSeconds { get; set; } = 10;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("sync")]
    public SyncConfig Sync { get; set; } = new();
}

/// <summary>Sync behaviour for an account.</summary>
public class SyncConfig
{
    [JsonPropertyName("idle_folders")]
    public List<string> IdleFolders { get; set; } = [];

    [JsonPropertyName("poll_interval")]
    public int PollInterval { get; set; } = 300;

    [JsonPropertyName("interval_minutes")]
    public int IntervalMinutes { get; set; } = 5;

    [JsonPropertyName("max_messages_per_sync")]
    public int MaxMessagesPerSync { get; set; } = 500;

    [JsonPropertyName("folders")]
    public List<FolderSyncConfig> Folders { get; set; } = [];
}

/// <summary>Per-folder sync configuration.</summary>
public class FolderSyncConfig
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("cache_window_days")]
    public int CacheWindowDays { get; set; } = 0;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("max_age_days")]
    public int? MaxAgeDays { get; set; }
}

/// <summary>SQLite cache configuration.</summary>
public class CacheConfig
{
    [JsonPropertyName("db_path")]
    public string DbPath { get; set; } = "~/.ultimate-imap-mcp/cache.db";

    [JsonPropertyName("max_size_mb")]
    public int MaxSizeMb { get; set; } = 500;

    [JsonPropertyName("default_window_days")]
    public int DefaultWindowDays { get; set; } = 0;

    [JsonPropertyName("max_body_age_days")]
    public int MaxBodyAgeDays { get; set; } = 0;

    [JsonPropertyName("imap_fallback_ttl_hours")]
    public int ImapFallbackTtlHours { get; set; } = 1;

    [JsonPropertyName("vacuum_on_startup")]
    public bool VacuumOnStartup { get; set; } = false;

    [JsonPropertyName("cleanup_server_deleted")]
    public bool CleanupServerDeleted { get; set; } = true;

    [JsonPropertyName("deleted_retention_days")]
    public int DeletedRetentionDays { get; set; } = 30;
}

/// <summary>Global sync defaults (used when per-account sync config is not set).</summary>
public class GlobalSyncConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("poll_interval")]
    public int PollInterval { get; set; } = 300;

    [JsonPropertyName("max_messages_per_sync")]
    public int MaxMessagesPerSync { get; set; } = 500;
}

/// <summary>Async operation queue settings.</summary>
public class QueueConfig
{
    [JsonPropertyName("p0_flush_interval")]
    public int P0FlushInterval { get; set; } = 2;

    [JsonPropertyName("p1_flush_interval")]
    public int P1FlushInterval { get; set; } = 30;

    [JsonPropertyName("p2_flush_interval")]
    public int P2FlushInterval { get; set; } = 300;

    [JsonPropertyName("send_undo_window")]
    public int SendUndoWindow { get; set; } = 10;

    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; set; } = 3;

    [JsonPropertyName("max_concurrent_operations")]
    public int MaxConcurrentOperations { get; set; } = 3;

    [JsonPropertyName("retry_attempts")]
    public int RetryAttempts { get; set; } = 3;

    [JsonPropertyName("retry_delay_seconds")]
    public int RetryDelaySeconds { get; set; } = 5;
}

/// <summary>Optional LLM integration settings.</summary>
public class LlmConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "openai";

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("api_key_env")]
    public string? ApiKeyEnv { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4o-mini";

    [JsonPropertyName("daily_token_budget")]
    public int DailyTokenBudget { get; set; } = 0;

    [JsonPropertyName("monthly_cost_limit")]
    public double MonthlyCostLimit { get; set; } = 0;

    [JsonPropertyName("auto_analyze_new")]
    public bool AutoAnalyzeNew { get; set; } = false;

    [JsonPropertyName("provider_api_keys")]
    public Dictionary<string, string> ProviderApiKeys { get; set; } = new();

    [JsonPropertyName("analysis_prompts")]
    public Dictionary<string, string> AnalysisPrompts { get; set; } = new();

    [JsonPropertyName("acp")]
    public AcpConfig Acp { get; set; } = new();

    /// <summary>Resolve the API key from config or environment variable, optionally checking provider-specific keys first.</summary>
    public string? ResolveApiKey(string? provider = null)
    {
        // Check provider-specific key first (case-insensitive lookup)
        if (provider is not null)
        {
            foreach (var kvp in ProviderApiKeys)
            {
                if (string.Equals(kvp.Key, provider, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(kvp.Value) && kvp.Value != "***")
                    return kvp.Value;
            }
        }

        // Fall back to global key
        if (!string.IsNullOrEmpty(ApiKey)) return ApiKey;
        if (!string.IsNullOrEmpty(ApiKeyEnv))
        {
            var envValue = Environment.GetEnvironmentVariable(ApiKeyEnv);
            if (!string.IsNullOrEmpty(envValue)) return envValue;
        }
        return null;
    }
}

/// <summary>Agent Client Protocol configuration for spawning LLM agents.</summary>
public class AcpConfig
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "claude";

    [JsonPropertyName("pool_size")]
    public int PoolSize { get; set; } = 2;

    [JsonPropertyName("request_timeout_seconds")]
    public int RequestTimeoutSeconds { get; set; } = 60;

    [JsonPropertyName("claude")]
    public AcpProviderConfig Claude { get; set; } = new()
    {
        Command = "npx",
        Args = ["--yes", "claude-code-acp"],
    };

    [JsonPropertyName("copilot")]
    public AcpProviderConfig Copilot { get; set; } = new()
    {
        Command = "gh",
        Args = ["copilot", "--acp"],
    };

    /// <summary>Legacy command field — if set, overrides claude provider command.</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = "claude";

    /// <summary>Legacy args field — if set, overrides claude provider args.</summary>
    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = ["--acp"];
}

public class AcpProviderConfig
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = [];
}

/// <summary>Prometheus/metrics endpoint settings.</summary>
public class MetricsConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 9090;

    [JsonPropertyName("path")]
    public string Path { get; set; } = "/metrics";

    [JsonPropertyName("internal_retention_days")]
    public int InternalRetentionDays { get; set; } = 7;

    [JsonPropertyName("otlp_endpoint")]
    public string? OtlpEndpoint { get; set; }

    [JsonPropertyName("otlp_protocol")]
    public string OtlpProtocol { get; set; } = "grpc";

    [JsonPropertyName("export_interval_seconds")]
    public int ExportIntervalSeconds { get; set; } = 15;
}

/// <summary>Configuration for an OAuth2 provider (e.g. Gmail, Outlook).</summary>
public class OAuthProviderConfig
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; set; }

    [JsonPropertyName("auth_url")]
    public string? AuthUrl { get; set; }

    [JsonPropertyName("token_url")]
    public string? TokenUrl { get; set; }

    [JsonPropertyName("scopes")]
    public List<string>? Scopes { get; set; }
}

/// <summary>Label vocabulary configuration.</summary>
public class LabelsConfig
{
    [JsonPropertyName("allow_cli_edits")]
    public bool AllowCliEdits { get; set; } = true;

    [JsonPropertyName("items")]
    public List<LabelDefinition> Items { get; set; } = [];
}

/// <summary>A single label definition in the vocabulary.</summary>
public class LabelDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
}
