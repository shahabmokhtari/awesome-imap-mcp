using UltimateImapMcp.Core.Database;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.ImapClient.Tests.Repositories;

public class AccountRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly AccountRepository _repo;

    public AccountRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _repo = new AccountRepository(_db);
    }

    [Fact]
    public void Insert_And_GetById_ReturnsAccount()
    {
        _repo.Insert("personal", "Personal Gmail", "imap.gmail.com", 993,
            "smtp.gmail.com", 465, true, "test@gmail.com", "app_password",
            "encrypted_creds", "gmail", null);

        var account = _repo.GetById("personal");
        Assert.NotNull(account);
        Assert.Equal("Personal Gmail", account.Name);
        Assert.Equal("imap.gmail.com", account.ImapHost);
        Assert.Equal("test@gmail.com", account.Username);
    }

    [Fact]
    public void GetAll_ReturnsAllAccounts()
    {
        _repo.Insert("a1", "Account 1", "imap.a.com", 993,
            null, 465, true, "u1@a.com", "password", "enc1", "generic", null);
        _repo.Insert("a2", "Account 2", "imap.b.com", 993,
            null, 465, true, "u2@b.com", "password", "enc2", "generic", null);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void GetById_NonExistent_ReturnsNull()
    {
        var account = _repo.GetById("nonexistent");
        Assert.Null(account);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
