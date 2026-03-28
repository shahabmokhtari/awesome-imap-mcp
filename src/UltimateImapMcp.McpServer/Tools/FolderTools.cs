using System.ComponentModel;
using System.Text.Json;
using MailKit;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class FolderTools(FolderRepository folderRepo, MessageRepository messageRepo,
    AccountRepository accountRepo, CredentialEncryptor encryptor,
    IOAuthAccessTokenProvider oauthProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    // messageRepo is injected for future use by message-count tools
    private readonly MessageRepository _messageRepo = messageRepo;

    [McpServerTool, Description(
        "List all folders for an email account with message counts, unread counts, and sync status.")]
    public string ListFolders([Description("Account ID")] string accountId)
    {
        var folders = folderRepo.GetByAccount(accountId);
        return JsonSerializer.Serialize(folders, JsonOptions);
    }

    [McpServerTool, Description(
        "Get detailed statistics for a specific folder including message count, size, and sync state.")]
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

    [McpServerTool, Description(
        "Create a new IMAP folder (mailbox) on the server. Supports nested paths like 'Archive/2026'. " +
        "Returns the created folder path or an error if the folder already exists.")]
    public async Task<string> CreateFolder(
        [Description("Account ID or name")] string accountId,
        [Description("Folder path to create (e.g., 'Projects' or 'Archive/2026')")] string folderPath)
    {
        try
        {
            var record = accountRepo.ResolveAccount(accountId);
            if (record is null)
                return JsonSerializer.Serialize(
                    new { error = $"Account '{accountId}' not found." }, JsonOptions);

            if (!record.Enabled)
                return JsonSerializer.Serialize(
                    new { error = $"Account '{record.Name}' is disabled." }, JsonOptions);

            if (string.IsNullOrWhiteSpace(folderPath))
                return JsonSerializer.Serialize(
                    new { error = "Folder path cannot be empty." }, JsonOptions);

            var accountConfig = AccountConfigMapper.ToAccountConfig(record, encryptor);
            using var connMgr = new ImapConnectionManager(accountConfig, encryptor,
                oauthProvider: oauthProvider, accountId: record.Id);

            var result = await connMgr.ExecuteAsync(async client =>
            {
                var ns = client.PersonalNamespaces.Count > 0
                    ? client.PersonalNamespaces[0]
                    : null;
                var separator = ns?.DirectorySeparator ?? '/';
                var topFolder = ns is not null
                    ? await client.GetFolderAsync(ns.Path).ConfigureAwait(false)
                    : client.Inbox.ParentFolder;

                // Check if the folder already exists
                try
                {
                    var existing = await client.GetFolderAsync(folderPath).ConfigureAwait(false);
                    if (existing is not null)
                        return new { error = (string?)$"Folder '{folderPath}' already exists.", path = (string?)null, separator = (char?)null };
                }
                catch (FolderNotFoundException)
                {
                    // Expected — folder does not exist yet
                }

                // Normalise path separators to match the server's delimiter
                var normalizedPath = folderPath.Replace('/', separator);

                var created = await topFolder.CreateAsync(normalizedPath, true).ConfigureAwait(false);
                return new { error = (string?)null, path = (string?)created.FullName, separator = (char?)separator };
            });

            if (result.error is not null)
                return JsonSerializer.Serialize(new { error = result.error }, JsonOptions);

            // Register the folder in the local database
            var displayName = result.path!.Contains(result.separator!.Value)
                ? result.path.Split(result.separator.Value).Last()
                : result.path;
            folderRepo.Insert(record.Id, result.path, displayName, role: null,
                delimiter: result.separator!.Value.ToString());

            return JsonSerializer.Serialize(new
            {
                success = true,
                folder_path = result.path,
                account_id = record.Id,
                account_name = record.Name
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FolderTools.CreateFolder] {ex}");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }
}
