using AwesomeImapMcp.Core.Providers;
using AwesomeImapMcp.Core.Types;
using AwesomeImapMcp.ImapClient;

namespace AwesomeImapMcp.ImapClient.Tests;

public class FolderMapperTests
{
    private readonly ProviderProfileRegistry _registry = new();

    [Theory]
    [InlineData(ProviderType.Gmail, FolderRole.Sent, "[Gmail]/Sent Mail")]
    [InlineData(ProviderType.Outlook, FolderRole.Trash, "Deleted Items")]
    [InlineData(ProviderType.Generic, FolderRole.Inbox, "INBOX")]
    public void GetPath_ReturnsProviderSpecificPath(ProviderType provider, FolderRole role, string expected)
    {
        var profile = _registry.GetProfile(provider);
        var mapper = new FolderMapper(profile);
        Assert.Equal(expected, mapper.GetPath(role));
    }

    [Fact]
    public void DetectRole_MatchesKnownPaths()
    {
        var profile = _registry.GetProfile(ProviderType.Gmail);
        var mapper = new FolderMapper(profile);

        Assert.Equal(FolderRole.Sent, mapper.DetectRole("[Gmail]/Sent Mail"));
        Assert.Equal(FolderRole.Inbox, mapper.DetectRole("INBOX"));
        Assert.Null(mapper.DetectRole("Custom Folder"));
    }

    [Fact]
    public void GetDisplayName_WithRole_ReturnsRoleName()
    {
        var profile = _registry.GetProfile(ProviderType.Gmail);
        var mapper = new FolderMapper(profile);

        Assert.Equal("Sent", mapper.GetDisplayName("[Gmail]/Sent Mail", FolderRole.Sent));
    }

    [Fact]
    public void GetDisplayName_WithoutRole_ReturnsLastSegment()
    {
        var profile = _registry.GetProfile(ProviderType.Gmail);
        var mapper = new FolderMapper(profile);

        Assert.Equal("Custom Folder", mapper.GetDisplayName("Custom Folder", null));
    }
}
