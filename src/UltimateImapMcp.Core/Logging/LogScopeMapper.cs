namespace UltimateImapMcp.Core.Logging;

/// <summary>
/// Maps .NET logger category names to user-friendly scope labels
/// used for filtering in the dashboard and file-based logs.
/// </summary>
public static class LogScopeMapper
{
    public static string MapCategoryToScope(string category) => category switch
    {
        _ when category.Contains("SyncManager") || category.Contains("ImapConnection") ||
               category.Contains("SmtpConnection") || category.Contains("CacheEvictor") ||
               category.Contains("ImapSyncService") => "mail",
        _ when category.Contains("AccountRepository") || category.Contains("AccountsApi") ||
               category.Contains("OAuth") => "accounts",
        _ when category.Contains("AspNetCore") || category.Contains("Dashboard") ||
               category.Contains("SettingsApi") || category.Contains("QueueApi") ||
               category.Contains("SyncApi") || category.Contains("LogsApi") ||
               category.Contains("MetricsApi") || category.Contains("AuthApi") ||
               category.Contains("PinAuth") => "api",
        _ when category.Contains("ModelContextProtocol") || category.Contains("McpServer") ||
               category.Contains("HttpMcpTransport") || category.Contains("Tools") => "mcp",
        _ when category.Contains("QueueWorker") || category.Contains("QueueManager") ||
               category.Contains("Executor") => "queue",
        _ => "system"
    };
}
