using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.RestBackend.Zoho;

/// <summary>
/// Implements <see cref="IEmailSyncBackend"/> for Zoho Mail using the REST API.
/// Syncs folders and messages into the shared local SQLite cache via the
/// same FolderRepository and MessageRepository used by the IMAP backend.
/// </summary>
internal sealed class ZohoSyncBackend : IEmailSyncBackend
{
    private readonly ZohoApiClient _api;
    private readonly AccountRepository _accountRepo;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;
    private readonly ILogger<ZohoSyncBackend> _logger;

    /// <summary>
    /// Cache of Zoho-internal account ID per our account ID.
    /// Zoho requires its own accountId (different from ours) for API calls.
    /// </summary>
    private readonly Dictionary<string, string> _zohoAccountIds = new();

    public ZohoSyncBackend(
        ZohoApiClient api,
        AccountRepository accountRepo,
        FolderRepository folderRepo,
        MessageRepository messageRepo,
        ILogger<ZohoSyncBackend> logger)
    {
        _api = api;
        _accountRepo = accountRepo;
        _folderRepo = folderRepo;
        _messageRepo = messageRepo;
        _logger = logger;
    }

    public string BackendType => "zoho_rest";
    public bool SupportsRealtimeSync => false;

    // ------------------------------------------------------------------
    // Folder sync
    // ------------------------------------------------------------------

    public async Task SyncFoldersAsync(string accountId, CancellationToken ct = default)
    {
        var zohoAcctId = await ResolveZohoAccountIdAsync(accountId, ct).ConfigureAwait(false);
        var folders = await _api.GetFoldersAsync(accountId, zohoAcctId, ct).ConfigureAwait(false);

        _logger.LogInformation("Zoho: syncing {Count} folders for account {AccountId}",
            folders.Count, accountId);

        foreach (var zf in folders)
        {
            var path = zf.FolderPath ?? zf.FolderName;
            var role = MapFolderRole(zf.FolderType);
            var displayName = zf.FolderName;

            // Use the Zoho folderId as part of the path so we can look it up later
            // Store the mapping: path -> zoho folder id in the delimiter field
            // (We reuse delimiter to store the Zoho folder ID since REST backends
            // don't use IMAP-style delimiters)
            _folderRepo.Insert(accountId, path, displayName, role, zf.FolderId);

            // Update message counts from Zoho
            var dbFolder = _folderRepo.GetByPath(accountId, path);
            if (dbFolder is not null)
            {
                _folderRepo.UpdateSyncState(
                    dbFolder.Id,
                    dbFolder.LastSyncedUid,
                    dbFolder.OldestSyncedUid,
                    zf.MessageCount,
                    zf.UnreadMessageCount);
            }
        }
    }

    // ------------------------------------------------------------------
    // Message sync
    // ------------------------------------------------------------------

    public async Task SyncFolderMessagesAsync(string accountId, string folderPath,
        CancellationToken ct = default)
    {
        var zohoAcctId = await ResolveZohoAccountIdAsync(accountId, ct).ConfigureAwait(false);
        var dbFolder = _folderRepo.GetByPath(accountId, folderPath);
        if (dbFolder is null)
        {
            _logger.LogWarning("Zoho: folder '{FolderPath}' not found in DB for account {AccountId}",
                folderPath, accountId);
            return;
        }

        // The Zoho folder ID is stored in the delimiter field for REST-backend folders
        var zohoFolderId = dbFolder.Delimiter;
        if (string.IsNullOrEmpty(zohoFolderId))
        {
            _logger.LogWarning("Zoho: no Zoho folder ID stored for '{FolderPath}' in account {AccountId}",
                folderPath, accountId);
            return;
        }

        // Page through messages from Zoho.
        // Start from where we left off (using sync_cursor which stores the start index).
        var startIndex = 0;
        if (!string.IsNullOrEmpty(dbFolder.SyncCursor) &&
            int.TryParse(dbFolder.SyncCursor, out var cursor))
        {
            startIndex = cursor;
        }

        const int pageSize = 100;
        var totalSynced = 0;
        var hasMore = true;
        long maxUid = dbFolder.LastSyncedUid;

        while (hasMore && !ct.IsCancellationRequested)
        {
            var messages = await _api.GetMessagesAsync(
                accountId, zohoAcctId, zohoFolderId,
                limit: pageSize, start: startIndex, ct: ct).ConfigureAwait(false);

            if (messages.Count == 0)
            {
                hasMore = false;
                break;
            }

            foreach (var msg in messages)
            {
                ct.ThrowIfCancellationRequested();

                // Use a hash of the Zoho messageId as our UID (long)
                var uid = ComputeUidFromMessageId(msg.MessageId);
                if (uid > maxUid) maxUid = uid;

                // Build flags string from Zoho status
                var flags = BuildFlags(msg.Status, msg.FlagId);

                // Determine date
                var epochMs = msg.SentDateInGmt > 0 ? msg.SentDateInGmt : msg.ReceivedTime;
                var dateEpoch = epochMs / 1000; // Zoho uses milliseconds
                var date = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToString("o");

                // Extract email from sender
                var fromEmail = msg.Sender is not null
                    ? MessageParser.ExtractEmailFromAddress(msg.Sender)
                    : null;

                // Thread ID from Zoho messageId
                var threadId = ThreadBuilder.ComputeThreadId(msg.MessageId, msg.InReplyTo);

                // Serialize to/cc
                var toJson = msg.ToAddress is not null
                    ? JsonSerializer.Serialize(new[] { msg.ToAddress })
                    : null;
                var ccJson = msg.CcAddress is not null
                    ? JsonSerializer.Serialize(new[] { msg.CcAddress })
                    : null;

                _messageRepo.InsertOrLink(
                    accountId,
                    dbFolder.Id,
                    uid,
                    msg.MessageId,
                    msg.InReplyTo,
                    referencesHdr: null,
                    threadId,
                    msg.Subject,
                    msg.Sender,
                    fromEmail,
                    toJson,
                    ccJson,
                    bccAddresses: null,
                    date,
                    dateEpoch,
                    flags,
                    sizeBytes: (int?)msg.Size,
                    msg.HasAttachment,
                    msg.Summary);

                totalSynced++;
            }

            startIndex += messages.Count;

            // If we got fewer than the page size, we've reached the end
            if (messages.Count < pageSize)
                hasMore = false;
        }

        // Update folder sync state
        _folderRepo.UpdateSyncState(dbFolder.Id, maxUid,
            dbFolder.OldestSyncedUid, dbFolder.MessageCount, dbFolder.UnreadCount);

        // Store cursor for next sync
        _folderRepo.UpdateSyncCursor(dbFolder.Id, startIndex.ToString());

        _logger.LogInformation(
            "Zoho: synced {Count} messages for {AccountId}/{FolderPath}",
            totalSynced, accountId, folderPath);
    }

