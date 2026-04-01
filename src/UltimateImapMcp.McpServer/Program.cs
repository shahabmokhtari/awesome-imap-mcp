using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.Coordination;
using UltimateImapMcp.Core.Logging;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.Core.Providers;
using UltimateImapMcp.Core.Repositories;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Dashboard;
using UltimateImapMcp.Llm;
using UltimateImapMcp.Llm.Acp;
using UltimateImapMcp.Llm.Repositories;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Executors;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.RestBackend;
using UltimateImapMcp.RestBackend.Zoho;
using UltimateImapMcp.McpServer;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Generate a unique instance ID for this server process
var instanceId = $"{Environment.MachineName}-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddTHHmmss}";
builder.Services.AddSingleton(new InstanceInfo(instanceId));

// Load config
// Parse CLI arguments
static string? GetArg(string[] args, string name)
{
    var prefixed = args.FirstOrDefault(a => a.StartsWith($"--{name}="))?.Split('=', 2)[1];
    if (prefixed is not null) return prefixed;
    return args.SkipWhile(a => a != $"--{name}").Skip(1).FirstOrDefault();
}

var configPath = GetArg(args, "config")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ultimate-imap-mcp", "config.json");

AppConfig config;
if (File.Exists(configPath))
{
    config = ConfigLoader.LoadFromFile(configPath);
}
else
{
    Console.Error.WriteLine($"Warning: Config file not found at {configPath}. Starting with defaults.");
    config = new AppConfig();
}

config.SourcePath = Path.GetFullPath(configPath);

// CLI overrides
if (GetArg(args, "port") is { } portStr && int.TryParse(portStr, out var port))
    config.Server.HttpPort = port;
if (GetArg(args, "dashboard-port") is { } dashPortStr && int.TryParse(dashPortStr, out var dashPort))
    config.Server.DashboardPort = dashPort;
if (GetArg(args, "transport") is { } transportArg)
    config.Server.Transport = transportArg;
if (args.Contains("--dashboard"))
    config.Server.DashboardEnabled = true;
if (args.Contains("--dashboard-auto-open"))
{
    config.Server.DashboardEnabled = true;
    config.Server.DashboardAutoOpen = true;
}

var dbPath = ConfigLoader.ResolveDbPath(config.Cache.DbPath);
var dbDir = Path.GetDirectoryName(dbPath)!;
var database = new AppDatabase(dbPath);
MigrationRunner.Migrate(database);

var healthDbPath = Path.Combine(dbDir, "health.db");
var healthDatabase = new HealthDatabase(healthDbPath);

var metricsDb = new MetricsDatabase(Path.Combine(dbDir, "metrics.db"));
var logsDb = new LogsDatabase(Path.Combine(dbDir, "logs.db"));
builder.Services.AddSingleton(healthDatabase);

builder.Services.AddSingleton<Func<int>>(sp =>
{
    var repo = sp.GetRequiredService<AccountRepository>();
    return () => { try { return repo.GetAll().Count; } catch (Exception ex) { Console.Error.WriteLine($"[Heartbeat] Account count failed: {ex.Message}"); return 0; } };
});
builder.Services.AddSingleton<IInstanceCoordinator, InstanceCoordinator>();
builder.Services.AddHostedService(sp => (InstanceCoordinator)sp.GetRequiredService<IInstanceCoordinator>());

// Create the shared accounts store (accounts.json, same directory as config.json)
var accountsPath = AccountsStore.ResolveAccountsPath(config.SourcePath);
var accountsStore = new AccountsStore(accountsPath);

// Clean up orphaned OAuth tokens from previously deleted accounts
new OAuthTokenRepository(accountsStore).CleanOrphans();

var passphrase = Environment.GetEnvironmentVariable("UIMAP_PASSPHRASE");
var encryptor = passphrase != null ? new CredentialEncryptor(passphrase) : CredentialEncryptor.FromMachineId();

