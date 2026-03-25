using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Tests;

public class QueueManagerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly AccountRepository _accountRepo;
    private readonly CredentialEncryptor _encryptor;
    private readonly QueueManager _manager;

    public QueueManagerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _encryptor = new CredentialEncryptor("test-passphrase");

        // Insert a test account with implicit confirm (default)
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO accounts (id, name, imap_host, username, auth_type, credentials_enc) VALUES ('test', 'Test', 'imap.test.com', 'u@test.com', 'password', $cred);";
        cmd.Parameters.AddWithValue("$cred", _encryptor.Encrypt("testpass"));
        cmd.ExecuteNonQuery();

        var repo = new QueueRepository(_db);
        _accountRepo = new AccountRepository(_db);
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
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR IGNORE INTO accounts (id, name, imap_host, username, auth_type, credentials_enc, config_json)
            VALUES ('explicit-acct', 'Explicit', 'imap.test.com', 'u@test.com', 'password', $cred, '{""confirm_mode"":""explicit""}');";
        cmd.Parameters.AddWithValue("$cred", _encryptor.Encrypt("testpass"));
        cmd.ExecuteNonQuery();

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
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR IGNORE INTO accounts (id, name, imap_host, username, auth_type, credentials_enc, config_json)
            VALUES ('explicit-acct2', 'Explicit2', 'imap.test.com', 'u@test.com', 'password', $cred, '{""confirm_mode"":""explicit""}');";
        cmd.Parameters.AddWithValue("$cred", _encryptor.Encrypt("testpass"));
        cmd.ExecuteNonQuery();

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
    }
}
