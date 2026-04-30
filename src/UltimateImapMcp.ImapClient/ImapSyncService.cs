using System.Text.Json;
using MailKit;
using MailKit.Search;
using MimeKit;
using UltimateImapMcp.ImapClient.Repositories;
using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace UltimateImapMcp.ImapClient;

/// <summary>
/// Synchronises IMAP folder metadata and messages into the local SQLite cache.
/// Uses contiguous UID-range sync: compares all server UIDs against the local
/// cache and fetches missing messages newest-first in batches.
/// </summary>
public sealed class ImapSyncService
{
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;
    private readonly AttachmentRepository _attachmentRepo;
    // Tracks accounts where BodyStructure fetch causes ImapProtocolException
    private readonly HashSet<string> _skipBodyStructureAccounts = new();

    public ImapSyncService(
        FolderRepository folderRepo,
        MessageRepository messageRepo,
        AttachmentRepository attachmentRepo)
    {
        ArgumentNullException.ThrowIfNull(folderRepo);
        ArgumentNullException.ThrowIfNull(messageRepo);
        ArgumentNullException.ThrowIfNull(attachmentRepo);

        _folderRepo = folderRepo;
        _messageRepo = messageRepo;
        _attachmentRepo = attachmentRepo;
    }

    // ------------------------------------------------------------------
    // Folder sync
    // ------------------------------------------------------------------

    /// <summary>
    /// Discovers all folders in the personal namespace and upserts them
    /// into the FolderRepository. INBOX is always ensured.
    /// </summary>
    public async Task SyncFoldersAsync(
        ImapClientLib client,
        string accountId,
        FolderMapper mapper,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(mapper);

        // Retrieve all folders in the personal namespace.
        var personalNs = client.PersonalNamespaces.FirstOrDefault();
        IEnumerable<IMailFolder> allFolders;

        if (personalNs is not null)
        {
            // GetFolders with subscribedOnly=false returns the full tree.
            allFolders = await client.GetFoldersAsync(personalNs, false, ct)
                .ConfigureAwait(false);
        }
        else if (client.Inbox is { } inbox)
        {
            // Fallback: start from INBOX and recurse subfolders.
            allFolders = await GetAllSubfoldersAsync(inbox, ct)
                .ConfigureAwait(false);
        }
        else
        {
            allFolders = [];
        }

        // Ensure INBOX is always present.
        var delimiter = personalNs?.DirectorySeparator.ToString() ?? "/";
        _folderRepo.Insert(accountId, "INBOX", "Inbox", "inbox", delimiter);

        foreach (var imapFolder in allFolders)
        {
            var path = imapFolder.FullName;
            if (string.IsNullOrWhiteSpace(path)) continue;

            // Skip non-selectable folders (e.g., [Gmail] namespace container)
            if (imapFolder.Attributes.HasFlag(FolderAttributes.NonExistent)
                || imapFolder.Attributes.HasFlag(FolderAttributes.NoSelect))
                continue;

            var role = mapper.DetectRole(path);
            var displayName = mapper.GetDisplayName(path, role);
            var folderDelimiter = imapFolder.DirectorySeparator.ToString();

            _folderRepo.Insert(
                accountId,
                path,
                displayName,
                role?.ToString().ToLowerInvariant(),
                folderDelimiter);
        }
    }

    // ------------------------------------------------------------------
    // Message sync
    // ------------------------------------------------------------------

