using MailKit;
using MimeKit;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.Core.Providers;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.RestBackend.Imap;

/// <summary>
/// Wraps the existing IMAP sync code behind <see cref="IEmailSyncBackend"/>.
/// Delegates all calls to <see cref="ImapSyncService"/> and <see cref="ImapConnectionManager"/>.
/// </summary>
internal sealed class ImapSyncBackend : IEmailSyncBackend
{
    private readonly ImapConnectionManager _connMgr;
    private readonly ImapSyncService _syncService;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;
    private readonly ProviderProfileRegistry _providerRegistry;
    private readonly AccountConfig _accountConfig;
    private readonly ILogger<ImapSyncBackend> _logger;

    public ImapSyncBackend(
        ImapConnectionManager connMgr,
        ImapSyncService syncService,
        FolderRepository folderRepo,
        MessageRepository messageRepo,
        ProviderProfileRegistry providerRegistry,
        AccountConfig accountConfig,
        ILogger<ImapSyncBackend> logger)
    {
        _connMgr = connMgr;
        _syncService = syncService;
        _folderRepo = folderRepo;
        _messageRepo = messageRepo;
        _providerRegistry = providerRegistry;
        _accountConfig = accountConfig;
        _logger = logger;
    }

    public string BackendType => "imap";
    public bool SupportsRealtimeSync => true;

