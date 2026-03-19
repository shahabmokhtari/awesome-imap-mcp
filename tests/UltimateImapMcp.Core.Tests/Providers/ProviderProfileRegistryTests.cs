// tests/UltimateImapMcp.Core.Tests/Providers/ProviderProfileRegistryTests.cs
using UltimateImapMcp.Core.Providers;
using UltimateImapMcp.Core.Types;

namespace UltimateImapMcp.Core.Tests.Providers;

public class ProviderProfileRegistryTests
{
    private readonly ProviderProfileRegistry _registry = new();

    [Theory]
    [InlineData("imap.gmail.com", ProviderType.Gmail)]
    [InlineData("imap.googlemail.com", ProviderType.Gmail)]
    [InlineData("outlook.office365.com", ProviderType.Outlook)]
    [InlineData("imap.fastmail.com", ProviderType.Fastmail)]
    [InlineData("imap.mail.yahoo.com", ProviderType.Yahoo)]
    [InlineData("127.0.0.1", ProviderType.Generic)]
    [InlineData("imap.example.com", ProviderType.Generic)]
    public void DetectFromHost_ReturnsCorrectProvider(string host, ProviderType expected)
    {
        var profile = _registry.DetectFromHost(host);
        Assert.Equal(expected, profile.Type);
    }

    [Fact]
    public void GetProfile_Gmail_HasCorrectFolderMap()
    {
        var profile = _registry.GetProfile(ProviderType.Gmail);
        Assert.Equal("[Gmail]/Sent Mail", profile.FolderMap[FolderRole.Sent]);
        Assert.Equal("[Gmail]/Trash", profile.FolderMap[FolderRole.Trash]);
        Assert.Equal("[Gmail]/Drafts", profile.FolderMap[FolderRole.Drafts]);
    }

    [Fact]
    public void GetProfile_Outlook_HasCorrectFolderMap()
    {
        var profile = _registry.GetProfile(ProviderType.Outlook);
        Assert.Equal("Sent Items", profile.FolderMap[FolderRole.Sent]);
        Assert.Equal("Deleted Items", profile.FolderMap[FolderRole.Trash]);
    }

    [Fact]
    public void GetProfile_AllProviders_HaveInbox()
    {
        foreach (var type in Enum.GetValues<ProviderType>())
        {
            var profile = _registry.GetProfile(type);
            Assert.Equal("INBOX", profile.FolderMap[FolderRole.Inbox]);
        }
    }
}
