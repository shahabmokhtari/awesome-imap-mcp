using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
using UltimateImapMcp.Core.Providers;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;
using UltimateImapMcp.RestBackend.Imap;
using UltimateImapMcp.RestBackend.Zoho;

namespace UltimateImapMcp.RestBackend;

/// <summary>
/// Routes backend creation to the correct implementation based on the account's
/// <c>backend_type</c> column (or derived from provider name).
///
/// Currently supported backends:
/// - "imap" (default): wraps existing IMAP sync and SMTP operations
/// - "zoho_rest": uses Zoho Mail REST API
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
    private readonly ImapSyncService _imapSyncService;
    private readonly IHttpClientFactory _httpClientFactory;
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
        ImapSyncService imapSyncService,
        IHttpClientFactory httpClientFactory,
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
        _imapSyncService = imapSyncService;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public IEmailSyncBackend CreateSyncBackend(string accountId)
    {
        var backendType = GetBackendType(accountId);

        return backendType switch
        {
            "zoho_rest" => CreateZohoSyncBackend(),
            _ => CreateImapSyncBackend(accountId)
        };
    }

    public IEmailOperationBackend CreateOperationBackend(string accountId)
    {
        var backendType = GetBackendType(accountId);

        return backendType switch
        {
            "zoho_rest" => CreateZohoOperationBackend(),
            _ => CreateImapOperationBackend(accountId)
        };
    }

    public string GetBackendType(string accountId)
    {
        var record = _accountRepo.ResolveAccount(accountId);
        if (record is null)
            throw new InvalidOperationException($"Account '{accountId}' not found.");

        // Use explicit backend_type if set
        if (!string.IsNullOrEmpty(record.BackendType) &&
            !record.BackendType.Equals("imap", StringComparison.OrdinalIgnoreCase))
        {
            return record.BackendType;
        }

        // Derive from provider for known REST-only providers
        if (record.Provider.Equals("zoho", StringComparison.OrdinalIgnoreCase) &&
            record.AuthType.Equals("oauth2", StringComparison.OrdinalIgnoreCase))
        {
            return "zoho_rest";
        }

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

    // ------------------------------------------------------------------
    // Zoho backend creation
    // ------------------------------------------------------------------

    private ZohoSyncBackend CreateZohoSyncBackend()
    {
        var apiClient = new ZohoApiClient(
            _httpClientFactory,
            _oauthProvider,
            _loggerFactory.CreateLogger<ZohoApiClient>());

        return new ZohoSyncBackend(
            apiClient,
            _accountRepo,
            _folderRepo,
            _messageRepo,
            _loggerFactory.CreateLogger<ZohoSyncBackend>());
    }

    private ZohoOperationBackend CreateZohoOperationBackend()
    {
        var apiClient = new ZohoApiClient(
            _httpClientFactory,
            _oauthProvider,
            _loggerFactory.CreateLogger<ZohoApiClient>());

        return new ZohoOperationBackend(
            apiClient,
            _accountRepo,
            _folderRepo,
            _messageRepo,
            _loggerFactory.CreateLogger<ZohoOperationBackend>());
    }
}
