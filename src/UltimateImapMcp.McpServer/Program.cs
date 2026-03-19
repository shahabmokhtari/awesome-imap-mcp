using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.Providers;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Dashboard;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Executors;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Load config
var configPath = args.FirstOrDefault(a => a.StartsWith("--config="))?.Split('=', 2)[1]
    ?? args.SkipWhile(a => a != "--config").Skip(1).FirstOrDefault()
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ultimate-imap-mcp", "config.json");

AppConfig config = File.Exists(configPath) ? ConfigLoader.LoadFromFile(configPath) : new AppConfig();

var dbPath = ConfigLoader.ResolveDbPath(config.Cache.DbPath);
var database = new AppDatabase(dbPath);
MigrationRunner.Migrate(database);

var passphrase = Environment.GetEnvironmentVariable("UIMAP_PASSPHRASE");
var encryptor = passphrase != null ? new CredentialEncryptor(passphrase) : CredentialEncryptor.FromMachineId();

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(encryptor);
builder.Services.AddSingleton<ProviderProfileRegistry>();
builder.Services.AddSingleton<AccountRepository>();
builder.Services.AddSingleton<FolderRepository>();
builder.Services.AddSingleton<MessageRepository>();
builder.Services.AddSingleton<AttachmentRepository>();

// Sync
builder.Services.AddSingleton<SyncLogRepository>();
builder.Services.AddSingleton<ImapSyncService>();
builder.Services.AddSingleton(config.Cache);
builder.Services.AddSingleton<SyncManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncManager>());
builder.Services.AddHostedService<CacheEvictor>();

// Queue
builder.Services.AddSingleton<QueueRepository>();
builder.Services.AddSingleton<QueueManager>();
builder.Services.AddSingleton(config.Queue);
builder.Services.AddSingleton<IOperationExecutor, SendExecutor>();
builder.Services.AddSingleton<IOperationExecutor, DeleteExecutor>();
builder.Services.AddSingleton<IOperationExecutor, MoveExecutor>();
builder.Services.AddSingleton<IOperationExecutor, FlagExecutor>();
builder.Services.AddHostedService<QueueWorker>();

// Dashboard (conditional)
if (config.Server.DashboardEnabled)
{
    builder.Services.AddDashboard(config);
}

builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new() { Name = "ultimate-imap-mcp", Version = "0.1.0" };
})
.WithStdioServerTransport()
.WithToolsFromAssembly();

await builder.Build().RunAsync();
