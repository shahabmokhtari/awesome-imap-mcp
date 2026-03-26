using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.Dashboard;

public static class SettingsApi
{
    public static IEndpointRouteBuilder MapSettingsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", (AppConfig config, AccountRepository accountRepo) =>
        {
            // Return config with sensitive fields redacted
            return Results.Ok(new
            {
                server = new
                {
                    config.Server.Transport,
                    config.Server.HttpPort,
                    config.Server.DashboardPort,
                    config.Server.DashboardEnabled,
                    config.Server.DashboardAuth,
                    config.Server.DashboardAutoOpen,
                    config.Server.LogLevel
                },
                cache = new
                {
                    config.Cache.DbPath,
                    config.Cache.MaxSizeMb,
                    config.Cache.DefaultWindowDays,
                    config.Cache.MaxBodyAgeDays,
                    config.Cache.ImapFallbackTtlHours
                },
                queue = new
                {
                    config.Queue.P0FlushInterval,
                    config.Queue.P1FlushInterval,
                    config.Queue.P2FlushInterval,
                    config.Queue.SendUndoWindow,
                    config.Queue.MaxRetries
                },
                llm = new
                {
                    config.Llm.Enabled,
                    config.Llm.Provider,
                    config.Llm.Model,
                    config.Llm.DailyTokenBudget,
                    config.Llm.MonthlyCostLimit,
                    config.Llm.AutoAnalyzeNew,
                    ProviderApiKeys = config.Llm.ProviderApiKeys.ToDictionary(
                        kvp => kvp.Key,
                        kvp => string.IsNullOrEmpty(kvp.Value) ? "" : "***")
                },
                sync = new
                {
                    config.Sync.Enabled,
                    config.Sync.PollInterval,
                    config.Sync.MaxMessagesPerSync
                },
                metrics = new
                {
                    config.Metrics.Enabled,
                    config.Metrics.Port,
                    config.Metrics.Path,
                    config.Metrics.InternalRetentionDays
                },
                accountCount = accountRepo.GetAll().Count
            });
        });

        app.MapPut("/api/settings", async (HttpContext ctx, AppConfig config) =>
        {
            var updates = await ctx.Request.ReadFromJsonAsync<SettingsUpdateRequest>().ConfigureAwait(false);
            if (updates is null)
                return Results.BadRequest(new { error = "Invalid request body" });

            var changed = new List<string>();

            // Server settings
            if (updates.Server is { } s)
            {
                if (s.Transport is not null) { config.Server.Transport = s.Transport; changed.Add("transport"); }
                if (s.HttpPort is { } hp) { config.Server.HttpPort = hp; changed.Add("http_port"); }
                if (s.DashboardPort is { } dp) { config.Server.DashboardPort = dp; changed.Add("dashboard_port"); }
                if (s.DashboardEnabled is { } de) { config.Server.DashboardEnabled = de; changed.Add("dashboard_enabled"); }
                if (s.DashboardAuth is not null) { config.Server.DashboardAuth = s.DashboardAuth; changed.Add("dashboard_auth"); }
                if (s.DashboardAutoOpen is { } dao) { config.Server.DashboardAutoOpen = dao; changed.Add("dashboard_auto_open"); }
                if (s.LogLevel is not null) { config.Server.LogLevel = s.LogLevel; changed.Add("log_level"); }
            }

            // Cache settings
            if (updates.Cache is { } c)
            {
                if (c.MaxSizeMb is { } ms) { config.Cache.MaxSizeMb = ms; changed.Add("max_size_mb"); }
                if (c.DefaultWindowDays is { } dw) { config.Cache.DefaultWindowDays = dw; changed.Add("default_window_days"); }
                if (c.MaxBodyAgeDays is { } mba) { config.Cache.MaxBodyAgeDays = mba; changed.Add("max_body_age_days"); }
                if (c.ImapFallbackTtlHours is { } ift) { config.Cache.ImapFallbackTtlHours = ift; changed.Add("imap_fallback_ttl_hours"); }
            }

            // Queue settings
            if (updates.Queue is { } q)
            {
                if (q.P0FlushInterval is { } p0) { config.Queue.P0FlushInterval = p0; changed.Add("p0_flush_interval"); }
                if (q.P1FlushInterval is { } p1) { config.Queue.P1FlushInterval = p1; changed.Add("p1_flush_interval"); }
                if (q.P2FlushInterval is { } p2) { config.Queue.P2FlushInterval = p2; changed.Add("p2_flush_interval"); }
                if (q.SendUndoWindow is { } su) { config.Queue.SendUndoWindow = su; changed.Add("send_undo_window"); }
                if (q.MaxRetries is { } mr) { config.Queue.MaxRetries = mr; changed.Add("max_retries"); }
            }

            // LLM settings
            if (updates.Llm is { } l)
            {
                if (l.Enabled is { } le) { config.Llm.Enabled = le; changed.Add("llm.enabled"); }
                if (l.Provider is not null) { config.Llm.Provider = l.Provider; changed.Add("llm.provider"); }
                if (l.Model is not null) { config.Llm.Model = l.Model; changed.Add("llm.model"); }
                if (l.DailyTokenBudget is { } dtb) { config.Llm.DailyTokenBudget = dtb; changed.Add("llm.daily_token_budget"); }
                if (l.MonthlyCostLimit is { } mcl) { config.Llm.MonthlyCostLimit = mcl; changed.Add("llm.monthly_cost_limit"); }
                if (l.AutoAnalyzeNew is { } aan) { config.Llm.AutoAnalyzeNew = aan; changed.Add("llm.auto_analyze_new"); }
                if (l.ProviderApiKeys is not null)
                {
                    foreach (var (provider, key) in l.ProviderApiKeys)
                    {
                        if (key == "***") continue; // Skip unchanged keys
                        config.Llm.ProviderApiKeys[provider] = key;
                    }
                    changed.Add("llm.provider_api_keys");
                }
            }

            // Sync settings
            if (updates.Sync is { } sy)
            {
                if (sy.Enabled is { } se) { config.Sync.Enabled = se; changed.Add("sync.enabled"); }
                if (sy.PollInterval is { } pi) { config.Sync.PollInterval = pi; changed.Add("sync.poll_interval"); }
                if (sy.MaxMessagesPerSync is { } mms) { config.Sync.MaxMessagesPerSync = mms; changed.Add("sync.max_messages_per_sync"); }
            }

            // Metrics settings
            if (updates.Metrics is { } m)
            {
                if (m.Enabled is { } me) { config.Metrics.Enabled = me; changed.Add("metrics.enabled"); }
                if (m.InternalRetentionDays is { } ird) { config.Metrics.InternalRetentionDays = ird; changed.Add("metrics.internal_retention_days"); }
            }

            // Persist to disk if we know the source file
            var persisted = false;
            if (config.SourcePath is not null && changed.Count > 0)
            {
                try
                {
                    ConfigLoader.SaveToFile(config, config.SourcePath);
                    persisted = true;
                }
                catch (Exception ex)
                {
                    return Results.Ok(new
                    {
                        updated = changed,
                        persisted = false,
                        warning = $"Settings applied in-memory but could not save to disk: {ex.Message}. Some changes may require a restart."
                    });
                }
            }

            return Results.Ok(new
            {
                updated = changed,
                persisted,
                message = changed.Count > 0
                    ? "Settings updated. Some changes (ports, transport) require a restart to take effect."
                    : "No changes applied."
            });
        });

        return app;
    }
}

