using System.ComponentModel;
using System.Text.Json;
using MailKit;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class FolderTools(FolderRepository folderRepo,
    AccountRepository accountRepo, CredentialEncryptor encryptor,
    IOAuthAccessTokenProvider oauthProvider, AppConfig config, ILogger<FolderTools> logger)
{
    [McpServerTool, Description(
        "List all folders for an email account with message counts, unread counts, and sync status.")]
    public string ListFolders([Description("Account ID")] string accountId)
    {
        return McpJsonDefaults.LogToolCall(logger, "list_folders",
            new Dictionary<string, object?> { ["accountId"] = accountId },
            () =>
            {
                try
                {
                    var folders = folderRepo.GetByAccount(accountId);
                    return JsonSerializer.Serialize(folders, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "ListFolders failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description(
        "Get detailed statistics for a specific folder including message count, size, and sync state.")]
    public string GetFolderStats(
        [Description("Account ID")] string accountId,
        [Description("Folder path")] string folderPath)
    {
        return McpJsonDefaults.LogToolCall(logger, "get_folder_stats",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["folderPath"] = folderPath },
            () =>
            {
                try
                {
                    var folder = folderRepo.GetByPath(accountId, folderPath);
                    if (folder is null)
                        return McpJsonDefaults.Error($"Folder '{folderPath}' not found for account '{accountId}'.");

                    return JsonSerializer.Serialize(folder, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "GetFolderStats failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description(
        "Create a new IMAP folder (mailbox) on the server. Supports nested paths like 'Archive/2026'. " +
        "Returns the created folder path or an error if the folder already exists.")]
    public async Task<string> CreateFolder(
        [Description("Account ID or name")] string accountId,
        [Description("Folder path to create (e.g., 'Projects' or 'Archive/2026')")] string folderPath)
    {
        return await McpJsonDefaults.LogToolCallAsync(logger, "create_folder",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["folderPath"] = folderPath },
            async () =>
            {
                try
                {
                    var record = accountRepo.ResolveAccount(accountId);
                    if (record is null)
                        return McpJsonDefaults.Error($"Account '{accountId}' not found.");

                    if (!record.Enabled)
                        return McpJsonDefaults.Error($"Account '{record.Name}' is disabled.");

                    if (string.IsNullOrWhiteSpace(folderPath))
                        return McpJsonDefaults.Error("Folder path cannot be empty.");

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
                        return McpJsonDefaults.Error(result.error);

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
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "CreateFolder failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }
}
