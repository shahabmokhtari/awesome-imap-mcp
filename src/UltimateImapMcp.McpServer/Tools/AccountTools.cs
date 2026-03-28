using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class AccountTools(AccountRepository accountRepo)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description(
        "List all configured email accounts with connection status, message counts, and sync state. " +
        "Disabled accounts are included with enabled=false.")]
    public string ListAccounts()
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
        return JsonSerializer.Serialize(accounts, JsonOptions);
    }

    [McpServerTool, Description(
        "Get detailed status for an email account including IMAP/SMTP config, sync progress, and folder counts.")]
    public string GetAccountStatus([Description("Account ID or name")] string accountId)
    {
        var account = accountRepo.ResolveAccount(accountId);
        if (account is null)
            return JsonSerializer.Serialize(new { error = $"Account '{accountId}' not found." }, JsonOptions);

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
        }, JsonOptions);
    }
}
