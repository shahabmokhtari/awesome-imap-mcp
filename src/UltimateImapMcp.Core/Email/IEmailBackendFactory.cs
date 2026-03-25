namespace UltimateImapMcp.Core.Email;

/// <summary>
/// Factory that creates the appropriate email backend for each account.
/// Routes based on provider and auth_type (e.g., IMAP for Gmail, REST for Zoho).
/// </summary>
public interface IEmailBackendFactory
{
    /// <summary>Create a sync backend for the given account.</summary>
    IEmailSyncBackend CreateSyncBackend(string accountId);

    /// <summary>Create an operation backend for the given account.</summary>
    IEmailOperationBackend CreateOperationBackend(string accountId);

    /// <summary>Get the backend type string for the given account (e.g., "imap", "zoho_rest").</summary>
    string GetBackendType(string accountId);
}
