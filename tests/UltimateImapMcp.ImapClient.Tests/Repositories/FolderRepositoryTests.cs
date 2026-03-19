using UltimateImapMcp.Core.Database;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.ImapClient.Tests.Repositories;

public class FolderRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly FolderRepository _repo;

    public FolderRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        var accountRepo = new AccountRepository(_db);
        accountRepo.Insert("test", "Test", "imap.test.com", 993,
            null, 465, true, "u@test.com", "password", "enc", "generic", null);
        _repo = new FolderRepository(_db);
    }

    [Fact]
    public void Insert_And_GetByPath_ReturnsFolder()
    {
        _repo.Insert("test", "INBOX", "Inbox", "inbox", "/");
        var folder = _repo.GetByPath("test", "INBOX");
        Assert.NotNull(folder);
        Assert.Equal("Inbox", folder.DisplayName);
        Assert.Equal("inbox", folder.Role);
    }

    [Fact]
    public void GetByAccount_ReturnsAllFolders()
    {
        _repo.Insert("test", "INBOX", "Inbox", "inbox", "/");
        _repo.Insert("test", "Sent", "Sent", "sent", "/");
        var folders = _repo.GetByAccount("test");
        Assert.Equal(2, folders.Count);
    }

    [Fact]
    public void Insert_Duplicate_IgnoredSilently()
    {
        _repo.Insert("test", "INBOX", "Inbox", "inbox", "/");
        _repo.Insert("test", "INBOX", "Inbox", "inbox", "/"); // should not throw
        var folders = _repo.GetByAccount("test");
        Assert.Single(folders);
    }

    [Fact]
    public void UpdateSyncState_UpdatesFields()
    {
        _repo.Insert("test", "INBOX", "Inbox", "inbox", "/");
        var folder = _repo.GetByPath("test", "INBOX")!;
        _repo.UpdateSyncState(folder.Id, lastSyncedUid: 42, messageCount: 100, unreadCount: 5);

        var updated = _repo.GetByPath("test", "INBOX")!;
        Assert.Equal(42, updated.LastSyncedUid);
        Assert.Equal(100, updated.MessageCount);
        Assert.Equal(5, updated.UnreadCount);
        Assert.NotNull(updated.LastSyncedAt);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