// --- Import config-file accounts into accounts.json (seed data) ---
{
    var accountRepo = new AccountRepository(accountsStore);
    var imported = 0;
    foreach (var acct in config.Accounts)
    {
        // Use a deterministic ID derived from the account name
        var id = AccountConfigMapper.DeriveIdFromName(acct.Name);

        // Skip if an account with that name already exists in the DB
        if (accountRepo.GetByName(acct.Name) is not null)
            continue;

        // Also skip if an account with the derived ID already exists
        if (accountRepo.GetById(id) is not null)
            continue;

        // Encrypt the password for DB storage
        var credEnc = acct.AuthType.Equals("oauth2", StringComparison.OrdinalIgnoreCase)
            ? encryptor.Encrypt("oauth2")
            : encryptor.Encrypt(acct.Password ?? "");

        // Serialize sync config + queue settings into config_json for the DB record
        var configJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            Sync = acct.Sync,
            confirm_mode = acct.ConfirmMode,
            undo_window_seconds = acct.UndoWindowSeconds
        });

        accountRepo.Insert(id, acct.Name, acct.ImapHost, acct.ImapPort,
            acct.SmtpHost, acct.SmtpPort, acct.SmtpUseSsl,
            acct.Username, acct.AuthType, credEnc, acct.Provider, configJson);

        // Respect the enabled field from config (defaults to true)
        if (!acct.Enabled)
            accountRepo.SetEnabled(id, false);

        if (string.IsNullOrEmpty(acct.Password) && !acct.AuthType.Equals("oauth2", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"  WARNING: Account '{acct.Name}' has no password. Set the env var or update via dashboard.");
        }

        imported++;
        Console.Error.WriteLine($"  Imported config account '{acct.Name}' into accounts.json (id: {id})");
    }
    if (imported > 0)
        Console.Error.WriteLine($"  Imported {imported} config account(s) into accounts.json.");
}

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(accountsStore);
builder.Services.AddSingleton(encryptor);
builder.Services.AddSingleton<ProviderProfileRegistry>();
builder.Services.AddSingleton<AccountRepository>();
builder.Services.AddSingleton<FolderRepository>();
builder.Services.AddSingleton<MessageRepository>();
builder.Services.AddSingleton<AttachmentRepository>();

// OAuth
builder.Services.AddHttpClient();
builder.Services.AddSingleton<OAuthTokenRepository>();
builder.Services.AddSingleton<OAuthTokenService>();
builder.Services.AddSingleton<OAuthStateStore>();
builder.Services.AddSingleton<IOAuthAccessTokenProvider, OAuthAccessTokenProvider>();
builder.Services.AddHostedService<OAuthTokenRefreshService>();

// Sync
builder.Services.AddSingleton<SyncLogRepository>();
builder.Services.AddSingleton<ImapSyncService>();
builder.Services.AddSingleton(config.Cache);
builder.Services.AddSingleton<SyncManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncManager>());
builder.Services.AddHostedService<CacheEvictor>();

// Email backend abstraction (routes to IMAP or REST per account)
builder.Services.AddSingleton<IEmailBackendFactory, CompositeBackendFactory>();
builder.Services.AddHttpClient("ZohoMail");
builder.Services.AddHostedService<ZohoSyncService>();

// Queue
builder.Services.AddSingleton<QueueRepository>();
builder.Services.AddSingleton<QueueManager>();
builder.Services.AddSingleton(config.Queue);
builder.Services.AddSingleton<IOperationExecutor, SendExecutor>();
builder.Services.AddSingleton<IOperationExecutor, DeleteExecutor>();
builder.Services.AddSingleton<IOperationExecutor, MoveExecutor>();
builder.Services.AddSingleton<IOperationExecutor, FlagExecutor>();
builder.Services.AddSingleton<IOperationExecutor, LabelExecutor>();
builder.Services.AddHostedService<QueueWorker>();

// LLM Analysis
builder.Services.AddSingleton(config.Llm);
builder.Services.AddSingleton<LlmUsageRepository>();
builder.Services.AddSingleton<LlmAnalysisRepository>();
builder.Services.AddSingleton<BudgetTracker>();

