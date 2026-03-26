using UltimateImapMcp.Core.Database;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.ImapClient.Tests.Repositories;

public class MessageRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly AccountRepository _accountRepo;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;

    public MessageRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
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
    public void Insert_And_GetByUid_ReturnsMessage()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        _messageRepo.Insert("test", folderId, uid: 1, messageId: "<msg1@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "thread1",
            subject: "Hello World", fromAddress: "Alice <alice@test.com>",
            fromEmail: "alice@test.com", toAddresses: "[\"bob@test.com\"]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[\"\\\\Seen\"]", sizeBytes: 1024,
            hasAttachments: false, snippet: "Hello, this is a test email");

        var msg = _messageRepo.GetByUid("test", folderId, 1);
        Assert.NotNull(msg);
        Assert.Equal("Hello World", msg.Subject);
        Assert.Equal("alice@test.com", msg.FromEmail);
    }

    [Fact]
    public void SearchFts_FindsBySubject()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        _messageRepo.Insert("test", folderId, uid: 1, messageId: "<msg1@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t1",
            subject: "Meeting tomorrow at 3pm", fromAddress: "Alice <alice@test.com>",
            fromEmail: "alice@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[]", sizeBytes: 512,
            hasAttachments: false, snippet: "Let's meet tomorrow");

        _messageRepo.Insert("test", folderId, uid: 2, messageId: "<msg2@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t2",
            subject: "Invoice #1234", fromAddress: "Bob <bob@test.com>",
            fromEmail: "bob@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T11:00:00Z",
            dateEpoch: 1774044000, flags: "[]", sizeBytes: 2048,
            hasAttachments: true, snippet: "Please find attached invoice");

        var results = _messageRepo.SearchFts("meeting", accountId: "test", maxResults: 10);
        Assert.Single(results);
        Assert.Equal("Meeting tomorrow at 3pm", results[0].Subject);
    }

    [Fact]
    public void GetByThreadId_ReturnsThreadMessages()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        _messageRepo.Insert("test", folderId, uid: 1, messageId: "<msg1@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "thread-abc",
            subject: "Original", fromAddress: "Alice <alice@test.com>",
            fromEmail: "alice@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[]", sizeBytes: 512,
            hasAttachments: false, snippet: "First message");

        _messageRepo.Insert("test", folderId, uid: 2, messageId: "<msg2@test.com>",
            inReplyTo: "<msg1@test.com>", referencesHdr: "<msg1@test.com>",
            threadId: "thread-abc",
            subject: "Re: Original", fromAddress: "Bob <bob@test.com>",
            fromEmail: "bob@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T11:00:00Z",
            dateEpoch: 1774044000, flags: "[]", sizeBytes: 1024,
            hasAttachments: false, snippet: "Reply here");

        var thread = _messageRepo.GetByThreadId("thread-abc");
        Assert.Equal(2, thread.Count);
        Assert.Equal("Original", thread[0].Subject);
        Assert.Equal("Re: Original", thread[1].Subject);
    }

    [Fact]
    public void UpdateBody_UpdatesBodyFields()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        _messageRepo.Insert("test", folderId, uid: 1, messageId: "<msg1@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t1",
            subject: "Test", fromAddress: "alice@test.com",
            fromEmail: "alice@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[]", sizeBytes: 512,
            hasAttachments: false, snippet: "Test snippet");

        var msg = _messageRepo.GetByUid("test", folderId, 1)!;
        Assert.False(msg.BodyFetched);

        _messageRepo.UpdateBody(msg.Id, "Plain text body", "<p>HTML body</p>");

        var updated = _messageRepo.GetByUid("test", folderId, 1)!;
        Assert.True(updated.BodyFetched);
        Assert.Equal("Plain text body", updated.BodyText);
        Assert.Equal("<p>HTML body</p>", updated.BodyHtml);
    }

    [Fact]
    public void GetMaxUid_ReturnsHighestUid()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        Assert.Equal(0, _messageRepo.GetMaxUid("test", folderId));

        _messageRepo.Insert("test", folderId, uid: 5, messageId: "<msg5@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t1",
            subject: "Test", fromAddress: "alice@test.com",
            fromEmail: "alice@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[]", sizeBytes: 512,
            hasAttachments: false, snippet: "Snippet");

        _messageRepo.Insert("test", folderId, uid: 10, messageId: "<msg10@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t2",
            subject: "Test 2", fromAddress: "bob@test.com",
            fromEmail: "bob@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T11:00:00Z",
            dateEpoch: 1774044000, flags: "[]", sizeBytes: 1024,
            hasAttachments: false, snippet: "Snippet 2");

        Assert.Equal(10, _messageRepo.GetMaxUid("test", folderId));
    }

    [Fact]
    public void SearchFts_AfterUpdateBody_FindsByBodyContent()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        _messageRepo.Insert("test", folderId, uid: 100, messageId: "<body-test@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t-body",
            subject: "Generic Subject", fromAddress: "Charlie <charlie@test.com>",
            fromEmail: "charlie@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[]", sizeBytes: 512,
            hasAttachments: false, snippet: "Original snippet");

        var msg = _messageRepo.GetByUid("test", folderId, 100)!;
        _messageRepo.UpdateBody(msg.Id, "The xylophone orchestra performed brilliantly", null);

        // FTS update trigger should allow searching the new body text
        var results = _messageRepo.SearchFts("xylophone", accountId: "test", maxResults: 10);
        Assert.Single(results);
        Assert.Equal("Generic Subject", results[0].Subject);
    }

    [Fact]
    public void SearchFts_AfterDelete_ReturnsEmpty()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        _messageRepo.Insert("test", folderId, uid: 200, messageId: "<del-test@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t-del",
            subject: "Ephemeral platypus message", fromAddress: "Dave <dave@test.com>",
            fromEmail: "dave@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[]", sizeBytes: 256,
            hasAttachments: false, snippet: "Platypus content");

        // Verify it's searchable before deletion
        var before = _messageRepo.SearchFts("platypus", accountId: "test", maxResults: 10);
        Assert.Single(before);

        // Delete the message directly (triggers FTS delete trigger)
        _db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM messages WHERE uid = 200 AND account_id = 'test';";
            cmd.ExecuteNonQuery();
        });

        var after = _messageRepo.SearchFts("platypus", accountId: "test", maxResults: 10);
        Assert.Empty(after);
    }

    [Fact]
    public void SearchFts_ByFromAddress_FindsMessage()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        _messageRepo.Insert("test", folderId, uid: 300, messageId: "<from-test@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t-from",
            subject: "Normal subject line", fromAddress: "Zephyrine <zephyrine@exotic-domain.com>",
            fromEmail: "zephyrine@exotic-domain.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[]", sizeBytes: 1024,
            hasAttachments: false, snippet: "Some snippet");

        var results = _messageRepo.SearchFts("zephyrine", accountId: "test", maxResults: 10);
        Assert.Single(results);
        Assert.Equal("zephyrine@exotic-domain.com", results[0].FromEmail);
    }

    // ---------------------------------------------------------------
    // Delete methods
    // ---------------------------------------------------------------

    [Fact]
    public void DeleteAll_RemovesAllMessages_ReturnsCount()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        InsertTestMessage("test", folderId, 501, "msg-501");
        InsertTestMessage("test", folderId, 502, "msg-502");

        var deleted = _messageRepo.DeleteAll();
        Assert.True(deleted >= 2);
        Assert.Null(_messageRepo.GetByUid("test", folderId, 501));
        Assert.Null(_messageRepo.GetByUid("test", folderId, 502));
    }

    [Fact]
    public void DeleteByAccount_OnlyRemovesTargetAccount()
    {
        _accountRepo.Insert("other", "Other", "imap.other.com", 993,
            null, 465, true, "u@other.com", "password", "enc", "generic", null);
        _folderRepo.Insert("other", "INBOX", "Inbox", "inbox", "/");
        var testFolderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        var otherFolderId = _folderRepo.GetByPath("other", "INBOX")!.Id;

        InsertTestMessage("test", testFolderId, 601, "msg-601");
        InsertTestMessage("other", otherFolderId, 602, "msg-602");

        var deleted = _messageRepo.DeleteByAccount("test");
        Assert.True(deleted >= 1);
        Assert.Null(_messageRepo.GetByUid("test", testFolderId, 601));
        Assert.NotNull(_messageRepo.GetByUid("other", otherFolderId, 602));
    }

    [Fact]
    public void DeleteByFolder_OnlyRemovesTargetFolder()
    {
        _folderRepo.Insert("test", "Sent", "Sent", "sent", "/");
        var inboxId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        var sentId = _folderRepo.GetByPath("test", "Sent")!.Id;

        InsertTestMessage("test", inboxId, 701, "msg-701");
        InsertTestMessage("test", sentId, 702, "msg-702");

        var deleted = _messageRepo.DeleteByFolder("test", inboxId);
        Assert.True(deleted >= 1);
        Assert.Null(_messageRepo.GetByUid("test", inboxId, 701));
        Assert.NotNull(_messageRepo.GetByUid("test", sentId, 702));
    }

    [Fact]
    public void DeleteByAccount_EmptyTable_ReturnsZero()
    {
        // Delete any seed data first
        _messageRepo.DeleteAll();

        var deleted = _messageRepo.DeleteByAccount("nonexistent");
        Assert.Equal(0, deleted);
    }

    private void InsertTestMessage(string accountId, int folderId, long uid, string msgId)
    {
        _messageRepo.Insert(accountId, folderId, uid: uid, messageId: $"<{msgId}@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: $"t-{msgId}",
            subject: $"Subject {msgId}", fromAddress: "Test <test@test.com>",
            fromEmail: "test@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[]", sizeBytes: 1024,
            hasAttachments: false, snippet: "test snippet");
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
