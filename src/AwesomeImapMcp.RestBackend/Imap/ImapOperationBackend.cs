using MailKit;
using Microsoft.Extensions.Logging;
using MimeKit;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Email;
using AwesomeImapMcp.Core.OAuth;
using AwesomeImapMcp.ImapClient;

namespace AwesomeImapMcp.RestBackend.Imap;

/// <summary>
/// Wraps existing IMAP/SMTP operations behind <see cref="IEmailOperationBackend"/>.
/// Delegates send to <see cref="SmtpConnectionManager"/> and
/// move/delete/flag to <see cref="ImapConnectionManager"/>.
/// </summary>
internal sealed class ImapOperationBackend : IEmailOperationBackend
{
    private readonly ImapConnectionManager _connMgr;
    private readonly AccountConfig _accountConfig;
    private readonly IOAuthAccessTokenProvider _oauthProvider;
    private readonly string _accountId;
    private readonly ILogger<ImapOperationBackend> _logger;

    public ImapOperationBackend(
        ImapConnectionManager connMgr,
        AccountConfig accountConfig,
        IOAuthAccessTokenProvider oauthProvider,
        string accountId,
        ILogger<ImapOperationBackend> logger)
    {
        _connMgr = connMgr;
        _accountConfig = accountConfig;
        _oauthProvider = oauthProvider;
        _accountId = accountId;
        _logger = logger;
    }

    public string BackendType => "imap";

    public async Task SendAsync(string accountId, EmailMessage message, CancellationToken ct = default)
    {
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(MailboxAddress.Parse(_accountConfig.Username));
        mimeMessage.To.Add(MailboxAddress.Parse(message.To));

        if (!string.IsNullOrEmpty(message.Cc))
            mimeMessage.Cc.Add(MailboxAddress.Parse(message.Cc));

        if (!string.IsNullOrEmpty(message.Bcc))
            mimeMessage.Bcc.Add(MailboxAddress.Parse(message.Bcc));

        mimeMessage.Subject = message.Subject;
        mimeMessage.Body = new TextPart("plain") { Text = message.Body };

        if (!string.IsNullOrEmpty(message.InReplyTo))
            mimeMessage.InReplyTo = message.InReplyTo;

        using var smtp = new SmtpConnectionManager(
            _accountConfig, _logger, _oauthProvider, _accountId);
        await smtp.SendAsync(mimeMessage, ct).ConfigureAwait(false);
    }

    public async Task MoveAsync(string accountId, IReadOnlyList<long> uids, string fromFolder,
        string toFolder, CancellationToken ct = default)
    {
        var uidList = uids.Select(u => new UniqueId((uint)u)).ToList();

        await _connMgr.ExecuteAsync(async client =>
        {
            var srcFolder = await client.GetFolderAsync(fromFolder, ct).ConfigureAwait(false);
            var dstFolder = await client.GetFolderAsync(toFolder, ct).ConfigureAwait(false);
            await srcFolder.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            await srcFolder.MoveToAsync(uidList, dstFolder, ct).ConfigureAwait(false);
            await srcFolder.CloseAsync(false, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string accountId, IReadOnlyList<long> uids, string folder,
        CancellationToken ct = default)
    {
        var uidList = uids.Select(u => new UniqueId((uint)u)).ToList();

        await _connMgr.ExecuteAsync(async client =>
        {
            var imapFolder = await client.GetFolderAsync(folder, ct).ConfigureAwait(false);
            await imapFolder.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            await imapFolder.AddFlagsAsync(uidList, MessageFlags.Deleted, true, ct).ConfigureAwait(false);
            await imapFolder.ExpungeAsync(ct).ConfigureAwait(false);
            await imapFolder.CloseAsync(false, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task SetFlagsAsync(string accountId, IReadOnlyList<long> uids, string folder,
        MessageAction action, CancellationToken ct = default)
    {
        var uidList = uids.Select(u => new UniqueId((uint)u)).ToList();
        var (flags, add) = action switch
        {
            MessageAction.MarkRead => (MessageFlags.Seen, true),
            MessageAction.MarkUnread => (MessageFlags.Seen, false),
            MessageAction.Flag => (MessageFlags.Flagged, true),
            MessageAction.Unflag => (MessageFlags.Flagged, false),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown message action")
        };

        await _connMgr.ExecuteAsync(async client =>
        {
            var imapFolder = await client.GetFolderAsync(folder, ct).ConfigureAwait(false);
            await imapFolder.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            if (add)
                await imapFolder.AddFlagsAsync(uidList, flags, true, ct).ConfigureAwait(false);
            else
                await imapFolder.RemoveFlagsAsync(uidList, flags, true, ct).ConfigureAwait(false);
            await imapFolder.CloseAsync(false, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _connMgr.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ImapOperationBackend disconnect failed (non-fatal)");
        }
        _connMgr.Dispose();
    }
}
