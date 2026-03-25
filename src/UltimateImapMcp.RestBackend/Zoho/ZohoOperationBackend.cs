using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.RestBackend.Zoho;

/// <summary>
/// Implements <see cref="IEmailOperationBackend"/> for Zoho Mail using the REST API.
/// Handles send, move, delete, and flag operations.
/// </summary>
internal sealed class ZohoOperationBackend : IEmailOperationBackend
{
    private readonly ZohoApiClient _api;
    private readonly AccountRepository _accountRepo;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;
    private readonly ILogger<ZohoOperationBackend> _logger;

    /// <summary>Cache of Zoho-internal account ID per our account ID.</summary>
    private readonly Dictionary<string, string> _zohoAccountIds = new();

    public ZohoOperationBackend(
        ZohoApiClient api,
        AccountRepository accountRepo,
        FolderRepository folderRepo,
        MessageRepository messageRepo,
        ILogger<ZohoOperationBackend> logger)
    {
        _api = api;
        _accountRepo = accountRepo;
        _folderRepo = folderRepo;
        _messageRepo = messageRepo;
        _logger = logger;
    }

    public string BackendType => "zoho_rest";

    // ------------------------------------------------------------------
    // Send
    // ------------------------------------------------------------------

    public async Task SendAsync(string accountId, EmailMessage message, CancellationToken ct = default)
    {
        var zohoAcctId = await ResolveZohoAccountIdAsync(accountId, ct).ConfigureAwait(false);
        var account = _accountRepo.ResolveAccount(accountId)
            ?? throw new InvalidOperationException($"Account '{accountId}' not found.");

        var request = new ZohoSendRequest
        {
            FromAddress = account.Username,
            ToAddress = message.To,
            Subject = message.Subject,
            Content = message.Body,
            CcAddress = message.Cc,
            BccAddress = message.Bcc,
            InReplyTo = message.InReplyTo,
            MailFormat = "plaintext"
        };

        await _api.SendMessageAsync(accountId, zohoAcctId, request, ct).ConfigureAwait(false);
        _logger.LogInformation("Zoho: sent email to {To} from account {AccountId}", message.To, accountId);
    }

    // ------------------------------------------------------------------
    // Move
    // ------------------------------------------------------------------

    public async Task MoveAsync(string accountId, IReadOnlyList<long> uids, string fromFolder,
        string toFolder, CancellationToken ct = default)
    {
        var zohoAcctId = await ResolveZohoAccountIdAsync(accountId, ct).ConfigureAwait(false);

        var srcDbFolder = _folderRepo.GetByPath(accountId, fromFolder)
            ?? throw new InvalidOperationException($"Source folder '{fromFolder}' not found.");
        var dstDbFolder = _folderRepo.GetByPath(accountId, toFolder)
            ?? throw new InvalidOperationException($"Destination folder '{toFolder}' not found.");

        var srcFolderId = srcDbFolder.Delimiter;
        var dstFolderId = dstDbFolder.Delimiter;

        foreach (var uid in uids)
        {
            ct.ThrowIfCancellationRequested();

            var dbMsg = _messageRepo.GetByUid(accountId, srcDbFolder.Id, uid);
            if (dbMsg?.MessageId is null)
            {
                _logger.LogWarning("Zoho: message UID {Uid} not found in DB for move, skipping", uid);
                continue;
            }

            await _api.MoveMessageAsync(
                accountId, zohoAcctId, srcFolderId, dbMsg.MessageId, dstFolderId, ct)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("Zoho: moved {Count} messages from {From} to {To} for account {AccountId}",
            uids.Count, fromFolder, toFolder, accountId);
    }

    // ------------------------------------------------------------------
    // Delete
    // ------------------------------------------------------------------

    public async Task DeleteAsync(string accountId, IReadOnlyList<long> uids, string folder,
        CancellationToken ct = default)
    {
        var zohoAcctId = await ResolveZohoAccountIdAsync(accountId, ct).ConfigureAwait(false);

        var dbFolder = _folderRepo.GetByPath(accountId, folder)
            ?? throw new InvalidOperationException($"Folder '{folder}' not found.");

        foreach (var uid in uids)
        {
            ct.ThrowIfCancellationRequested();

            var dbMsg = _messageRepo.GetByUid(accountId, dbFolder.Id, uid);
            if (dbMsg?.MessageId is null)
            {
                _logger.LogWarning("Zoho: message UID {Uid} not found in DB for delete, skipping", uid);
                continue;
            }

            await _api.DeleteMessageAsync(accountId, zohoAcctId, dbMsg.MessageId, ct)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("Zoho: deleted {Count} messages from {Folder} for account {AccountId}",
            uids.Count, folder, accountId);
    }

    // ------------------------------------------------------------------
    // Flags
    // ------------------------------------------------------------------

    public async Task SetFlagsAsync(string accountId, IReadOnlyList<long> uids, string folder,
        MessageAction action, CancellationToken ct = default)
    {
        var zohoAcctId = await ResolveZohoAccountIdAsync(accountId, ct).ConfigureAwait(false);

        var dbFolder = _folderRepo.GetByPath(accountId, folder)
            ?? throw new InvalidOperationException($"Folder '{folder}' not found.");

        var folderId = dbFolder.Delimiter;

        var request = action switch
        {
            MessageAction.MarkRead => new ZohoFlagUpdateRequest { Mode = "markAsRead" },
            MessageAction.MarkUnread => new ZohoFlagUpdateRequest { Mode = "markAsUnread" },
            MessageAction.Flag => new ZohoFlagUpdateRequest { Mode = "flagMail", FlagId = "1" },
            MessageAction.Unflag => new ZohoFlagUpdateRequest { Mode = "unflagMail", FlagId = "0" },
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown message action")
        };

        foreach (var uid in uids)
        {
            ct.ThrowIfCancellationRequested();

            var dbMsg = _messageRepo.GetByUid(accountId, dbFolder.Id, uid);
            if (dbMsg?.MessageId is null)
            {
                _logger.LogWarning("Zoho: message UID {Uid} not found in DB for flag update, skipping", uid);
                continue;
            }

            await _api.UpdateMessageFlagsAsync(
                accountId, zohoAcctId, folderId, dbMsg.MessageId, request, ct)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("Zoho: {Action} on {Count} messages in {Folder} for account {AccountId}",
            action, uids.Count, folder, accountId);
    }

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<string> ResolveZohoAccountIdAsync(string accountId, CancellationToken ct)
    {
        if (_zohoAccountIds.TryGetValue(accountId, out var cached))
            return cached;

        var accounts = await _api.GetAccountsAsync(accountId, ct).ConfigureAwait(false);
        var zohoAccount = accounts.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No Zoho mail accounts found for account '{accountId}'.");

        _zohoAccountIds[accountId] = zohoAccount.AccountId;
        return zohoAccount.AccountId;
    }
}
