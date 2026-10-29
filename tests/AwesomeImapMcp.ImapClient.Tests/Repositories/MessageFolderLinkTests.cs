using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.ImapClient.Tests.Repositories;

/// <summary>
/// A message seen in two folders is stored once and linked to each folder with
/// that folder's own UID. The messages row keeps only the first (origin) pair, so
/// any lookup scoped to the other folder must report that folder's link, or
/// callers act on a UID that names a different message there.
/// </summary>
public class MessageFolderLinkTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
    private readonly string _accountsPath = Path.Combine(Path.GetTempPath(), $"test_accounts_{Guid.NewGuid()}.json");
    private readonly AppDatabase _db;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _repo;
    private readonly int _spam, _inbox;

    public MessageFolderLinkTests()
    {
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        new AccountRepository(new AccountsStore(_accountsPath)).Insert("acct", "Test", "imap.example.com", 993,
            null, 465, true, "user@example.com", "password", "enc", "generic", null);
        _folderRepo = new FolderRepository(_db);
        _repo = new MessageRepository(_db);
        _folderRepo.Insert("acct", "Spam", "Spam", "junk", "/");
        _folderRepo.Insert("acct", "INBOX", "Inbox", "inbox", "/");
        _spam = _folderRepo.GetByPath("acct", "Spam")!.Id;
        _inbox = _folderRepo.GetByPath("acct", "INBOX")!.Id;
    }

    private int Add(int folderId, long uid, string rfcId, string subject) =>
        _repo.InsertOrLink("acct", folderId, uid, rfcId, null, null, null, subject,
            "Sender <sender@example.com>", "sender@example.com", "[]", null, null,
            "2026-10-01T10:00:00Z", 1790000000, "[]", 100, false, subject);

    /// <summary>One message first seen in Spam as uid 1880, then in INBOX as uid 39950.</summary>
    private int AddDeduplicated()
    {
        var id = Add(_spam, 1880, "<shared@example.com>", "Shared message");
        Assert.Equal(id, Add(_inbox, 39950, "<shared@example.com>", "Shared message"));
        return id;
    }

    [Fact]
    public void GetByUid_ReportsTheRequestedFoldersLink()
    {
        var id = AddDeduplicated();

        var msg = _repo.GetByUid("acct", _inbox, 39950)!;

        Assert.Equal(id, msg.Id);
        Assert.Equal(_inbox, msg.FolderId);
        Assert.Equal(39950, msg.Uid);
    }

    [Fact]
    public void GetByFolder_ReportsThatFoldersUid()
    {
        AddDeduplicated();

        Assert.Equal(39950, Assert.Single(_repo.GetByFolder("acct", _inbox)).Uid);
        Assert.Equal(1880, Assert.Single(_repo.GetByFolder("acct", _spam)).Uid);
    }

    [Fact]
    public void SearchAdvanced_ScopedToFolder_ReportsThatFoldersLink()
    {
        AddDeduplicated();

        var scoped = Assert.Single(_repo.SearchAdvanced(new SearchFilter { AccountId = "acct", FolderId = _inbox }));
        Assert.Equal((_inbox, 39950L), (scoped.FolderId, scoped.Uid));

        var fts = Assert.Single(_repo.SearchFts("Shared", "acct", _inbox));
        Assert.Equal((_inbox, 39950L), (fts.FolderId, fts.Uid));
    }

    [Fact]
    public void Unscoped_ReportsTheOriginLink_WhileItExists()
    {
        var id = AddDeduplicated();

        var msg = _repo.GetById(id)!;
        Assert.Equal((_spam, 1880L), (msg.FolderId, msg.Uid));
        var found = Assert.Single(_repo.SearchAdvanced(new SearchFilter { AccountId = "acct" }));
        Assert.Equal((_spam, 1880L), (found.FolderId, found.Uid));
    }

    [Fact]
    public void Unscoped_FallsBackToAnExistingLink_WhenTheOriginLinkIsGone()
    {
        var id = AddDeduplicated();
        _repo.UnlinkFromFolder(id, _spam);

        var msg = _repo.GetById(id)!;

        Assert.Equal((_inbox, 39950L), (msg.FolderId, msg.Uid));
    }

    [Fact]
    public void Resolve_WithoutFolder_RefusesAUidThatNamesDifferentMessages()
    {
        Add(_spam, 500, "<a@example.com>", "In spam");
        Add(_inbox, 500, "<b@example.com>", "In inbox");

        var ex = Assert.Throws<InvalidOperationException>(
            () => _repo.Resolve(null, "acct", null, 500, _folderRepo));

        Assert.Contains("Spam", ex.Message);
        Assert.Contains("INBOX", ex.Message);
    }

    [Fact]
    public void Resolve_WithoutFolder_ReturnsTheOnlyMatch()
    {
        AddDeduplicated();

        var msg = _repo.Resolve(null, "acct", null, 39950, _folderRepo)!;

        Assert.Equal((_inbox, 39950L), (msg.FolderId, msg.Uid));
    }

    [Fact]
    public void Resolve_WithFolder_MissingUid_ReturnsNull()
    {
        AddDeduplicated();

        Assert.Null(_repo.Resolve(null, "acct", _inbox, 1880, _folderRepo));
    }

    // ---- per-folder deletion ----

    private bool IsSoftDeleted(int id)
    {
        using var conn = _db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT deleted_at IS NOT NULL FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    private void MarkDeletedTheOldWay(int id)
    {
        _db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET deleted_at = datetime('now') WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        });
    }

    [Fact]
    public void SoftDeleteByUids_GoneFromOneFolder_StaysVisibleInTheOther()
    {
        var id = AddDeduplicated();

        Assert.Equal(1, _repo.SoftDeleteByUids("acct", _inbox, [39950]));

        Assert.False(IsSoftDeleted(id));
        Assert.Empty(_repo.GetByFolder("acct", _inbox));
        Assert.Equal(id, Assert.Single(_repo.GetByFolder("acct", _spam)).Id);
        Assert.DoesNotContain(39950L, _repo.GetCachedUids("acct", _inbox));
    }

    [Fact]
    public void SoftDeleteByUids_GoneFromItsOnlyFolder_IsSoftDeleted()
    {
        var id = Add(_inbox, 7, "<only@example.com>", "Only here");

        Assert.Equal(1, _repo.SoftDeleteByUids("acct", _inbox, [7]));

        Assert.True(IsSoftDeleted(id));
        Assert.Empty(_repo.GetByFolder("acct", _inbox));
    }

    [Fact]
    public void SoftDeleteByUids_RepairsAMessageHiddenByTheOldWholeMessageDelete()
    {
        // The old behaviour: moved INBOX -> Spam, INBOX sync then soft-deleted the
        // whole message and left the stale INBOX link behind.
        var id = AddDeduplicated();
        MarkDeletedTheOldWay(id);
        Assert.Empty(_repo.GetByFolder("acct", _spam));

        // Next INBOX sync finds 39950 gone from the server.
        _repo.SoftDeleteByUids("acct", _inbox, [39950]);

        Assert.False(IsSoftDeleted(id));
        Assert.Equal(id, Assert.Single(_repo.GetByFolder("acct", _spam)).Id);
    }

    [Fact]
    public void SoftDeleteByUids_UnknownUid_ChangesNothing()
    {
        var id = AddDeduplicated();

        Assert.Equal(0, _repo.SoftDeleteByUids("acct", _inbox, [123456]));

        Assert.False(IsSoftDeleted(id));
        Assert.Single(_repo.GetByFolder("acct", _inbox));
    }

    [Fact]
    public void Relinking_ASoftDeletedMessage_MakesItVisibleAgain()
    {
        // Restored on the server: the folder sync sees the same RFC Message-ID again.
        var id = Add(_inbox, 7, "<restored@example.com>", "Restored");
        _repo.SoftDeleteByUids("acct", _inbox, [7]);
        Assert.True(IsSoftDeleted(id));

        Assert.Equal(id, Add(_spam, 900, "<restored@example.com>", "Restored"));

        Assert.False(IsSoftDeleted(id));
        Assert.Equal(900, Assert.Single(_repo.GetByFolder("acct", _spam)).Uid);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _accountsPath })
            if (File.Exists(f)) File.Delete(f);
    }
}