// Register ACP pool as singleton (shared across email analyzer and dashboard)
if (config.Llm.Enabled && config.Llm.Provider.StartsWith("acp_", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAcpClientPool>(sp =>
        new AcpClientPool(
            config.Llm.Acp,
            sp.GetRequiredService<ILoggerFactory>()));
}

builder.Services.AddSingleton<IEmailAnalyzer>(sp =>
{
    var llmConfig = sp.GetRequiredService<LlmConfig>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    var prompts = llmConfig.AnalysisPrompts.Count > 0 ? llmConfig.AnalysisPrompts : null;

    return llmConfig.Provider.ToLowerInvariant() switch
    {
        "anthropic" or "openai" when llmConfig.Enabled =>
            new ApiEmailAnalyzer(
                ChatClientFactory.Create(llmConfig),
                llmConfig.Model,
                loggerFactory.CreateLogger<ApiEmailAnalyzer>(),
                prompts),

        var p when (p is "acp_claude" or "acp_copilot") && llmConfig.Enabled =>
            new AcpEmailAnalyzer(
                sp.GetRequiredService<IAcpClientPool>(),
                loggerFactory.CreateLogger<AcpEmailAnalyzer>(),
                prompts),

        "in_context" => new InContextAnalyzer(),

        _ => new InContextAnalyzer()
    };
});

// Observability — separate databases for metrics and logs
builder.Services.AddSingleton(metricsDb);
builder.Services.AddSingleton(new MetricsRepository(metricsDb));
var logsRepository = new LogsRepository(logsDb);
builder.Services.AddSingleton(logsDb);
builder.Services.AddSingleton(logsRepository);
builder.Services.AddSingleton(config.Metrics);
builder.Services.AddHostedService<MetricsCollector>();

// SQLite log sink — shares the same LogsRepository instance registered in DI
builder.Logging.AddProvider(new SqliteLoggerProvider(logsRepository, instanceId));

// File log sink — writes logs to files organized by scope
{
    var logDir = config.Server.LogDir
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ultimate-imap-mcp", "logs");
    builder.Logging.AddProvider(new FileLoggerProvider(logDir, instanceId, config.Server.LogDirMaxSizeMb));
}

var transport = config.Server.Transport;

// Wrap stdio streams with protocol logger when verbose logging is enabled
if (config.Server.LogProtocol && transport is "stdio" or "both")
{
    var protocolLogDir = config.Server.LogDir
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ultimate-imap-mcp", "logs");
    var protocolLogger = LoggerFactory.Create(lb =>
    {
        lb.SetMinimumLevel(LogLevel.Debug);
        lb.AddProvider(new FileLoggerProvider(protocolLogDir, instanceId, config.Server.LogDirMaxSizeMb));
    }).CreateLogger("MCP.Protocol");

    var wrappedIn = new McpProtocolLogger(Console.OpenStandardInput(), protocolLogger, "IN");
    var wrappedOut = new McpProtocolLogger(Console.OpenStandardOutput(), protocolLogger, "OUT");
    Console.SetIn(new StreamReader(wrappedIn));
    Console.SetOut(new StreamWriter(wrappedOut) { AutoFlush = true });
}

// Dashboard (conditional) — DashboardHost handles port standby internally
if (config.Server.DashboardEnabled)
{
    builder.Services.AddDashboard(config);
}

if (transport is "stdio" or "both")
{
    builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "ultimate-imap-mcp", Version = "0.1.0" };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
}

if (transport is "http" or "both")
{
    builder.Services.AddSingleton<UltimateImapMcp.McpServer.HttpMcpTransportHost>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<UltimateImapMcp.McpServer.HttpMcpTransportHost>());
}

var host = builder.Build();

// Wire dashboard active status to coordinator
{
    var coordinator = host.Services.GetRequiredService<IInstanceCoordinator>() as InstanceCoordinator;
    var dashboardHost = host.Services.GetServices<IHostedService>()
        .OfType<UltimateImapMcp.Dashboard.DashboardHost>().FirstOrDefault();
    if (coordinator is not null && dashboardHost is not null)
        coordinator.SetDashboardActiveProvider(() => dashboardHost.IsActivelyServing);
}

