using System.Text.Json;
using MailKit;
using MailKit.Search;
using MimeKit;
using UltimateImapMcp.ImapClient.Repositories;
using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace UltimateImapMcp.ImapClient;

/// <summary>
/// Synchronises IMAP folder metadata and messages into the local SQLite cache.
/// Phase 1: full UID-range sync. Incremental delta sync deferred to Phase 3.
/// </summary>
public sealed class ImapSyncService
{
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;
    private readonly AttachmentRepository _attachmentRepo;

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
        else
        {
            // Fallback: start from INBOX and recurse subfolders.
            allFolders = await GetAllSubfoldersAsync(client.Inbox, ct)
                .ConfigureAwait(false);
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
    /// Bidirectional sync: derives current sync boundaries from the actual cached
    /// messages in the DB (not stored columns), then fetches new messages forward
    /// and backfills older messages in batches of 100.
    /// </summary>
    public async Task SyncFolderMessagesAsync(
        ImapClientLib client,
        string accountId,
        FolderRecord folder,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(folder);

        var imapFolder = await client.GetFolderAsync(folder.Path, ct).ConfigureAwait(false);
        if (imapFolder.Attributes.HasFlag(FolderAttributes.NoSelect)
            || imapFolder.Attributes.HasFlag(FolderAttributes.NonExistent))
            return;

        await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

        try
        {
            // Derive sync boundaries from actual cached data — single source of truth
            var maxUid = _messageRepo.GetMaxUid(accountId, folder.Id);
            var minUid = _messageRepo.GetMinUid(accountId, folder.Id);

            // Phase 1: Forward sync — fetch new messages (UID > max cached)
            {
                var startUid = new UniqueId((uint)(maxUid + 1));
                var range = new UniqueIdRange(startUid, UniqueId.MaxValue);
                var newUids = await imapFolder.SearchAsync(SearchQuery.Uids(range), ct).ConfigureAwait(false);

                if (newUids.Count > 0)
                {
                    await FetchAndStoreMessagesAsync(imapFolder, accountId, folder.Id, newUids, ct).ConfigureAwait(false);
                    maxUid = Math.Max(maxUid, newUids.Max(u => (long)u.Id));
                    if (minUid == 0)
                        minUid = newUids.Min(u => (long)u.Id);
                }
            }

            // Phase 2: Backfill — fetch older messages (UID < min cached)
            if (minUid > 1)
            {
                var endUid = new UniqueId((uint)(minUid - 1));
                var backfillRange = new UniqueIdRange(UniqueId.MinValue, endUid);
                var oldUids = await imapFolder.SearchAsync(SearchQuery.Uids(backfillRange), ct).ConfigureAwait(false);

                if (oldUids.Count > 0)
                {
                    var batch = oldUids.OrderByDescending(u => u.Id).Take(100).ToList();
                    await FetchAndStoreMessagesAsync(imapFolder, accountId, folder.Id, batch, ct).ConfigureAwait(false);
                    minUid = batch.Min(u => (long)u.Id);
                }
                else
                {
                    minUid = 1; // Backfill complete
                }
            }

            // Update folder state (for dashboard display and backfill tracking)
            _folderRepo.UpdateSyncState(
                folder.Id, maxUid, minUid,
                imapFolder.Count, imapFolder.Unread);
        }
        finally
        {
            await imapFolder.CloseAsync(false, ct).ConfigureAwait(false);
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
            await imapFolder.CloseAsync(false, ct).ConfigureAwait(false);
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
        var fetchRequest = new FetchRequest(
            MessageSummaryItems.UniqueId |
            MessageSummaryItems.Envelope |
            MessageSummaryItems.Flags |
            MessageSummaryItems.Size |
            MessageSummaryItems.BodyStructure,
            new[] { "References" });

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
                or IOException or OperationCanceledException or InvalidOperationException)
            {
                // Non-critical -- fall back to subject.
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
