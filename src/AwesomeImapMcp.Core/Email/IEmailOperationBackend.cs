namespace AwesomeImapMcp.Core.Email;

/// <summary>
/// Abstraction for the write/operation side of an email backend.
/// Handles sending, moving, deleting, and flagging messages.
/// </summary>
public interface IEmailOperationBackend : IAsyncDisposable
{
    /// <summary>The backend type identifier (e.g., "imap").</summary>
    string BackendType { get; }

    /// <summary>Send an email message via this backend.</summary>
    Task SendAsync(string accountId, EmailMessage message, CancellationToken ct = default);

    /// <summary>Move messages by UID from one folder to another.</summary>
    Task MoveAsync(string accountId, IReadOnlyList<long> uids, string fromFolder, string toFolder,
        CancellationToken ct = default);

    /// <summary>Delete messages by UID from a folder.</summary>
    Task DeleteAsync(string accountId, IReadOnlyList<long> uids, string folder,
        CancellationToken ct = default);

    /// <summary>Set or remove flags on messages by UID.</summary>
    Task SetFlagsAsync(string accountId, IReadOnlyList<long> uids, string folder,
        MessageAction action, CancellationToken ct = default);
}

/// <summary>Represents an email message to be sent.</summary>
public record EmailMessage(
    string To,
    string Subject,
    string Body,
    string? Cc = null,
    string? Bcc = null,
    string? InReplyTo = null);

/// <summary>Actions that can be performed on message flags.</summary>
public enum MessageAction
{
    MarkRead,
    MarkUnread,
    Flag,
    Unflag
}