// Request DTOs with nullable fields — only provided fields are applied
file record SettingsUpdateRequest
{
    public ServerSettingsUpdate? Server { get; init; }
    public CacheSettingsUpdate? Cache { get; init; }
    public QueueSettingsUpdate? Queue { get; init; }
    public SyncSettingsUpdate? Sync { get; init; }
    public LlmSettingsUpdate? Llm { get; init; }
    public MetricsSettingsUpdate? Metrics { get; init; }
}

file record ServerSettingsUpdate
{
    public string? Transport { get; init; }
    public int? HttpPort { get; init; }
    public int? DashboardPort { get; init; }
    public bool? DashboardEnabled { get; init; }
    public string? DashboardAuth { get; init; }
    public bool? DashboardAutoOpen { get; init; }
    public string? LogLevel { get; init; }
}

file record CacheSettingsUpdate
{
    public int? MaxSizeMb { get; init; }
    public int? DefaultWindowDays { get; init; }
    public int? MaxBodyAgeDays { get; init; }
    public int? ImapFallbackTtlHours { get; init; }
}

file record QueueSettingsUpdate
{
    public int? P0FlushInterval { get; init; }
    public int? P1FlushInterval { get; init; }
    public int? P2FlushInterval { get; init; }
    public int? SendUndoWindow { get; init; }
    public int? MaxRetries { get; init; }
}

file record LlmSettingsUpdate
{
    public bool? Enabled { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public int? DailyTokenBudget { get; init; }
    public double? MonthlyCostLimit { get; init; }
    public bool? AutoAnalyzeNew { get; init; }
    public Dictionary<string, string>? ProviderApiKeys { get; init; }
}

file record SyncSettingsUpdate
{
    public bool? Enabled { get; init; }
    public int? PollInterval { get; init; }
    public int? MaxMessagesPerSync { get; init; }
}

file record MetricsSettingsUpdate
{
    public bool? Enabled { get; init; }
    public int? InternalRetentionDays { get; init; }
}
