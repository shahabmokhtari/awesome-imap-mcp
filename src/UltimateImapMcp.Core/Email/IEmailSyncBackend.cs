namespace UltimateImapMcp.Core.Email;

/// <summary>
/// Abstraction for the sync/read side of an email backend.
/// Implementations populate the local SQLite cache via repositories.
/// See docs/REST_BACKENDS.md for how to implement a new backend.
/// </summary>
public interface IEmailSyncBackend : IAsyncDisposable
{
    /// <summary>The backend type identifier (e.g., "imap", "zoho_rest").</summary>
    string BackendType { get; }

    /// <summary>Sync the folder list for the given account into the local cache.</summary>
    Task SyncFoldersAsync(string accountId, CancellationToken ct = default);

    /// <summary>Sync messages for a specific folder into the local cache.</summary>
    Task SyncFolderMessagesAsync(string accountId, string folderPath, CancellationToken ct = default);

    /// <summary>Fetch the full message body and store it in the local cache.</summary>
    Task FetchMessageBodyAsync(string accountId, string folderPath, long uid, CancellationToken ct = default);

    /// <summary>
    /// Fetches message bodies in batch for the given UIDs in one IMAP session.
    /// Returns the count of bodies successfully fetched.
    /// </summary>
    Task<int> FetchMessageBodiesBatchAsync(string accountId, string folderPath,
        IReadOnlyList<long> uids, CancellationToken ct = default)
    {
        throw new NotSupportedException($"Batch body fetch is not supported by the {BackendType} backend.");
    }

    /// <summary>Whether this backend supports real-time push notifications (e.g., IMAP IDLE).</summary>
    bool SupportsRealtimeSync { get; }

    /// <summary>
    /// Starts a real-time listener for changes in a folder.
    /// When changes are detected, <paramref name="onChangesDetected"/> is invoked.
    /// Only called when <see cref="SupportsRealtimeSync"/> is true.
    /// </summary>
    Task StartRealtimeListenerAsync(string accountId, string folderPath,
        Func<Task> onChangesDetected, CancellationToken ct = default);

    /// <summary>
    /// Downloads a specific attachment from a message and saves it to disk.
    /// Returns the number of bytes written.
    /// </summary>
    Task<long> DownloadAttachmentAsync(string accountId, string folderPath, long uid,
        string? targetFilename, string? contentId, string savePath, CancellationToken ct = default)
    {
        throw new NotSupportedException($"Attachment download is not supported by the {BackendType} backend.");
    }
}
