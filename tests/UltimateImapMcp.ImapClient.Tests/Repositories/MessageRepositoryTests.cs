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

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
