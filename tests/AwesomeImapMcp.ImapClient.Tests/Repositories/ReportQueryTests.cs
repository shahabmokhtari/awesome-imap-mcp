using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.ImapClient.Tests.Repositories;

public class ReportQueryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _accountsPath;
    private readonly AppDatabase _db;
    private readonly AccountRepository _accountRepo;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;

    public ReportQueryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_report_{Guid.NewGuid()}.db");
        _accountsPath = Path.Combine(Path.GetTempPath(), $"test_accounts_{Guid.NewGuid()}.json");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        var store = new AccountsStore(_accountsPath);
        _accountRepo = new AccountRepository(store);
        _folderRepo = new FolderRepository(_db);
        _messageRepo = new MessageRepository(_db);

        // Seed account and folders
        _accountRepo.Insert("test", "Test", "imap.test.com", 993,
            null, 465, true, "u@test.com", "password", "enc", "generic", null);
        _folderRepo.Insert("test", "INBOX", "Inbox", "inbox", "/");
        _folderRepo.Insert("test", "Sent", "Sent", "sent", "/");

        // Seed messages from multiple senders
        var inboxId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        var sentId = _folderRepo.GetByPath("test", "Sent")!.Id;
        var now = DateTimeOffset.UtcNow;

        // Inbox messages
        for (int i = 1; i <= 5; i++)
        {
            _messageRepo.Insert("test", inboxId, uid: i,
                messageId: $"<inbox{i}@test.com>",
                inReplyTo: null, referencesHdr: null, threadId: $"t{i}",
                subject: $"Test message {i}",
                fromAddress: "Alice <alice@example.com>",
                fromEmail: "alice@example.com",
                toAddresses: "[\"u@test.com\"]",
                ccAddresses: null, bccAddresses: null,
                date: now.AddDays(-i).ToString("O"),
                dateEpoch: now.AddDays(-i).ToUnixTimeSeconds(),
                flags: "[]", sizeBytes: 1024 * i,
                hasAttachments: i % 2 == 0, snippet: $"Message {i} body");
        }

        // More inbox messages from bob
        for (int i = 6; i <= 8; i++)
        {
            _messageRepo.Insert("test", inboxId, uid: i,
                messageId: $"<inbox{i}@test.com>",
                inReplyTo: null, referencesHdr: null, threadId: $"t{i}",
                subject: $"From Bob {i}",
                fromAddress: "Bob <bob@example.com>",
                fromEmail: "bob@example.com",
                toAddresses: "[\"u@test.com\"]",
                ccAddresses: null, bccAddresses: null,
                date: now.AddDays(-i).ToString("O"),
                dateEpoch: now.AddDays(-i).ToUnixTimeSeconds(),
                flags: "[]", sizeBytes: 2048,
                hasAttachments: false, snippet: $"Bob message {i}");
        }

        // Sent folder messages
        for (int i = 1; i <= 3; i++)
        {
            _messageRepo.Insert("test", sentId, uid: i,
                messageId: $"<sent{i}@test.com>",
                inReplyTo: null, referencesHdr: null, threadId: $"st{i}",
                subject: $"Sent message {i}",
                fromAddress: "Test <u@test.com>",
                fromEmail: "u@test.com",
                toAddresses: "[\"alice@example.com\"]",
                ccAddresses: null, bccAddresses: null,
                date: now.AddDays(-i).ToString("O"),
                dateEpoch: now.AddDays(-i).ToUnixTimeSeconds(),
                flags: "[]", sizeBytes: 512,
                hasAttachments: false, snippet: $"Sent {i}");
        }
    }

    [Fact]
    public void GetEmailVolume_ReturnsPerFolderStats()
    {
        var volume = _messageRepo.GetEmailVolume("test", days: 30);

        Assert.Equal(2, volume.Count);

        var inbox = volume.First(v => v.FolderPath == "INBOX");
        Assert.Equal(8, inbox.MessageCount);
        // Alice: 5 msgs (1024*1 + 1024*2 + 1024*3 + 1024*4 + 1024*5 = 15360)
        // Bob: 3 msgs (2048*3 = 6144)
        // Total = 21504
        Assert.Equal(21504, inbox.TotalSizeBytes);
        Assert.Equal(2, inbox.WithAttachments); // UIDs 2 and 4

        var sent = volume.First(v => v.FolderPath == "Sent");
        Assert.Equal(3, sent.MessageCount);
        Assert.Equal(1536, sent.TotalSizeBytes); // 3 * 512
    }

    [Fact]
    public void GetEmailVolume_WithShortPeriod_ExcludesOlderMessages()
    {
        // Only look at last 3 days — should exclude messages older than 3 days
        var volume = _messageRepo.GetEmailVolume("test", days: 3);
        var totalMessages = volume.Sum(v => v.MessageCount);
        // Messages at day -1, -2, -3 should be included
        // Day -1: uid 1 (alice), uid 1 (sent)
        // Day -2: uid 2 (alice), uid 2 (sent)
        // Day -3: uid 3 (alice), uid 3 (sent)
        Assert.True(totalMessages >= 4 && totalMessages <= 6,
            $"Expected 4-6 messages within 3 days, got {totalMessages}");
    }

    [Fact]
    public void GetEmailVolume_UnknownAccount_ReturnsEmpty()
    {
        var volume = _messageRepo.GetEmailVolume("nonexistent", days: 30);
        Assert.Empty(volume);
    }

    [Fact]
    public void GetTopSenders_ReturnsOrderedByCount()
    {
        var senders = _messageRepo.GetTopSenders("test", days: 30, limit: 10);

        Assert.Equal(3, senders.Count);

        // Alice sent 5, Bob sent 3, u@test.com sent 3
        Assert.Equal("alice@example.com", senders[0].FromEmail);
        Assert.Equal(5, senders[0].MessageCount);
    }

    [Fact]
    public void GetTopSenders_RespectsLimit()
    {
        var senders = _messageRepo.GetTopSenders("test", days: 30, limit: 1);
        Assert.Single(senders);
        Assert.Equal("alice@example.com", senders[0].FromEmail);
    }

    [Fact]
    public void GetTopSenders_WithShortPeriod_FiltersCorrectly()
    {
        var senders = _messageRepo.GetTopSenders("test", days: 2, limit: 10);
        // Only messages from last 2 days
        var totalMessages = senders.Sum(s => s.MessageCount);
        Assert.True(totalMessages >= 2 && totalMessages <= 4,
            $"Expected 2-4 messages within 2 days, got {totalMessages}");
    }

    [Fact]
    public void GetTopSenders_UnknownAccount_ReturnsEmpty()
    {
        var senders = _messageRepo.GetTopSenders("nonexistent", days: 30, limit: 10);
        Assert.Empty(senders);
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
