using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.ImapClient.Tests.Repositories;

/// <summary>
/// Covers MessageRepository.ClearAll / ClearAccount / ClearFolder: dependent rows go with the
/// messages, deduplicated messages survive a folder clear while another folder still holds
/// them, sync cursors are reset, and non-cache tables are left alone.
/// </summary>
public class CacheClearTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;
    private readonly AttachmentRepository _attachmentRepo;

    private readonly int _aInbox;
    private readonly int _aArchive;
    private readonly int _bInbox;

    public CacheClearTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_cacheclear_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _folderRepo = new FolderRepository(_db);
        _messageRepo = new MessageRepository(_db);
        _attachmentRepo = new AttachmentRepository(_db);

        _folderRepo.Insert("acct-a", "INBOX", "Inbox", "inbox", "/");
        _folderRepo.Insert("acct-a", "Archive", "Archive", "archive", "/");
        _folderRepo.Insert("acct-b", "INBOX", "Inbox", "inbox", "/");
        _aInbox = _folderRepo.GetByPath("acct-a", "INBOX")!.Id;
        _aArchive = _folderRepo.GetByPath("acct-a", "Archive")!.Id;
        _bInbox = _folderRepo.GetByPath("acct-b", "INBOX")!.Id;

        foreach (var id in new[] { _aInbox, _aArchive, _bInbox })
        {
            _folderRepo.UpdateSyncState(id, lastSyncedUid: 500, oldestSyncedUid: 100, messageCount: 10, unreadCount: 2);
            _folderRepo.UpdateSyncCursor(id, "cursor-token");
        }
    }

    // ---------------------------------------------------------------
    // Full clear
    // ---------------------------------------------------------------

    [Fact]
    public void ClearAll_RemovesMessagesLinksAttachmentsAndAnalysis()
    {
        var a1 = Seed("acct-a", _aInbox, 1, "a1");
        var b1 = Seed("acct-b", _bInbox, 1, "b1");

        var result = _messageRepo.ClearAll();

        Assert.Equal(2, result.MessagesDeleted);
        Assert.Equal(2, result.FolderLinksRemoved);
        Assert.Equal(0, Count("messages"));
        Assert.Equal(0, Count("message_folders"));
        Assert.Equal(0, Count("attachments"));
        Assert.Equal(0, Count("llm_analysis"));
        Assert.Empty(_attachmentRepo.GetByMessageId(a1));
        Assert.Empty(_attachmentRepo.GetByMessageId(b1));
    }

    [Fact]
    public void ClearAll_KeepsNonCacheTables()
    {
        Seed("acct-a", _aInbox, 1, "a1");
        Sql("""
            INSERT INTO operation_queue (id, account_id, operation, payload) VALUES ('op-1', 'acct-a', 'move', '{}');
            INSERT INTO llm_usage (date, model, tokens_input, request_count) VALUES ('2026-01-01', 'example-model', 10, 1);
            INSERT INTO llm_rules (name, trigger, analysis_type, prompt_template) VALUES ('rule', 'manual', 'custom', 'p');
            INSERT INTO sync_log (account_id, folder_id, sync_type, status) VALUES ('acct-a', 1, 'poll', 'completed');
            INSERT INTO dashboard_sessions (token, expires_at) VALUES ('tok', '2099-01-01');
            """);

        _messageRepo.ClearAll();

        Assert.Equal(1, Count("operation_queue"));
        Assert.Equal(1, Count("llm_usage"));
        Assert.Equal(1, Count("llm_rules"));
        Assert.Equal(1, Count("sync_log"));
        Assert.Equal(1, Count("dashboard_sessions"));
        Assert.Equal(3, Count("folders"));
    }

    [Fact]
    public void ClearAll_ResetsEveryFolderCursor_AndKeepsFolderSettings()
    {
        Seed("acct-a", _aInbox, 1, "a1");
        Sql($"UPDATE folders SET sync_enabled = 0 WHERE id = {_aArchive};");

        _messageRepo.ClearAll();

        foreach (var folder in _folderRepo.GetByAccount("acct-a").Concat(_folderRepo.GetByAccount("acct-b")))
        {
            Assert.Equal(0, folder.LastSyncedUid);
            Assert.Equal(0, folder.OldestSyncedUid);
            Assert.Null(folder.SyncCursor);
        }
        Assert.False(_folderRepo.GetByPath("acct-a", "Archive")!.SyncEnabled);
    }

    [Fact]
    public void ClearAll_EmptiesSearchIndex_AndRestoresTheFtsDeleteTrigger()
    {
        Seed("acct-a", _aInbox, 1, "a1", subject: "quarterly zebra report");

        _messageRepo.ClearAll();

        Assert.Empty(_messageRepo.SearchFts("zebra", accountId: "acct-a", maxResults: 10));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'messages_fts_delete';"));

        // Index keeps working after the clear: new rows are found, deleted rows disappear.
        var id = Seed("acct-a", _aInbox, 2, "a2", subject: "another zebra sighting");
        Assert.Single(_messageRepo.SearchFts("zebra", accountId: "acct-a", maxResults: 10));
        _messageRepo.ClearFolder("acct-a", _aInbox);
        Assert.Empty(_messageRepo.SearchFts("zebra", accountId: "acct-a", maxResults: 10));
        Assert.Null(_messageRepo.GetById(id));
    }

    // ---------------------------------------------------------------
    // Account clear
    // ---------------------------------------------------------------

    [Fact]
    public void ClearAccount_RemovesOnlyThatAccount_WithDependentRows()
    {
        var a1 = Seed("acct-a", _aInbox, 1, "a1");
        var a2 = Seed("acct-a", _aArchive, 2, "a2");
        var b1 = Seed("acct-b", _bInbox, 1, "b1");

        var result = _messageRepo.ClearAccount("acct-a");

        Assert.Equal(2, result.MessagesDeleted);
        Assert.Null(_messageRepo.GetById(a1));
        Assert.Null(_messageRepo.GetById(a2));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM attachments WHERE message_id IN ({a1}, {a2});"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM llm_analysis WHERE message_id IN ({a1}, {a2});"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM message_folders WHERE folder_id IN ({_aInbox}, {_aArchive});"));

        Assert.NotNull(_messageRepo.GetById(b1));
        Assert.Single(_attachmentRepo.GetByMessageId(b1));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM llm_analysis WHERE message_id = {b1};"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM message_folders WHERE message_id = {b1};"));
    }

    [Fact]
    public void ClearAccount_ResetsOnlyThatAccountsCursors()
    {
        _messageRepo.ClearAccount("acct-a");

        Assert.All(_folderRepo.GetByAccount("acct-a"), f =>
        {
            Assert.Equal(0, f.LastSyncedUid);
            Assert.Equal(0, f.OldestSyncedUid);
            Assert.Null(f.SyncCursor);
        });
        var other = _folderRepo.GetByPath("acct-b", "INBOX")!;
        Assert.Equal(500, other.LastSyncedUid);
        Assert.Equal(100, other.OldestSyncedUid);
        Assert.Equal("cursor-token", other.SyncCursor);
    }

    // ---------------------------------------------------------------
    // Folder clear
    // ---------------------------------------------------------------

    [Fact]
    public void ClearFolder_MessageInTwoFolders_SurvivesWithOnlyThatLinkRemoved()
    {
        // Same RFC Message-ID in INBOX and Archive: stored once, linked twice.
        var shared = Seed("acct-a", _aInbox, 10, "shared");
        Assert.Equal(shared, _messageRepo.InsertOrLink("acct-a", _aArchive, 20, "<shared@example.com>",
            null, null, null, "Subject shared", "Sender <sender@example.com>", "sender@example.com",
            "[]", null, null, "2026-03-18T10:00:00Z", 1774040400, "[]", 1024, false, "snippet"));

        var result = _messageRepo.ClearFolder("acct-a", _aInbox);

        Assert.Equal(1, result.FolderLinksRemoved);
        Assert.Equal(0, result.MessagesDeleted);
        Assert.NotNull(_messageRepo.GetById(shared));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM message_folders WHERE message_id = {shared} AND folder_id = {_aInbox};"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM message_folders WHERE message_id = {shared} AND folder_id = {_aArchive};"));
        Assert.Single(_attachmentRepo.GetByMessageId(shared));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM llm_analysis WHERE message_id = {shared};"));
        Assert.NotNull(_messageRepo.GetByUid("acct-a", _aArchive, 20));
    }

    [Fact]
    public void ClearFolder_MessageOnlyInThatFolder_IsDeletedWithDependentRows()
    {
        var only = Seed("acct-a", _aInbox, 1, "only");
        var elsewhere = Seed("acct-a", _aArchive, 2, "elsewhere");

        var result = _messageRepo.ClearFolder("acct-a", _aInbox);

        Assert.Equal(1, result.MessagesDeleted);
        Assert.Equal(1, result.FolderLinksRemoved);
        Assert.Null(_messageRepo.GetById(only));
        Assert.Empty(_attachmentRepo.GetByMessageId(only));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM llm_analysis WHERE message_id = {only};"));

        Assert.NotNull(_messageRepo.GetById(elsewhere));
        Assert.Single(_attachmentRepo.GetByMessageId(elsewhere));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM llm_analysis WHERE message_id = {elsewhere};"));
    }

    [Fact]
    public void ClearFolder_UsesFolderLinks_NotTheOriginFolderColumn()
    {
        // Message originated in INBOX (messages.folder_id) but has since moved to Archive.
        var moved = Seed("acct-a", _aInbox, 1, "moved");
        _messageRepo.LinkToFolder(moved, _aArchive, 7);
        _messageRepo.UnlinkFromFolder(moved, _aInbox);

        // Clearing INBOX must not touch it: it is no longer in INBOX.
        var inbox = _messageRepo.ClearFolder("acct-a", _aInbox);
        Assert.Equal(0, inbox.MessagesDeleted);
        Assert.NotNull(_messageRepo.GetById(moved));

        // Clearing Archive removes it, even though messages.folder_id still says INBOX.
        var archive = _messageRepo.ClearFolder("acct-a", _aArchive);
        Assert.Equal(1, archive.MessagesDeleted);
        Assert.Null(_messageRepo.GetById(moved));
    }

    [Fact]
    public void ClearFolder_LeavesUnrelatedUnlinkedMessagesAlone()
    {
        // An unlinked row that originated in Archive is not part of an INBOX clear.
        var unlinkedArchive = Seed("acct-a", _aArchive, 3, "unlinked-archive");
        _messageRepo.UnlinkFromFolder(unlinkedArchive, _aArchive);
        // An unlinked row that originated in INBOX is (startup repair would re-link it there).
        var unlinkedInbox = Seed("acct-a", _aInbox, 4, "unlinked-inbox");
        _messageRepo.UnlinkFromFolder(unlinkedInbox, _aInbox);

        var result = _messageRepo.ClearFolder("acct-a", _aInbox);

        Assert.Equal(1, result.MessagesDeleted);
        Assert.NotNull(_messageRepo.GetById(unlinkedArchive));
        Assert.Null(_messageRepo.GetById(unlinkedInbox));
    }

    [Fact]
    public void ClearFolder_ResetsOnlyThatFolderCursor()
    {
        _messageRepo.ClearFolder("acct-a", _aInbox);

        var inbox = _folderRepo.GetByPath("acct-a", "INBOX")!;
        Assert.Equal(0, inbox.LastSyncedUid);
        Assert.Equal(0, inbox.OldestSyncedUid);
        Assert.Null(inbox.SyncCursor);

        var archive = _folderRepo.GetByPath("acct-a", "Archive")!;
        Assert.Equal(500, archive.LastSyncedUid);
        Assert.Equal("cursor-token", archive.SyncCursor);
    }

    [Fact]
    public void ClearFolder_FolderOfAnotherAccount_DoesNothing()
    {
        var b1 = Seed("acct-b", _bInbox, 1, "b1");

        var result = _messageRepo.ClearFolder("acct-a", _bInbox);

        Assert.Equal(0, result.MessagesDeleted);
        Assert.Equal(0, result.FolderLinksRemoved);
        Assert.NotNull(_messageRepo.GetById(b1));
        Assert.Equal(500, _folderRepo.GetByPath("acct-b", "INBOX")!.LastSyncedUid);
    }

    [Fact]
    public void ClearFolder_KeepsNonCacheTables()
    {
        Seed("acct-a", _aInbox, 1, "a1");
        Sql("""
            INSERT INTO operation_queue (id, account_id, operation, payload) VALUES ('op-1', 'acct-a', 'move', '{}');
            INSERT INTO llm_usage (date, model, tokens_input, request_count) VALUES ('2026-01-01', 'example-model', 10, 1);
            """);

        _messageRepo.ClearFolder("acct-a", _aInbox);
        _messageRepo.ClearAccount("acct-a");

        Assert.Equal(1, Count("operation_queue"));
        Assert.Equal(1, Count("llm_usage"));
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    /// <summary>Inserts a message with one attachment and one LLM analysis row. Returns its DB id.</summary>
    private int Seed(string accountId, int folderId, long uid, string tag, string? subject = null)
    {
        var id = _messageRepo.InsertOrLink(accountId, folderId, uid, $"<{tag}@example.com>",
            null, null, null, subject ?? $"Subject {tag}", "Sender <sender@example.com>", "sender@example.com",
            "[]", null, null, "2026-03-18T10:00:00Z", 1774040400, "[]", 1024, true, "snippet");
        _attachmentRepo.Insert(id, $"{tag}.pdf", "application/pdf", 100, null, false);
        Sql($"INSERT INTO llm_analysis (message_id, analysis_type, result) VALUES ({id}, 'category', '{{}}');");
        return id;
    }

    private void Sql(string sql) => _db.ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    });

    private long Scalar(string sql)
    {
        using var conn = _db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private long Count(string table) => Scalar($"SELECT COUNT(*) FROM {table};");

    // ---------------------------------------------------------------
    // Review follow-ups
    // ---------------------------------------------------------------

    [Fact]
    public void ClearAll_EmptiesTheSearchIndex()
    {
        // SearchFts joins messages, so stale index rows never show in results;
        // check the index itself.
        Seed("acct-a", _aInbox, 1, "fts", "Quarterly planning notes");
        Assert.True(Count("messages_fts_docsize") > 0);

        _messageRepo.ClearAll();

        Assert.Equal(0, Count("messages_fts_docsize"));
    }

    [Fact]
    public void LinkToFolder_AfterAClearDeletedTheRow_LeavesNoDanglingLink()
    {
        // The race: a sync inserts a message, a clear deletes it, then the sync
        // links the id it read before the clear committed.
        var staleId = Seed("acct-a", _aInbox, 77, "race");
        _messageRepo.ClearAccount("acct-a");

        _messageRepo.LinkToFolder(staleId, _aInbox, 77);

        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM message_folders WHERE folder_id = {_aInbox} AND uid = 77;"));
        // The next sync can cache it again and it shows in its folder.
        var freshId = Seed("acct-a", _aInbox, 77, "race");
        Assert.Equal(freshId, Assert.Single(_messageRepo.GetByFolder("acct-a", _aInbox)).Id);
    }

    [Fact]
    public void LinkToFolder_ReplacesAnExistingDanglingLinkForTheSameSlot()
    {
        // A dangling link left by older code holds (folder, uid) via the unique index.
        Sql($"INSERT INTO message_folders (message_id, folder_id, uid) VALUES (999999, {_aInbox}, 88);");
        var id = Seed("acct-a", _aArchive, 5, "moved-in");

        _messageRepo.LinkToFolder(id, _aInbox, 88);

        Assert.Equal(id, _messageRepo.GetByUid("acct-a", _aInbox, 88)!.Id);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
