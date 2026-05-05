using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public class AccountTools(AccountRepository accountRepo, AppConfig config, ILogger<AccountTools> logger)
{
    [McpServerTool, Description(
        "List all configured email accounts with connection status, message counts, and sync state. " +
        "Disabled accounts are included with enabled=false.")]
    public string ListAccounts()
    {
        return McpJsonDefaults.LogToolCall(logger, "list_accounts",
            new Dictionary<string, object?>(),
            () =>
            {
                try
                {
                    var accounts = accountRepo.GetAll()
                        .Select(a => new
                        {
                            id = a.Id,
                            name = a.Name,
                            imap_host = a.ImapHost,
                            username = a.Username,
                            provider = a.Provider,
                            enabled = a.Enabled
                        })
                        .ToList();
                    return JsonSerializer.Serialize(accounts, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "ListAccounts failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description(
        "Get detailed status for an email account including IMAP/SMTP config, sync progress, and folder counts.")]
    public string GetAccountStatus([Description("Account ID or name")] string accountId)
    {
        return McpJsonDefaults.LogToolCall(logger, "get_account_status",
            new Dictionary<string, object?> { ["accountId"] = accountId },
            () =>
            {
                try
                {
                    var account = accountRepo.ResolveAccount(accountId);
                    if (account is null)
                        return McpJsonDefaults.Error($"Account '{accountId}' not found.");

                    return JsonSerializer.Serialize(new
                    {
                        id = account.Id,
                        name = account.Name,
                        imap_host = account.ImapHost,
                        imap_port = account.ImapPort,
                        username = account.Username,
                        provider = account.Provider,
                        auth_type = account.AuthType,
                        enabled = account.Enabled,
                        created_at = account.CreatedAt
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "GetAccountStatus failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }
}