    // ------------------------------------------------------------------
    // Body fetch
    // ------------------------------------------------------------------

    public async Task FetchMessageBodyAsync(string accountId, string folderPath, long uid,
        CancellationToken ct = default)
    {
        var zohoAcctId = await ResolveZohoAccountIdAsync(accountId, ct).ConfigureAwait(false);
        var dbFolder = _folderRepo.GetByPath(accountId, folderPath);
        if (dbFolder is null) return;

        var zohoFolderId = dbFolder.Delimiter;
        if (string.IsNullOrEmpty(zohoFolderId)) return;

        // Find the message in DB to get the Zoho messageId
        var dbMsg = _messageRepo.GetByUid(accountId, dbFolder.Id, uid);
        if (dbMsg is null || dbMsg.MessageId is null) return;

        var detail = await _api.GetMessageDetailAsync(
            accountId, zohoAcctId, zohoFolderId, dbMsg.MessageId, ct).ConfigureAwait(false);

        if (detail is null) return;

        _messageRepo.UpdateBody(dbMsg.Id, detail.Content, detail.HtmlContent);

        _logger.LogDebug("Zoho: fetched body for message {Uid} in {AccountId}/{FolderPath}",
            uid, accountId, folderPath);
    }

    // ------------------------------------------------------------------
    // Realtime (not supported)
    // ------------------------------------------------------------------

    public Task StartRealtimeListenerAsync(string accountId, string folderPath,
        Func<Task> onChangesDetected, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Zoho REST backend does not support real-time sync. Use polling instead.");
    }

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves the Zoho-internal account ID by calling the accounts API.
    /// The result is cached per our account ID.
    /// </summary>
    private async Task<string> ResolveZohoAccountIdAsync(string accountId, CancellationToken ct)
    {
        if (_zohoAccountIds.TryGetValue(accountId, out var cached))
            return cached;

        var accounts = await _api.GetAccountsAsync(accountId, ct).ConfigureAwait(false);
        var zohoAccount = accounts.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No Zoho mail accounts found for account '{accountId}'. " +
                "Ensure the OAuth token has the correct scopes.");

        _zohoAccountIds[accountId] = zohoAccount.AccountId;
        return zohoAccount.AccountId;
    }

    /// <summary>Maps Zoho folder type to our role string.</summary>
    private static string? MapFolderRole(string? folderType)
    {
        return folderType?.ToLowerInvariant() switch
        {
            "inbox" => "inbox",
            "sent" => "sent",
            "drafts" or "draft" => "drafts",
            "trash" => "trash",
            "spam" or "junk" => "spam",
            "archive" => "archive",
            _ => null
        };
    }

    /// <summary>Builds a flags string from Zoho status values.</summary>
    private static string? BuildFlags(string? status, string? flagId)
    {
        var parts = new List<string>();

        // Zoho status: "read" or "unread"
        if (status?.Equals("read", StringComparison.OrdinalIgnoreCase) == true)
            parts.Add("\\Seen");

        // Zoho flagid: non-null/non-zero means flagged
        if (!string.IsNullOrEmpty(flagId) && flagId != "0")
            parts.Add("\\Flagged");

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>
    /// Computes a deterministic long UID from a Zoho message ID string.
    /// Uses first 8 bytes of SHA256 interpreted as a positive long.
    /// </summary>
    private static long ComputeUidFromMessageId(string messageId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        var value = BitConverter.ToInt64(hash, 0);
        return Math.Abs(value);
    }
}
