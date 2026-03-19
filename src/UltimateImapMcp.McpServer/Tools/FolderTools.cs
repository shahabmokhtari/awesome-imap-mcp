using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class FolderTools(FolderRepository folderRepo, MessageRepository messageRepo)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    // messageRepo is injected for future use by message-count tools
    private readonly MessageRepository _messageRepo = messageRepo;

    [McpServerTool, Description("List folders for an email account with message and unread counts.")]
    public string ListFolders([Description("Account ID")] string accountId)
    {
        var folders = folderRepo.GetByAccount(accountId);
        return JsonSerializer.Serialize(folders, JsonOptions);
    }

    [McpServerTool, Description("Get detailed stats for a specific folder.")]
    public string GetFolderStats(
        [Description("Account ID")] string accountId,
        [Description("Folder path")] string folderPath)
    {
        var folder = folderRepo.GetByPath(accountId, folderPath);
        if (folder is null)
            return JsonSerializer.Serialize(
                new { error = $"Folder '{folderPath}' not found for account '{accountId}'." },
                JsonOptions);

        return JsonSerializer.Serialize(folder, JsonOptions);
    }
}
