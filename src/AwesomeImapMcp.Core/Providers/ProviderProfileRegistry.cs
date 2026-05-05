// src/AwesomeImapMcp.Core/Providers/ProviderProfileRegistry.cs
namespace AwesomeImapMcp.Core.Providers;
using AwesomeImapMcp.Core.Types;

public class ProviderProfileRegistry
{
    private readonly Dictionary<ProviderType, ProviderProfile> _profiles;

    public ProviderProfileRegistry()
    {
        _profiles = new Dictionary<ProviderType, ProviderProfile>
        {
            [ProviderType.Gmail] = new ProviderProfile
            {
                Type = ProviderType.Gmail,
                Name = "Gmail",
                FolderMap = new Dictionary<FolderRole, string>
                {
                    [FolderRole.Inbox]   = "INBOX",
                    [FolderRole.Sent]    = "[Gmail]/Sent Mail",
                    [FolderRole.Drafts]  = "[Gmail]/Drafts",
                    [FolderRole.Trash]   = "[Gmail]/Trash",
                    [FolderRole.Spam]    = "[Gmail]/Spam",
                    [FolderRole.Archive] = "[Gmail]/All Mail",
                },
                SupportedAuth = [AuthMethod.AppPassword, AuthMethod.OAuth2],
                Search = SearchCapabilities.BasicSearch | SearchCapabilities.BodySearch,
                MaxConnections = 5,
            },

            [ProviderType.Outlook] = new ProviderProfile
            {
                Type = ProviderType.Outlook,
                Name = "Outlook",
                FolderMap = new Dictionary<FolderRole, string>
                {
                    [FolderRole.Inbox]   = "INBOX",
                    [FolderRole.Sent]    = "Sent Items",
                    [FolderRole.Drafts]  = "Drafts",
                    [FolderRole.Trash]   = "Deleted Items",
                    [FolderRole.Spam]    = "Junk Email",
                    [FolderRole.Archive] = "Archive",
                },
                SupportedAuth = [AuthMethod.Password, AuthMethod.OAuth2],
                Search = SearchCapabilities.BasicSearch | SearchCapabilities.BodySearch,
                MaxConnections = 5,
            },

            [ProviderType.Fastmail] = new ProviderProfile
            {
                Type = ProviderType.Fastmail,
                Name = "Fastmail",
                FolderMap = new Dictionary<FolderRole, string>
                {
                    [FolderRole.Inbox]   = "INBOX",
                    [FolderRole.Sent]    = "Sent",
                    [FolderRole.Drafts]  = "Drafts",
                    [FolderRole.Trash]   = "Trash",
                    [FolderRole.Spam]    = "Junk Mail",
                    [FolderRole.Archive] = "Archive",
                },
                SupportedAuth = [AuthMethod.AppPassword],
                Search = SearchCapabilities.BasicSearch | SearchCapabilities.BodySearch | SearchCapabilities.FuzzySearch,
                MaxConnections = 5,
            },

            [ProviderType.ProtonMail] = new ProviderProfile
            {
                Type = ProviderType.ProtonMail,
                Name = "ProtonMail",
                FolderMap = new Dictionary<FolderRole, string>
                {
                    [FolderRole.Inbox]   = "INBOX",
                    [FolderRole.Sent]    = "Sent",
                    [FolderRole.Drafts]  = "Drafts",
                    [FolderRole.Trash]   = "Trash",
                    [FolderRole.Spam]    = "Spam",
                    [FolderRole.Archive] = "Archive",
                },
                SupportedAuth = [AuthMethod.AppPassword],
                Search = SearchCapabilities.BasicSearch,
                MaxConnections = 3,
                RequiresTlsTrust = true,
            },

            [ProviderType.Yahoo] = new ProviderProfile
            {
                Type = ProviderType.Yahoo,
                Name = "Yahoo",
                FolderMap = new Dictionary<FolderRole, string>
                {
                    [FolderRole.Inbox]   = "INBOX",
                    [FolderRole.Sent]    = "Sent",
                    [FolderRole.Drafts]  = "Draft",
                    [FolderRole.Trash]   = "Trash",
                    [FolderRole.Spam]    = "Bulk Mail",
                    [FolderRole.Archive] = "Archive",
                },
                SupportedAuth = [AuthMethod.AppPassword],
                Search = SearchCapabilities.BasicSearch | SearchCapabilities.BodySearch,
                MaxConnections = 5,
            },

            [ProviderType.Generic] = new ProviderProfile
            {
                Type = ProviderType.Generic,
                Name = "Generic",
                FolderMap = new Dictionary<FolderRole, string>
                {
                    [FolderRole.Inbox]   = "INBOX",
                    [FolderRole.Sent]    = "Sent",
                    [FolderRole.Drafts]  = "Drafts",
                    [FolderRole.Trash]   = "Trash",
                    [FolderRole.Spam]    = "Spam",
                    [FolderRole.Archive] = "Archive",
                },
                SupportedAuth = [AuthMethod.Password, AuthMethod.AppPassword],
                Search = SearchCapabilities.BasicSearch,
                MaxConnections = 5,
            },
        };
    }

    public ProviderProfile GetProfile(ProviderType type) => _profiles[type];

    public ProviderProfile DetectFromHost(string host)
    {
        var lower = host.ToLowerInvariant();

        if (lower.Contains("gmail") || lower.Contains("googlemail"))
            return _profiles[ProviderType.Gmail];

        if (lower.Contains("outlook") || lower.Contains("office365") || lower.Contains("hotmail"))
            return _profiles[ProviderType.Outlook];

        if (lower.Contains("fastmail"))
            return _profiles[ProviderType.Fastmail];

        if (lower.Contains("protonmail"))
            return _profiles[ProviderType.ProtonMail];

        if (lower.Contains("yahoo") || lower.Contains("aol"))
            return _profiles[ProviderType.Yahoo];

        return _profiles[ProviderType.Generic];
    }

    public ProviderProfile GetProfileByName(string providerName)
    {
        if (Enum.TryParse<ProviderType>(providerName, ignoreCase: true, out var type) && _profiles.ContainsKey(type))
            return _profiles[type];

        return _profiles[ProviderType.Generic];
    }
}