    /// <summary>
    /// Contiguous sync: gets ALL UIDs from server, compares with cached UIDs,
    /// fetches missing ones newest-first in batches. Guarantees no gaps.
    /// Optionally soft-deletes messages that no longer exist on the server.
    /// Returns the number of messages still missing after this batch (0 = fully caught up).
    /// </summary>
    public async Task<int> SyncFolderMessagesAsync(
        ImapClientLib client,
        string accountId,
        FolderRecord folder,
        bool cleanupServerDeleted = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(folder);

        var imapFolder = await client.GetFolderAsync(folder.Path, ct).ConfigureAwait(false);
        if (imapFolder.Attributes.HasFlag(FolderAttributes.NoSelect)
            || imapFolder.Attributes.HasFlag(FolderAttributes.NonExistent))
            return 0;

        await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

        try
        {
            // Get ALL UIDs that exist on the server
            var allServerUids = await imapFolder.SearchAsync(SearchQuery.All, ct).ConfigureAwait(false);
            var serverUidSet = new HashSet<long>(allServerUids.Select(u => (long)u.Id));

            if (allServerUids.Count == 0)
            {
                _folderRepo.UpdateSyncState(folder.Id, 0, 0, 0, 0);
                return 0;
            }

            // Get UIDs we already have cached
            var cachedUids = _messageRepo.GetCachedUids(accountId, folder.Id);

            // Find missing UIDs (on server but not in cache) — fetch newest first
            var missingUids = allServerUids
                .Where(u => !cachedUids.Contains((long)u.Id))
                .OrderByDescending(u => u.Id)
                .ToList();

            if (missingUids.Count > 0)
            {
                const int batchSize = 200;
                var batch = missingUids.Take(batchSize).ToList();
                try
                {
                    await FetchAndStoreMessagesAsync(imapFolder, accountId, folder.Id, batch, ct)
                        .ConfigureAwait(false);
                }
                catch (MailKit.Net.Imap.ImapProtocolException) when (!_skipBodyStructureAccounts.Contains(accountId))
                {
                    // BodyStructure parsing failed — mark account and rethrow so connection
                    // gets rebuilt. Next sync cycle will skip BodyStructure for this account.
                    _skipBodyStructureAccounts.Add(accountId);
                    throw;
                }
            }

            // Soft-delete messages that were removed from the server
            if (cleanupServerDeleted && cachedUids.Count > 0)
            {
                var deletedOnServer = cachedUids
                    .Where(uid => !serverUidSet.Contains(uid))
                    .ToList();

                if (deletedOnServer.Count > 0)
                {
                    _messageRepo.SoftDeleteByUids(accountId, folder.Id, deletedOnServer);
                }
            }

            // Update folder state from actual data
            var maxUid = _messageRepo.GetMaxUid(accountId, folder.Id);
            var minUid = _messageRepo.GetMinUid(accountId, folder.Id);

            _folderRepo.UpdateSyncState(
                folder.Id, maxUid, minUid,
                imapFolder.Count, imapFolder.Unread);

            // Return how many messages are still missing
            var remaining = missingUids.Count > 200 ? missingUids.Count - 200 : 0;
            return remaining;
        }
        finally
        {
            try { await imapFolder.CloseAsync(false, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is MailKit.ServiceNotConnectedException
                or MailKit.ServiceNotAuthenticatedException
                or IOException or OperationCanceledException) { /* connection already dead */ }
        }
    }

    // ------------------------------------------------------------------
    // Server-side IMAP search
    // ------------------------------------------------------------------

    /// <summary>
    /// Searches directly on the IMAP server and returns matching messages.
    /// Messages not in the local cache are fetched and cached automatically.
    /// </summary>
    public async Task<List<MessageRecord>> ServerSearchAsync(
        ImapClientLib client, string accountId, FolderRecord folder,
        string? query, string? from, string? to, string? subject,
        long? fromEpoch, long? toEpoch, int maxResults,
        CancellationToken ct = default)
    {
        var imapFolder = await client.GetFolderAsync(folder.Path, ct).ConfigureAwait(false);
        if (imapFolder.Attributes.HasFlag(FolderAttributes.NoSelect))
            return [];

        await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

        try
        {
            // Build IMAP search query
            SearchQuery searchQuery = SearchQuery.All;
            if (!string.IsNullOrEmpty(query))
                searchQuery = SearchQuery.And(searchQuery, SearchQuery.BodyContains(query));
            if (!string.IsNullOrEmpty(from))
                searchQuery = SearchQuery.And(searchQuery, SearchQuery.FromContains(from));
            if (!string.IsNullOrEmpty(to))
                searchQuery = SearchQuery.And(searchQuery, SearchQuery.ToContains(to));
            if (!string.IsNullOrEmpty(subject))
                searchQuery = SearchQuery.And(searchQuery, SearchQuery.SubjectContains(subject));
            if (fromEpoch is not null)
                searchQuery = SearchQuery.And(searchQuery,
                    SearchQuery.SentSince(DateTimeOffset.FromUnixTimeSeconds(fromEpoch.Value).DateTime));
            if (toEpoch is not null)
                searchQuery = SearchQuery.And(searchQuery,
                    SearchQuery.SentBefore(DateTimeOffset.FromUnixTimeSeconds(toEpoch.Value).DateTime));

            var uids = await imapFolder.SearchAsync(searchQuery, ct).ConfigureAwait(false);

            // Limit and reverse for newest-first
            var limitedUids = uids.Reverse().Take(maxResults).ToList();
            if (limitedUids.Count == 0) return [];

            // Check which are already cached
            var results = new List<MessageRecord>();
            var missingUids = new List<UniqueId>();

            foreach (var uid in limitedUids)
            {
                var cached = _messageRepo.GetByUid(accountId, folder.Id, uid.Id);
                if (cached is not null)
                    results.Add(cached);
                else
                    missingUids.Add(uid);
            }

            // Fetch and cache missing messages using the shared helper
            if (missingUids.Count > 0)
            {
                var insertedIds = await FetchAndStoreMessagesAsync(imapFolder, accountId, folder.Id, missingUids, ct, synced: false)
                    .ConfigureAwait(false);

                foreach (var dbMsgId in insertedIds)
                {
                    if (dbMsgId > 0)
                    {
                        var inserted = _messageRepo.GetById(dbMsgId);
                        if (inserted is not null) results.Add(inserted);
                    }
                }
            }

            return results;
        }
        finally
        {
            try { await imapFolder.CloseAsync(false, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is MailKit.ServiceNotConnectedException
                or MailKit.ServiceNotAuthenticatedException
                or IOException or OperationCanceledException) { /* connection already dead */ }
        }
    }

    // ------------------------------------------------------------------
    // Shared fetch helper
    // ------------------------------------------------------------------

    /// <summary>
    /// Fetches message summaries for the given UIDs and stores them via InsertOrLink.
    /// Returns the list of database message IDs for inserted/linked messages.
    /// </summary>
    private async Task<List<int>> FetchAndStoreMessagesAsync(
        IMailFolder imapFolder, string accountId, int folderId,
        IList<UniqueId> uids, CancellationToken ct, bool synced = true)
    {
        // Some servers (e.g., Zoho) return malformed BODYSTRUCTURE that kills the connection.
        // Once detected for an account, skip BodyStructure on all subsequent fetches.
        var useBodyStructure = !_skipBodyStructureAccounts.Contains(accountId);

        var items = MessageSummaryItems.UniqueId |
            MessageSummaryItems.Envelope |
            MessageSummaryItems.Flags |
            MessageSummaryItems.Size;
        if (useBodyStructure) items |= MessageSummaryItems.BodyStructure;

        var fetchRequest = new FetchRequest(items, new[] { "References" });

        IList<IMessageSummary> summaries = await imapFolder
            .FetchAsync(uids, fetchRequest, ct)
            .ConfigureAwait(false);

        var insertedIds = new List<int>();

        foreach (var summary in summaries)
        {
            ct.ThrowIfCancellationRequested();

            var uid = (long)summary.UniqueId.Id;

            // Envelope data.
            var envelope = summary.Envelope;
            var messageId = envelope?.MessageId;
            var inReplyTo = envelope?.InReplyTo;
            var subject = envelope?.Subject;
            var date = envelope?.Date?.ToString("o") ?? DateTimeOffset.UtcNow.ToString("o");
            var dateEpoch = envelope?.Date?.ToUnixTimeSeconds();

            var fromAddress = envelope?.From.FirstOrDefault()?.ToString();
            var fromEmail = fromAddress is not null
                ? MessageParser.ExtractEmailFromAddress(fromAddress)
                : null;

            var toJson = SerializeAddressList(envelope?.To);
            var ccJson = SerializeAddressList(envelope?.Cc);

            // References header (already on summary.References or fallback to Headers).
            var referencesHdr = summary.References?.Count > 0
                ? string.Join(" ", summary.References)
                : summary.Headers?["References"];

            // Thread ID.
            var threadId = ThreadBuilder.ComputeThreadId(messageId, referencesHdr);

            // Flags.
            var flags = BuildFlagsString(summary.Flags, summary.Keywords);

            // Attachments detection.
            var attachments = summary.Attachments?.ToList() ?? [];
            var hasAttachments = attachments.Count > 0;

            // Snippet: try fetching the plain-text body part; fall back to subject.
            string? snippet = null;
            try
            {
                var textBodyPart = summary.TextBody;
                if (textBodyPart is not null)
                {
                    var mimeEntity = await imapFolder
                        .GetBodyPartAsync(summary.UniqueId, textBodyPart, ct)
                        .ConfigureAwait(false);

                    if (mimeEntity is TextPart textPart)
                        snippet = MessageParser.GenerateSnippet(textPart.Text);
                }
            }
            catch (Exception ex) when (ex is MailKit.Net.Imap.ImapProtocolException
                or IOException or OperationCanceledException or InvalidOperationException
                or FormatException)
            {
                // Non-critical -- fall back to subject. FormatException occurs when
                // MimeKit encounters malformed MIME headers in broken emails.
                snippet = subject is not null
                    ? MessageParser.GenerateSnippet(subject)
                    : null;
            }

            // Insert or link message record (deduplicates across folders).
            var dbMsgId = _messageRepo.InsertOrLink(
                accountId,
                folderId,
                uid,
                messageId,
                inReplyTo,
                referencesHdr,
                threadId,
                subject,
                fromAddress,
                fromEmail,
                toJson,
                ccJson,
                bccAddresses: null,
                date,
                dateEpoch,
                flags,
                sizeBytes: summary.Size.HasValue ? (int?)summary.Size.Value : null,
                hasAttachments,
                snippet,
                synced: synced);

            insertedIds.Add(dbMsgId);

            // Attachment metadata stubs.
            if (hasAttachments && dbMsgId > 0)
            {
                var dbMsg = _messageRepo.GetById(dbMsgId);
                if (dbMsg is not null)
                {
                    foreach (var att in attachments)
                    {
                        _attachmentRepo.Insert(
                            dbMsg.Id,
                            att.FileName,
                            att.ContentType?.MimeType,
                            sizeBytes: att.Octets > 0 ? (int?)att.Octets : null,
                            att.ContentId,
                            isInline: att.ContentDisposition?.Disposition
                                ?.Equals("inline", StringComparison.OrdinalIgnoreCase) ?? false);
                    }
                }
            }
        }

        return insertedIds;
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private static async Task<IEnumerable<IMailFolder>> GetAllSubfoldersAsync(
        IMailFolder root, CancellationToken ct)
    {
        var result = new List<IMailFolder>();
        var subs = await root.GetSubfoldersAsync(false, ct).ConfigureAwait(false);
        foreach (var sub in subs)
        {
            result.Add(sub);
            result.AddRange(await GetAllSubfoldersAsync(sub, ct).ConfigureAwait(false));
        }
        return result;
    }

    private static string? SerializeAddressList(InternetAddressList? list)
    {
        if (list is null || list.Count == 0) return null;

        var addresses = list.Select(a => a.ToString()).ToArray();
        return JsonSerializer.Serialize(addresses);
    }

    private static string? BuildFlagsString(MessageFlags? flags, IReadOnlySet<string>? keywords)
    {
        if (flags is null && (keywords is null || keywords.Count == 0)) return null;

        var parts = new List<string>();

        if (flags.HasValue)
        {
            if ((flags.Value & MessageFlags.Seen) != 0) parts.Add("\\Seen");
            if ((flags.Value & MessageFlags.Answered) != 0) parts.Add("\\Answered");
            if ((flags.Value & MessageFlags.Flagged) != 0) parts.Add("\\Flagged");
            if ((flags.Value & MessageFlags.Deleted) != 0) parts.Add("\\Deleted");
            if ((flags.Value & MessageFlags.Draft) != 0) parts.Add("\\Draft");
        }

        if (keywords is not null)
            parts.AddRange(keywords);

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }
}