    public async Task SyncFoldersAsync(string accountId, CancellationToken ct = default)
    {
        await _connMgr.ExecuteAsync(async client =>
        {
            var profile = _providerRegistry.GetProfileByName(_accountConfig.Provider);
            var mapper = new FolderMapper(profile);
            await _syncService.SyncFoldersAsync(client, accountId, mapper, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task SyncFolderMessagesAsync(string accountId, string folderPath,
        CancellationToken ct = default)
    {
        await _connMgr.ExecuteAsync(async client =>
        {
            var folder = _folderRepo.GetByPath(accountId, folderPath);
            if (folder is null)
            {
                _logger.LogWarning("Folder '{FolderPath}' not found in DB for account {AccountId}",
                    folderPath, accountId);
                return;
            }

            await _syncService.SyncFolderMessagesAsync(client, accountId, folder, ct: ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task FetchMessageBodyAsync(string accountId, string folderPath, long uid,
        CancellationToken ct = default)
    {
        // Check if body is already cached in the database — skip IMAP fetch if so
        var folder = _folderRepo.GetByPath(accountId, folderPath);
        if (folder is null) return;

        var existing = _messageRepo.GetByUid(accountId, folder.Id, uid);
        if (existing is not null && existing.BodyFetched)
        {
            _logger.LogDebug("Body already cached for message UID {Uid} in {AccountId}/{FolderPath}, skipping IMAP fetch",
                uid, accountId, folderPath);
            return;
        }

        await _connMgr.ExecuteAsync(async client =>
        {
            var imapFolder = await client.GetFolderAsync(folderPath, ct).ConfigureAwait(false);
            await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

            try
            {
                var uidObj = new UniqueId((uint)uid);
                var message = await imapFolder.GetMessageAsync(uidObj, ct).ConfigureAwait(false);

                if (message is not null)
                {
                    var bodyText = message.TextBody;
                    var bodyHtml = message.HtmlBody;

                    // Extract raw headers from the MIME message
                    string? rawHeaders = null;
                    try
                    {
                        using var headerStream = new System.IO.MemoryStream();
                        message.Headers.WriteTo(headerStream);
                        rawHeaders = System.Text.Encoding.UTF8.GetString(headerStream.ToArray());
                    }
                    catch { /* non-fatal — headers are optional */ }

                    var dbMessage = _messageRepo.GetByUid(accountId, folder.Id, uid);
                    if (dbMessage is not null)
                    {
                        _messageRepo.UpdateBody(dbMessage.Id, bodyText, bodyHtml, rawHeaders);
                    }
                }
            }
            finally
            {
                try { await imapFolder.CloseAsync(false, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is MailKit.ServiceNotConnectedException
                    or MailKit.ServiceNotAuthenticatedException
                    or IOException or OperationCanceledException) { /* connection already dead */ }
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> FetchMessageBodiesBatchAsync(string accountId, string folderPath,
        IReadOnlyList<long> uids, CancellationToken ct = default)
    {
        var folder = _folderRepo.GetByPath(accountId, folderPath);
        if (folder is null) return 0;

        // Filter to UIDs that haven't been fetched yet
        var toFetch = new List<long>();
        foreach (var uid in uids)
        {
            var existing = _messageRepo.GetByUid(accountId, folder.Id, uid);
            if (existing is not null && !existing.BodyFetched)
                toFetch.Add(uid);
        }

        if (toFetch.Count == 0) return 0;

        var fetched = 0;

        await _connMgr.ExecuteAsync(async client =>
        {
            var imapFolder = await client.GetFolderAsync(folderPath, ct).ConfigureAwait(false);
            await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

            try
            {
                foreach (var uid in toFetch)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var uidObj = new UniqueId((uint)uid);
                        var message = await imapFolder.GetMessageAsync(uidObj, ct).ConfigureAwait(false);
                        if (message is not null)
                        {
                            var dbMessage = _messageRepo.GetByUid(accountId, folder.Id, uid);
                            if (dbMessage is not null)
                            {
                                _messageRepo.UpdateBody(dbMessage.Id, message.TextBody, message.HtmlBody);
                                fetched++;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to fetch body for UID {Uid} in {AccountId}/{FolderPath}",
                            uid, accountId, folderPath);
                    }
                }
            }
            finally
            {
                try { await imapFolder.CloseAsync(false, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is MailKit.ServiceNotConnectedException
                    or MailKit.ServiceNotAuthenticatedException
                    or IOException or OperationCanceledException) { }
            }
        }, ct).ConfigureAwait(false);

        return fetched;
    }

    public async Task<long> DownloadAttachmentAsync(string accountId, string folderPath, long uid,
        string? targetFilename, string? contentId, string savePath, CancellationToken ct = default)
    {
        long bytesWritten = 0;

        await _connMgr.ExecuteAsync(async client =>
        {
            var imapFolder = await client.GetFolderAsync(folderPath, ct).ConfigureAwait(false);
            await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

            try
            {
                var uidObj = new UniqueId((uint)uid);
                var message = await imapFolder.GetMessageAsync(uidObj, ct).ConfigureAwait(false);

                if (message is null)
                    throw new InvalidOperationException($"Message UID {uid} not found in folder '{folderPath}'.");

                // Find the matching attachment
                MimePart? match = null;
                foreach (var attachment in message.Attachments.OfType<MimePart>())
                {
                    // Match by content_id first (most specific)
                    if (!string.IsNullOrEmpty(contentId) && attachment.ContentId == contentId)
                    {
                        match = attachment;
                        break;
                    }
                    // Match by filename
                    if (!string.IsNullOrEmpty(targetFilename) && string.Equals(attachment.FileName, targetFilename, StringComparison.OrdinalIgnoreCase))
                    {
                        match = attachment;
                        break;
                    }
                }

                // If no match via attachments, also check body parts (inline)
                if (match is null)
                {
                    foreach (var part in message.BodyParts.OfType<MimePart>())
                    {
                        if (!string.IsNullOrEmpty(contentId) && part.ContentId == contentId)
                        {
                            match = part;
                            break;
                        }
                        if (!string.IsNullOrEmpty(targetFilename) && string.Equals(part.FileName, targetFilename, StringComparison.OrdinalIgnoreCase))
                        {
                            match = part;
                            break;
                        }
                    }
                }

                if (match is null)
                    throw new InvalidOperationException(
                        $"Attachment not found in message UID {uid} (filename='{targetFilename}', contentId='{contentId}').");

                // Ensure directory exists
                var dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (match.Content is null)
                    throw new InvalidOperationException("Attachment has no content data.");

                await using var stream = File.Create(savePath);
                await match.Content.DecodeToAsync(stream, ct).ConfigureAwait(false);
                bytesWritten = stream.Length;
            }
            finally
            {
                try { await imapFolder.CloseAsync(false, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is MailKit.ServiceNotConnectedException
                    or MailKit.ServiceNotAuthenticatedException
                    or IOException or OperationCanceledException) { /* connection already dead */ }
            }
        }, ct).ConfigureAwait(false);

        return bytesWritten;
    }

    public Task StartRealtimeListenerAsync(string accountId, string folderPath,
        Func<Task> onChangesDetected, CancellationToken ct = default)
    {
        // The real-time IDLE listener is managed by SyncManager directly.
        // This method exists for future refactoring when SyncManager delegates to backends.
        _logger.LogDebug(
            "IMAP realtime listener requested for {AccountId}/{Folder} — " +
            "currently managed by SyncManager directly", accountId, folderPath);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _connMgr.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ImapSyncBackend disconnect failed (non-fatal)");
        }
        _connMgr.Dispose();
    }
}
