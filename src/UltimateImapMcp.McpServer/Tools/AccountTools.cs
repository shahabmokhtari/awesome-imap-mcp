using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class AccountTools(AccountRepository accountRepo)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description("List all configured email accounts and their status.")]
    public string ListAccounts()
    {
        var accounts = accountRepo.GetAll();
        return JsonSerializer.Serialize(accounts, JsonOptions);
    }

    [McpServerTool, Description("Get detailed status for a specific email account.")]
    public string GetAccountStatus([Description("Account ID")] string accountId)
    {
        var account = accountRepo.GetById(accountId);
        if (account is null)
            return JsonSerializer.Serialize(new { error = $"Account '{accountId}' not found." }, JsonOptions);

        return JsonSerializer.Serialize(account, JsonOptions);
    }
}
