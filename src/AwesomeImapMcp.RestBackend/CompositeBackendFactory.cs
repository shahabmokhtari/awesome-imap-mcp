using Microsoft.Extensions.Logging;
using AwesomeImapMcp.Core.Email;
using AwesomeImapMcp.Core.Encryption;
using AwesomeImapMcp.Core.OAuth;
using AwesomeImapMcp.Core.Providers;
using AwesomeImapMcp.ImapClient;
using AwesomeImapMcp.ImapClient.Repositories;
using AwesomeImapMcp.RestBackend.Imap;
namespace AwesomeImapMcp.RestBackend;

/// <summary>
/// Routes backend creation to the correct implementation based on the account's
/// <c>backend_type</c> column (or derived from provider name).
///
/// Currently supported backends:
/// - "imap" (default): wraps existing IMAP sync and SMTP operations
/// </summary>
public sealed class CompositeBackendFactory : IEmailBackendFactory
{
    private readonly AccountRepository _accountRepo;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;
    private readonly AttachmentRepository _attachmentRepo;
    private readonly SyncLogRepository _syncLogRepo;
    private readonly CredentialEncryptor _encryptor;
    private readonly ProviderProfileRegistry _providerRegistry;
    private readonly IOAuthAccessTokenProvider _oauthProvider;
    private readonly OAuthTokenRepository _tokenRepo;
    private readonly ImapSyncService _imapSyncService;
    private readonly ILoggerFactory _loggerFactory;

    public CompositeBackendFactory(
        AccountRepository accountRepo,
        FolderRepository folderRepo,
        MessageRepository messageRepo,
        AttachmentRepository attachmentRepo,
        SyncLogRepository syncLogRepo,
        CredentialEncryptor encryptor,
        ProviderProfileRegistry providerRegistry,
        IOAuthAccessTokenProvider oauthProvider,
        OAuthTokenRepository tokenRepo,
        ImapSyncService imapSyncService,
        ILoggerFactory loggerFactory)
    {
        _accountRepo = accountRepo;
        _folderRepo = folderRepo;
        _messageRepo = messageRepo;
        _attachmentRepo = attachmentRepo;
        _syncLogRepo = syncLogRepo;
        _encryptor = encryptor;
        _providerRegistry = providerRegistry;
        _oauthProvider = oauthProvider;
        _tokenRepo = tokenRepo;
        _imapSyncService = imapSyncService;
        _loggerFactory = loggerFactory;
    }

    public IEmailSyncBackend CreateSyncBackend(string accountId)
    {
        return CreateImapSyncBackend(accountId);
    }

    public IEmailOperationBackend CreateOperationBackend(string accountId)
    {
        return CreateImapOperationBackend(accountId);
    }

    public string GetBackendType(string accountId)
    {
        var record = _accountRepo.ResolveAccount(accountId);
        if (record is null)
            throw new InvalidOperationException($"Account '{accountId}' not found.");

        return "imap";
    }

    // ------------------------------------------------------------------
    // IMAP backend creation
    // ------------------------------------------------------------------

    private ImapSyncBackend CreateImapSyncBackend(string accountId)
    {
        var record = _accountRepo.ResolveAccount(accountId)
            ?? throw new InvalidOperationException($"Account '{accountId}' not found.");
        var accountConfig = AccountConfigMapper.ToAccountConfig(record, _encryptor);

        var connMgr = new ImapConnectionManager(
            accountConfig, _encryptor,
            _loggerFactory.CreateLogger<ImapConnectionManager>(),
            _oauthProvider, record.Id);

        return new ImapSyncBackend(
            connMgr,
            _imapSyncService,
            _folderRepo,
            _messageRepo,
            _providerRegistry,
            accountConfig,
            _loggerFactory.CreateLogger<ImapSyncBackend>());
    }

    private ImapOperationBackend CreateImapOperationBackend(string accountId)
    {
        var record = _accountRepo.ResolveAccount(accountId)
            ?? throw new InvalidOperationException($"Account '{accountId}' not found.");
        var accountConfig = AccountConfigMapper.ToAccountConfig(record, _encryptor);

        var connMgr = new ImapConnectionManager(
            accountConfig, _encryptor,
            _loggerFactory.CreateLogger<ImapConnectionManager>(),
            _oauthProvider, record.Id);

        return new ImapOperationBackend(
            connMgr,
            accountConfig,
            _oauthProvider,
            record.Id,
            _loggerFactory.CreateLogger<ImapOperationBackend>());
    }

}
