// src/AwesomeImapMcp.Core/Types/Enums.cs
namespace AwesomeImapMcp.Core.Types;

public enum FolderRole { Inbox, Sent, Drafts, Trash, Spam, Archive }
public enum AuthMethod { Password, AppPassword, OAuth2 }
public enum ProviderType { Gmail, Outlook, Fastmail, ProtonMail, Yahoo, Generic }

[Flags]
public enum SearchCapabilities
{
    None = 0, BasicSearch = 1, BodySearch = 2,
    FuzzySearch = 4, SortExtension = 8, ThreadExtension = 16
}
