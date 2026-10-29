using System.Text.Json.Serialization;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.Dashboard;

/// <summary>
/// One autocomplete option for a tool parameter on the dashboard Tools page.
/// <see cref="Value"/> and <see cref="Label"/> are always present; the context fields
/// let the client narrow one parameter's options by the values chosen for the others
/// (account → folder → message and back). Null context fields are omitted from JSON.
/// </summary>
public sealed record ToolSuggestion(object Value, string Label)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountId { get; init; }

    /// <summary>Folder the option belongs to (folder options and per-folder UID options).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FolderId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FolderPath { get; init; }

    /// <summary>Database message ID the option refers to (UID options only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageId { get; init; }

    /// <summary>Every folder the message is linked to (messageId options only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? FolderIds { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? FolderPaths { get; init; }
}

/// <summary>
/// Optional scope for message suggestions. Any set field narrows the message lists
/// (<c>uid</c>, <c>messageId</c>); the account and folder lists are always complete so
/// the client can still switch context.
/// </summary>
public sealed record ToolSuggestionScope(string? AccountId = null, int? FolderId = null, string? FolderPath = null)
{
    public bool IsEmpty =>
        string.IsNullOrEmpty(AccountId) && FolderId is null && string.IsNullOrEmpty(FolderPath);
}

/// <summary>
/// Builds the <c>GET /api/tools/suggestions</c> payload, keyed by <c>accountId</c>,
/// <c>folderPath</c>, <c>folderId</c>, <c>uid</c> and <c>messageId</c>.
/// Message folder membership and per-folder UIDs come from the <c>message_folders</c>
/// junction, never from the origin-folder columns on <c>messages</c>.
/// </summary>
public static class ToolSuggestionsBuilder
{
    /// <summary>Newest messages per account when no scope is given.</summary>
    public const int DefaultMessagesPerAccount = 20;

    /// <summary>Newest messages per account when scoped to an account or folder.</summary>
    public const int ScopedMessagesPerAccount = 50;

    public static Dictionary<string, List<ToolSuggestion>> Build(
        AccountRepository accountRepo,
        FolderRepository folderRepo,
        MessageRepository messageRepo,
        ToolSuggestionScope? scope = null)
    {
        scope ??= new ToolSuggestionScope();
        var limit = scope.IsEmpty ? DefaultMessagesPerAccount : ScopedMessagesPerAccount;

        var accountSuggestions = new List<ToolSuggestion>();
        var folderPathSuggestions = new List<ToolSuggestion>();
        var folderIdSuggestions = new List<ToolSuggestion>();
        var uidSuggestions = new List<ToolSuggestion>();
        var messageIdSuggestions = new List<ToolSuggestion>();

        foreach (var account in accountRepo.GetAll())
        {
            accountSuggestions.Add(new ToolSuggestion(account.Id, $"{account.Name} ({account.Provider})"));

            var folders = folderRepo.GetByAccount(account.Id);
            var foldersById = folders.ToDictionary(f => f.Id);

            // Display names can repeat within an account (e.g. "Inbox" under two parents);
            // add the folder ID to those path labels so they can be told apart.
            var duplicateNames = folders
                .GroupBy(FolderName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var f in folders)
            {
                var name = FolderName(f);
                var pathLabel = duplicateNames.Contains(name)
                    ? $"{name} (ID: {f.Id}) — {account.Name}"
                    : $"{name} — {account.Name}";
                folderPathSuggestions.Add(new ToolSuggestion(f.Path, pathLabel)
                {
                    AccountId = account.Id,
                    FolderId = f.Id,
                });
                folderIdSuggestions.Add(new ToolSuggestion(f.Id, $"{name} (ID: {f.Id}) — {account.Name}")
                {
                    AccountId = account.Id,
                    FolderPath = f.Path,
                });
            }

            if (!TryResolveMessageScope(scope, account.Id, folders, out var scopedFolderId))
                continue;

            var entries = messageRepo.GetRecentFolderEntries(account.Id, scopedFolderId, limit);

            foreach (var group in entries.GroupBy(e => e.MessageDbId))
            {
                // Ignore junction rows pointing at folders this account no longer has
                var memberships = group.Where(e => foldersById.ContainsKey(e.FolderId)).ToList();
                if (memberships.Count == 0) continue;

                var first = memberships[0];
                var subject = string.IsNullOrWhiteSpace(first.Subject) ? "(no subject)" : first.Subject;

                foreach (var e in memberships)
                {
                    // When scoped to a folder, only that folder's UID is a valid choice
                    if (scopedFolderId is not null && e.FolderId != scopedFolderId) continue;
                    var folder = foldersById[e.FolderId];
                    uidSuggestions.Add(new ToolSuggestion(e.Uid,
                        $"[{e.Uid}] {subject} — {FolderName(folder)} · {account.Name}")
                    {
                        AccountId = account.Id,
                        FolderId = folder.Id,
                        FolderPath = folder.Path,
                        MessageId = e.MessageDbId,
                    });
                }

                var memberFolders = memberships.Select(e => foldersById[e.FolderId]).ToList();
                messageIdSuggestions.Add(new ToolSuggestion(group.Key,
                    $"#{group.Key} {subject} — {string.Join(", ", memberFolders.Select(FolderName))} · {account.Name}")
                {
                    AccountId = account.Id,
                    FolderIds = memberFolders.Select(f => f.Id).ToList(),
                    FolderPaths = memberFolders.Select(f => f.Path).ToList(),
                });
            }
        }

        return new Dictionary<string, List<ToolSuggestion>>
        {
            ["accountId"] = accountSuggestions,
            ["folderPath"] = folderPathSuggestions,
            ["folderId"] = folderIdSuggestions,
            ["uid"] = uidSuggestions,
            ["messageId"] = messageIdSuggestions,
        };
    }

    /// <summary>
    /// Decides whether an account's messages are in scope and, if a folder is named,
    /// which of its folders. Returns false when the scope excludes the account entirely
    /// (other account, folder not in this account, or a folder ID and path that disagree).
    /// </summary>
    private static bool TryResolveMessageScope(
        ToolSuggestionScope scope, string accountId, List<FolderRecord> folders, out int? folderId)
    {
        folderId = null;

        if (!string.IsNullOrEmpty(scope.AccountId) &&
            !string.Equals(scope.AccountId, accountId, StringComparison.Ordinal))
            return false;

        if (scope.FolderId is not null)
        {
            if (!folders.Any(f => f.Id == scope.FolderId.Value)) return false;
            folderId = scope.FolderId;
        }

        if (!string.IsNullOrEmpty(scope.FolderPath))
        {
            // IMAP paths are case-sensitive (except INBOX): prefer an exact match
            var byPath = folders.FirstOrDefault(f => string.Equals(f.Path, scope.FolderPath, StringComparison.Ordinal))
                ?? folders.FirstOrDefault(f => string.Equals(f.Path, scope.FolderPath, StringComparison.OrdinalIgnoreCase));
            if (byPath is null) return false;
            if (folderId is not null && folderId != byPath.Id) return false;
            folderId = byPath.Id;
        }

        return true;
    }

    private static string FolderName(FolderRecord f) =>
        string.IsNullOrEmpty(f.DisplayName) ? f.Path : f.DisplayName;
}
