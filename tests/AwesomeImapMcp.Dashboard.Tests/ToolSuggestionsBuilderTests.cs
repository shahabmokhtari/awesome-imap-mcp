using System.Text.Json;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.Dashboard.Tests;

/// <summary>
/// Tests for the Tools page suggestion payload. The key invariant: a message's folder
/// membership and per-folder UID come from the message_folders junction, not from the
/// origin-folder columns on the messages row.
/// </summary>
public sealed class ToolSuggestionsBuilderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _accountsPath;
    private readonly AppDatabase _db;
    private readonly AccountRepository _accountRepo;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;

    private readonly int _aInbox;
    private readonly int _aArchive;
    private readonly int _bInbox;

    public ToolSuggestionsBuilderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_suggestions_{Guid.NewGuid()}.db");
        _accountsPath = Path.Combine(Path.GetTempPath(), $"test_suggestions_accounts_{Guid.NewGuid()}.json");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _accountRepo = new AccountRepository(new AccountsStore(_accountsPath));
        _folderRepo = new FolderRepository(_db);
        _messageRepo = new MessageRepository(_db);

        _accountRepo.Insert("acct-a", "Alpha", "imap.example.com", 993,
            null, 465, true, "alpha@example.com", "password", "enc", "generic", null);
        _accountRepo.Insert("acct-b", "Beta", "imap.example.org", 993,
            null, 465, true, "beta@example.org", "password", "enc", "generic", null);

        _folderRepo.Insert("acct-a", "INBOX", "Inbox", "inbox", "/");
        _folderRepo.Insert("acct-a", "Archive", "Archive", "archive", "/");
        _folderRepo.Insert("acct-b", "INBOX", "Inbox", "inbox", "/");
        _aInbox = _folderRepo.GetByPath("acct-a", "INBOX")!.Id;
        _aArchive = _folderRepo.GetByPath("acct-a", "Archive")!.Id;
        _bInbox = _folderRepo.GetByPath("acct-b", "INBOX")!.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _accountsPath })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private int AddMessage(string accountId, int folderId, long uid, string subject, long dateEpoch, string? rfcId = null)
    {
        return _messageRepo.InsertOrLink(accountId, folderId, uid, rfcId ?? $"<{Guid.NewGuid()}@example.com>",
            null, null, null, subject, "Sender <sender@example.com>", "sender@example.com",
            "[]", null, null, "2026-01-01T00:00:00Z", dateEpoch, "[]", 100, false, "snippet");
    }

    private Dictionary<string, List<ToolSuggestion>> Build(ToolSuggestionScope? scope = null) =>
        ToolSuggestionsBuilder.Build(_accountRepo, _folderRepo, _messageRepo, scope);

    [Fact]
    public void MessageInTwoFolders_GetsEachFoldersOwnUid()
    {
        // Same RFC Message-ID: stored once, linked to INBOX as uid 10 and Archive as uid 77
        var id = AddMessage("acct-a", _aInbox, 10, "Quarterly report", 1_000, "<shared@example.com>");
        var linkedId = AddMessage("acct-a", _aArchive, 77, "Quarterly report", 1_000, "<shared@example.com>");
        Assert.Equal(id, linkedId);

        var uids = Build()["uid"].Where(s => s.MessageId == id).ToList();

        Assert.Equal(2, uids.Count);
        var inbox = Assert.Single(uids, s => s.FolderId == _aInbox);
        var archive = Assert.Single(uids, s => s.FolderId == _aArchive);
        Assert.Equal(10L, inbox.Value);
        Assert.Equal("INBOX", inbox.FolderPath);
        Assert.Equal(77L, archive.Value);
        Assert.Equal("Archive", archive.FolderPath);
        Assert.All(uids, s => Assert.Equal("acct-a", s.AccountId));
    }

    [Fact]
    public void UidIsTakenFromJunction_NotFromStaleOriginColumns()
    {
        var id = AddMessage("acct-a", _aInbox, 10, "Moved later", 1_000);
        // Simulate a move: the junction row changes, the messages row keeps its origin values
        _messageRepo.LinkToFolder(id, _aArchive, 555);
        _messageRepo.UnlinkFromFolder(id, _aInbox);

        var uid = Assert.Single(Build()["uid"], s => s.MessageId == id);
        Assert.Equal(_aArchive, uid.FolderId);
        Assert.Equal(555L, uid.Value);

        var msg = Assert.Single(Build()["messageId"], s => (int)s.Value == id);
        Assert.Equal([_aArchive], msg.FolderIds);
    }

    [Fact]
    public void MessageIdSuggestion_ListsEveryFolder()
    {
        var id = AddMessage("acct-a", _aInbox, 10, "Two homes", 1_000, "<two@example.com>");
        AddMessage("acct-a", _aArchive, 20, "Two homes", 1_000, "<two@example.com>");

        var s = Assert.Single(Build()["messageId"], x => (int)x.Value == id);
        Assert.Equal("acct-a", s.AccountId);
        Assert.Equal(new[] { _aInbox, _aArchive }.Order(), s.FolderIds!.Order());
        Assert.Equal(new[] { "Archive", "INBOX" }, s.FolderPaths!.Order());
        Assert.Contains("Two homes", s.Label);
    }

    [Fact]
    public void ScopeByFolder_ReturnsOnlyThatFoldersUids()
    {
        var shared = AddMessage("acct-a", _aInbox, 10, "Both", 3_000, "<both@example.com>");
        AddMessage("acct-a", _aArchive, 20, "Both", 3_000, "<both@example.com>");
        AddMessage("acct-a", _aInbox, 11, "Inbox only", 2_000);
        var archiveOnly = AddMessage("acct-a", _aArchive, 21, "Archive only", 1_000);
        AddMessage("acct-b", _bInbox, 5, "Other account", 4_000);

        var result = Build(new ToolSuggestionScope(FolderId: _aArchive));

        Assert.All(result["uid"], s => Assert.Equal(_aArchive, s.FolderId));
        Assert.Equal(new long[] { 20, 21 }, result["uid"].Select(s => (long)s.Value).Order());
        Assert.Equal(new[] { shared, archiveOnly }.Order(), result["messageId"].Select(s => (int)s.Value).Order());
        // Account and folder lists stay complete
        Assert.Equal(2, result["accountId"].Count);
        Assert.Equal(3, result["folderId"].Count);
    }

    [Fact]
    public void ScopeByAccountAndPath_ResolvesThatAccountsFolder()
    {
        AddMessage("acct-a", _aInbox, 10, "Alpha inbox", 1_000);
        AddMessage("acct-b", _bInbox, 10, "Beta inbox", 1_000);

        var both = Build(new ToolSuggestionScope(FolderPath: "INBOX"))["uid"];
        Assert.Equal(new[] { _aInbox, _bInbox }.Order(), both.Select(s => s.FolderId!.Value).Order());

        var betaOnly = Build(new ToolSuggestionScope(AccountId: "acct-b", FolderPath: "INBOX"))["uid"];
        var s = Assert.Single(betaOnly);
        Assert.Equal(_bInbox, s.FolderId);
        Assert.Equal("acct-b", s.AccountId);
    }

    [Fact]
    public void ScopeWithFolderOfAnotherAccount_ReturnsNoMessages()
    {
        AddMessage("acct-a", _aInbox, 10, "Alpha", 1_000);
        AddMessage("acct-b", _bInbox, 10, "Beta", 1_000);

        var result = Build(new ToolSuggestionScope(AccountId: "acct-b", FolderId: _aInbox));

        Assert.Empty(result["uid"]);
        Assert.Empty(result["messageId"]);
    }

    [Fact]
    public void Limits_NewestPerAccount_UnscopedAndScoped()
    {
        for (var i = 1; i <= 60; i++)
            AddMessage("acct-a", _aInbox, i, $"Message {i}", i);
        AddMessage("acct-b", _bInbox, 1, "Beta one", 1);

        var unscoped = Build();
        var alpha = unscoped["uid"].Where(s => s.AccountId == "acct-a").ToList();
        Assert.Equal(ToolSuggestionsBuilder.DefaultMessagesPerAccount, alpha.Count);
        // Newest first: uid 60 (dateEpoch 60) down to 41
        Assert.Equal(60L, alpha[0].Value);
        Assert.Equal(41L, alpha[^1].Value);
        // The other account's message is not crowded out
        Assert.Single(unscoped["uid"], s => s.AccountId == "acct-b");

        var scoped = Build(new ToolSuggestionScope(AccountId: "acct-a"));
        Assert.Equal(ToolSuggestionsBuilder.ScopedMessagesPerAccount, scoped["uid"].Count);
        Assert.Equal(ToolSuggestionsBuilder.ScopedMessagesPerAccount, scoped["messageId"].Count);
    }

    [Fact]
    public void DeletedMessages_AreNotSuggested()
    {
        AddMessage("acct-a", _aInbox, 10, "Kept", 2_000);
        AddMessage("acct-a", _aInbox, 11, "Gone", 1_000);
        _messageRepo.SoftDeleteByUids("acct-a", _aInbox, [11]);

        var uids = Build()["uid"];
        Assert.Equal(10L, Assert.Single(uids).Value);
    }

    [Fact]
    public void DuplicateDisplayNames_IncludeFolderIdInPathLabel()
    {
        _folderRepo.Insert("acct-a", "Projects/Inbox", "Inbox", null, "/");
        var nestedId = _folderRepo.GetByPath("acct-a", "Projects/Inbox")!.Id;

        var paths = Build()["folderPath"];
        var nested = Assert.Single(paths, s => (string)s.Value == "Projects/Inbox");
        Assert.Contains($"(ID: {nestedId})", nested.Label);
        Assert.Equal(nestedId, nested.FolderId);
        var archive = Assert.Single(paths, s => (string)s.Value == "Archive");
        Assert.DoesNotContain("(ID:", archive.Label);
    }

    [Fact]
    public void Serializes_CamelCase_BackwardsCompatibleShape_OmittingNulls()
    {
        AddMessage("acct-a", _aInbox, 10, "Hello", 1_000);
        var json = JsonSerializer.Serialize(Build(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var key in new[] { "accountId", "folderPath", "folderId", "uid", "messageId" })
            Assert.True(root.TryGetProperty(key, out _), $"missing key {key}");

        var account = root.GetProperty("accountId")[0];
        Assert.Equal("acct-a", account.GetProperty("value").GetString());
        Assert.True(account.TryGetProperty("label", out _));
        Assert.False(account.TryGetProperty("folderId", out _)); // nulls omitted

        var uid = root.GetProperty("uid")[0];
        Assert.Equal(10, uid.GetProperty("value").GetInt64());
        Assert.Equal("acct-a", uid.GetProperty("accountId").GetString());
        Assert.Equal(_aInbox, uid.GetProperty("folderId").GetInt32());
        Assert.Equal("INBOX", uid.GetProperty("folderPath").GetString());
        Assert.True(uid.TryGetProperty("messageId", out _));

        var folderId = root.GetProperty("folderId")[0];
        Assert.Equal(JsonValueKind.Number, folderId.GetProperty("value").ValueKind);
    }
}