// Wire sync events to the dashboard event bus (if dashboard is enabled)
{
    var syncMgr = host.Services.GetRequiredService<SyncManager>();
    var evtBus = host.Services.GetService<IEventBus>();
    if (evtBus is not null)
    {
        syncMgr.SetEventCallback((method, data) =>
        {
            switch (method)
            {
                case "sync:progress":
                    evtBus.Publish(new SyncProgressEvent(
                        GetProp<string>(data, "accountId"),
                        GetProp<string>(data, "folder"),
                        0,
                        GetProp<int>(data, "totalFolders")));
                    break;
                case "sync:complete":
                    evtBus.Publish(new SyncCompleteEvent(
                        GetProp<string>(data, "accountId"),
                        GetProp<string>(data, "folder"),
                        GetProp<int>(data, "messagesSynced")));
                    break;
                case "sync:error":
                    evtBus.Publish(new SyncErrorEvent(
                        GetProp<string>(data, "accountId"),
                        GetProp<string>(data, "folder"),
                        GetProp<string>(data, "error")));
                    break;
            }
        });
    }
}

// Helper to extract properties from anonymous objects via reflection
static T GetProp<T>(object obj, string name)
{
    var prop = obj.GetType().GetProperty(name);
    return prop is not null ? (T)prop.GetValue(obj)! : default!;
}

// Repair orphaned messages missing junction table entries (one-time fix on startup)
{
    var repairRepo = new MessageRepository(database);
    var repaired = repairRepo.RepairMissingFolderLinks();
    if (repaired > 0)
        Console.Error.WriteLine($"  [Startup] Repaired {repaired} messages with missing folder links.");
}

// Print startup banner to stderr (stdout is reserved for MCP stdio protocol)
{
    var bannerRepo = new AccountRepository(accountsStore);
    var dbAccounts = bannerRepo.GetAll();
    PrintStartupBanner(config, configPath, dbAccounts);
}

await host.RunAsync();

static void PrintStartupBanner(AppConfig config, string configPath,
    List<AccountRecord> dbAccounts)
{
    var w = Console.Error;
    w.WriteLine();
    w.WriteLine("  Ultimate IMAP MCP Server v0.1.0");
    w.WriteLine("  ================================");
    w.WriteLine();
    w.WriteLine($"  Config:     {configPath}");
    w.WriteLine($"  Database:   {ConfigLoader.ResolveDbPath(config.Cache.DbPath)}");
    w.WriteLine($"  Accounts:   {dbAccounts.Count} in database ({string.Join(", ", dbAccounts.Select(a => a.Name))})");
    if (config.Accounts.Count > 0)
        w.WriteLine($"              ({config.Accounts.Count} in config file — used as seed data)");
    w.WriteLine();

    // Transport info
    var transport = config.Server.Transport;
    w.WriteLine("  Transport");
    if (transport is "stdio" or "both")
        w.WriteLine("    stdio:     enabled (connect via MCP client)");
    if (transport is "http" or "both")
        w.WriteLine($"    HTTP+SSE:  http://localhost:{config.Server.HttpPort}/sse");
    if (transport is "stdio")
        w.WriteLine("    HTTP+SSE:  disabled");
    w.WriteLine();

    // Dashboard info
    if (config.Server.DashboardEnabled)
    {
        w.Write($"  Dashboard:   http://localhost:{config.Server.DashboardPort}");
        if (config.Server.DashboardAuth == "pin")
            w.Write(" (PIN protected)");
        w.WriteLine();
    }
    else
    {
        w.WriteLine("  Dashboard:   disabled");
    }

    // LLM info
    if (config.Llm.Enabled)
        w.WriteLine($"  LLM:         {config.Llm.Provider} / {config.Llm.Model}");
    else
        w.WriteLine("  LLM:         disabled");

    // Metrics info
    if (config.Metrics.Enabled)
        w.WriteLine($"  Metrics:     http://localhost:{config.Metrics.Port}{config.Metrics.Path}");

    w.WriteLine();

    // Connection instructions
    w.WriteLine("  Connect:");
    if (transport is "stdio" or "both")
        w.WriteLine("    claude mcp add ultimate-imap-mcp -- ultimate-imap-mcp");
    if (transport is "http" or "both")
        w.WriteLine($"    MCP client -> http://localhost:{config.Server.HttpPort}/sse");
    w.WriteLine();

    // Multi-client hint for stdio-only users
    if (transport is "stdio")
    {
        w.WriteLine("  Tip: To share one server across multiple AI clients, use HTTP transport:");
        w.WriteLine("    ultimate-imap-mcp --transport both");
        w.WriteLine();
    }
}
