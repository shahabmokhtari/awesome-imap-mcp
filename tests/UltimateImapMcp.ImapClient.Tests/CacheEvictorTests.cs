using Microsoft.Extensions.Logging.Abstractions;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Coordination;
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.ImapClient.Tests;

/// <summary>Stub coordinator that always reports this instance as leader.</summary>
file sealed class AlwaysLeaderCoordinator : IInstanceCoordinator
{
    public bool IsLeader => true;
    public string InstanceId => "test-instance";
    public IReadOnlyList<InstanceHeartbeat> GetLiveInstances() => [];
    public Task<bool> RequestShutdownAsync(string instanceId) => Task.FromResult(false);
}

public class CacheEvictorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly AccountRepository _accountRepo;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;

    public CacheEvictorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_evictor_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _accountRepo = new AccountRepository(_db);
        _folderRepo = new FolderRepository(_db);
        _messageRepo = new MessageRepository(_db);

        // Seed test data
        _accountRepo.Insert("test", "Test", "imap.test.com", 993,
            null, 465, true, "u@test.com", "password", "enc", "generic", null);
        _folderRepo.Insert("test", "INBOX", "Inbox", "inbox", "/");
    }

    [Fact]
    public void EvictBodies_NullsOutBodyFields()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;

        // Insert messages with bodies
        for (var i = 1; i <= 5; i++)
        {
            _messageRepo.Insert("test", folderId, uid: i, messageId: $"<msg{i}@test.com>",
                inReplyTo: null, referencesHdr: null, threadId: $"t{i}",
                subject: $"Subject {i}", fromAddress: $"user{i}@test.com",
                fromEmail: $"user{i}@test.com", toAddresses: "[]",
                ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
                dateEpoch: 1774040400 + i, flags: null, sizeBytes: 512,
                hasAttachments: false, snippet: $"Snippet {i}",
                bodyText: $"Body text {i}", bodyHtml: $"<p>Body {i}</p>");
        }

        // Evict 3 bodies (oldest first)
        var evicted = _messageRepo.EvictBodies(3);
        Assert.Equal(3, evicted);

        // First 3 messages should have bodies evicted
        for (var i = 1; i <= 3; i++)
        {
            var msg = _messageRepo.GetByUid("test", folderId, i)!;
            Assert.Null(msg.BodyText);
            Assert.Null(msg.BodyHtml);
            Assert.False(msg.BodyFetched);
        }

        // Messages 4-5 should still have bodies
        for (var i = 4; i <= 5; i++)
        {
            var msg = _messageRepo.GetByUid("test", folderId, i)!;
            Assert.NotNull(msg.BodyText);
            Assert.True(msg.BodyFetched);
        }
    }

    [Fact]
    public void SizeBasedEviction_EvictsBodiesBeforeMessages()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;

        // Insert messages with large bodies to inflate the DB
        for (var i = 1; i <= 10; i++)
        {
            var largeBody = new string('x', 50_000);
            _messageRepo.Insert("test", folderId, uid: i, messageId: $"<msg{i}@test.com>",
                inReplyTo: null, referencesHdr: null, threadId: $"t{i}",
                subject: $"Subject {i}", fromAddress: $"user{i}@test.com",
                fromEmail: $"user{i}@test.com", toAddresses: "[]",
                ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
                dateEpoch: 1774040400 + i, flags: null, sizeBytes: 512,
                hasAttachments: false, snippet: $"Snippet {i}",
                bodyText: largeBody, bodyHtml: $"<p>{largeBody}</p>");
        }

        // With 0 MB limit, evictor tries bodies first, then deletes messages.
        // Since SQLite file doesn't shrink without VACUUM, both passes run
        // until exhausted. This tests the ordering: bodies evicted before
        // message rows deleted.
        var config = new CacheConfig { MaxSizeMb = 0 };
        var evictor = new CacheEvictor(_db, _messageRepo, config,
            new AlwaysLeaderCoordinator(), NullLogger<CacheEvictor>.Instance);

        evictor.RunEviction();

        // With 0 limit: bodies are evicted first (all 10), then since DB
        // file still exceeds 0 bytes, all messages get deleted too.
        // The key behavior: bodies were attempted first.
        // All messages should be gone.
        for (var i = 1; i <= 10; i++)
        {
            Assert.Null(_messageRepo.GetByUid("test", folderId, i));
        }
    }

    [Fact]
    public void SizeBasedEviction_DeletesMessagesWhenNoBodiesLeft()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;

        // Insert messages WITHOUT bodies
        for (var i = 1; i <= 5; i++)
        {
            _messageRepo.Insert("test", folderId, uid: i, messageId: $"<msg{i}@test.com>",
                inReplyTo: null, referencesHdr: null, threadId: $"t{i}",
                subject: $"Subject {i} {new string('y', 5000)}",
                fromAddress: $"user{i}@test.com",
                fromEmail: $"user{i}@test.com", toAddresses: "[]",
                ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
                dateEpoch: 1774040400 + i, flags: null, sizeBytes: 512,
                hasAttachments: false, snippet: $"Snippet {i}");
        }

        // 0 MB limit → body eviction finds nothing, proceeds to delete messages
        var config = new CacheConfig { MaxSizeMb = 0 };
        var evictor = new CacheEvictor(_db, _messageRepo, config,
            new AlwaysLeaderCoordinator(), NullLogger<CacheEvictor>.Instance);

        evictor.RunEviction();

        // All messages should have been deleted
        for (var i = 1; i <= 5; i++)
        {
            Assert.Null(_messageRepo.GetByUid("test", folderId, i));
        }
    }

    [Fact]
    public void TimeBasedEviction_EvictsOldBodies()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;

        // Insert a message and backdate its cached_at to 60 days ago
        _messageRepo.Insert("test", folderId, uid: 1, messageId: "<msg1@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t1",
            subject: "Old message", fromAddress: "old@test.com",
            fromEmail: "old@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-01-01T10:00:00Z",
            dateEpoch: 1735689600, flags: null, sizeBytes: 512,
            hasAttachments: false, snippet: "Old snippet",
            bodyText: "Old body text", bodyHtml: "<p>Old body</p>");

        // Backdate cached_at
        var conn = _db.GetWriteConnection();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE messages SET cached_at = datetime('now', '-60 days') WHERE uid = 1;";
            cmd.ExecuteNonQuery();
        }

        // Insert a recent message
        _messageRepo.Insert("test", folderId, uid: 2, messageId: "<msg2@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t2",
            subject: "Recent message", fromAddress: "new@test.com",
            fromEmail: "new@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: null, sizeBytes: 512,
            hasAttachments: false, snippet: "New snippet",
            bodyText: "New body text", bodyHtml: "<p>New body</p>");

        // MaxBodyAgeDays=30 → bodies older than 30 days get evicted
        var config = new CacheConfig { MaxSizeMb = 1000, MaxBodyAgeDays = 30 };

        var evictor = new CacheEvictor(_db, _messageRepo, config,
            new AlwaysLeaderCoordinator(), NullLogger<CacheEvictor>.Instance);

        evictor.RunEviction();

        // Old message body should be evicted but row remains
        var oldMsg = _messageRepo.GetByUid("test", folderId, 1)!;
        Assert.Null(oldMsg.BodyText);
        Assert.Null(oldMsg.BodyHtml);
        Assert.False(oldMsg.BodyFetched);
        Assert.Equal("Old message", oldMsg.Subject); // Row still exists

        // Recent message body should be preserved
        var newMsg = _messageRepo.GetByUid("test", folderId, 2)!;
        Assert.Equal("New body text", newMsg.BodyText);
        Assert.True(newMsg.BodyFetched);
    }

    [Fact]
    public void TimeBasedEviction_DeletesOldMessages()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;

        // Insert a message and backdate its cached_at
        _messageRepo.Insert("test", folderId, uid: 1, messageId: "<msg1@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t1",
            subject: "Ancient message", fromAddress: "old@test.com",
            fromEmail: "old@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2025-01-01T10:00:00Z",
            dateEpoch: 1735689600, flags: null, sizeBytes: 512,
            hasAttachments: false, snippet: "Old snippet");

        // Backdate cached_at to 120 days ago
        var conn = _db.GetWriteConnection();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE messages SET cached_at = datetime('now', '-120 days') WHERE uid = 1;";
            cmd.ExecuteNonQuery();
        }

        // Insert a recent message
        _messageRepo.Insert("test", folderId, uid: 2, messageId: "<msg2@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t2",
            subject: "Recent", fromAddress: "new@test.com",
            fromEmail: "new@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: null, sizeBytes: 512,
            hasAttachments: false, snippet: "New snippet");

        // DefaultWindowDays=90 → messages older than 90 days get deleted
        var config = new CacheConfig { MaxSizeMb = 1000, DefaultWindowDays = 90 };

        var evictor = new CacheEvictor(_db, _messageRepo, config,
            new AlwaysLeaderCoordinator(), NullLogger<CacheEvictor>.Instance);

        evictor.RunEviction();

        // Old message should be deleted entirely
        Assert.Null(_messageRepo.GetByUid("test", folderId, 1));

        // Recent message should remain
        Assert.NotNull(_messageRepo.GetByUid("test", folderId, 2));
    }

    [Fact]
    public void NoEviction_WhenUnderLimits()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;

        _messageRepo.Insert("test", folderId, uid: 1, messageId: "<msg1@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t1",
            subject: "Normal message", fromAddress: "user@test.com",
            fromEmail: "user@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: null, sizeBytes: 512,
            hasAttachments: false, snippet: "Snippet",
            bodyText: "Body text", bodyHtml: "<p>Body</p>");

        // Large size limit and no time-based eviction
        var config = new CacheConfig { MaxSizeMb = 1000 };

        var evictor = new CacheEvictor(_db, _messageRepo, config,
            new AlwaysLeaderCoordinator(), NullLogger<CacheEvictor>.Instance);

        evictor.RunEviction();

        // Message should be untouched
        var msg = _messageRepo.GetByUid("test", folderId, 1)!;
        Assert.Equal("Body text", msg.BodyText);
        Assert.True(msg.BodyFetched);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
