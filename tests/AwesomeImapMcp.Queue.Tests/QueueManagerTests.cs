using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.Core.Encryption;
using AwesomeImapMcp.ImapClient.Repositories;
using AwesomeImapMcp.Queue;
using AwesomeImapMcp.Queue.Models;

namespace AwesomeImapMcp.Queue.Tests;

public class QueueManagerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _accountsPath;
    private readonly AppDatabase _db;
    private readonly AccountRepository _accountRepo;
    private readonly CredentialEncryptor _encryptor;
    private readonly QueueManager _manager;

    public QueueManagerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _accountsPath = Path.Combine(Path.GetTempPath(), $"test_accounts_{Guid.NewGuid()}.json");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _encryptor = new CredentialEncryptor("test-passphrase");

        // Insert a test account with implicit confirm (default) via AccountsStore
        var store = new AccountsStore(_accountsPath);
        _accountRepo = new AccountRepository(store);
        _accountRepo.Insert("test", "Test", "imap.test.com", 993,
            null, 587, false, "u@test.com", "password",
            _encryptor.Encrypt("testpass"), "generic", null);

        var repo = new QueueRepository(_db);
        _manager = new QueueManager(repo, _accountRepo, _encryptor);
    }

    [Fact]
    public void EnqueueSend_ImplicitConfirm_SetsSendsAt()
    {
        var result = _manager.EnqueueSend("test", """{"to":"bob@test.com","subject":"Hi","body":"Hello"}""");
        Assert.NotNull(result.PendingId);
        Assert.Equal("implicit", result.ConfirmMode);
        Assert.NotNull(result.SendsAt);
    }

    [Fact]
    public void EnqueueSend_ExplicitConfirm_NoSendsAt()
    {
        // Insert a second account with explicit confirm in config_json
        _accountRepo.Insert("explicit-acct", "Explicit", "imap.test.com", 993,
            null, 587, false, "u@test.com", "password",
            _encryptor.Encrypt("testpass"), "generic",
            """{"confirm_mode":"explicit"}""");

        var manager = new QueueManager(new QueueRepository(_db), _accountRepo, _encryptor);

        var result = manager.EnqueueSend("explicit-acct", """{"to":"bob@test.com"}""");
        Assert.Equal("explicit", result.ConfirmMode);
        Assert.Null(result.SendsAt);
        Assert.Equal("pending", result.Status);
    }

    [Fact]
    public void EnqueueOperation_P1_AutoConfirmed()
    {
        var id = _manager.EnqueueOperation("test", OperationType.Delete, """{"uids":[1,2]}""");
        var op = _manager.GetOperation(id);
        Assert.Equal("confirmed", op!.Status);  // P1 ops auto-confirm
    }

    [Fact]
    public void Confirm_PendingSend_ChangesToConfirmed()
    {
        // Insert a second account with explicit confirm
        _accountRepo.Insert("explicit-acct2", "Explicit2", "imap.test.com", 993,
            null, 587, false, "u@test.com", "password",
            _encryptor.Encrypt("testpass"), "generic",
            """{"confirm_mode":"explicit"}""");

        var manager = new QueueManager(new QueueRepository(_db), _accountRepo, _encryptor);

        var result = manager.EnqueueSend("explicit-acct2", """{"to":"bob@test.com"}""");
        var confirmed = manager.Confirm(result.PendingId);
        Assert.True(confirmed);

        var op = manager.GetOperation(result.PendingId);
        Assert.Equal("confirmed", op!.Status);
    }

    [Fact]
    public void Cancel_PendingOperation_Succeeds()
    {
        var result = _manager.EnqueueSend("test", """{"to":"bob@test.com"}""");
        var cancelled = _manager.Cancel(result.PendingId);
        Assert.True(cancelled);
    }

    [Fact]
    public void ListPending_ReturnsAllPendingAndConfirmed()
    {
        _manager.EnqueueSend("test", """{"to":"a@test.com"}""");
        _manager.EnqueueOperation("test", OperationType.Delete, """{"uids":[1]}""");

        var pending = _manager.ListPending("test");
        Assert.Equal(2, pending.Count);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
        if (File.Exists(_accountsPath)) File.Delete(_accountsPath);
    }
}
