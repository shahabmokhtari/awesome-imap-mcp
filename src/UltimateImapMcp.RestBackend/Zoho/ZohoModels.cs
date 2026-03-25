using System.Text.Json.Serialization;

namespace UltimateImapMcp.RestBackend.Zoho;

/// <summary>
/// Internal DTOs mapping Zoho Mail REST API JSON responses.
/// Only fields we actually use are included.
/// </summary>

// ---------------------------------------------------------------------------
// Envelope types — Zoho wraps every response in { "status": { "code": 200 }, "data": [...] }
// ---------------------------------------------------------------------------

internal sealed class ZohoResponse<T>
{
    [JsonPropertyName("status")]
    public ZohoStatus Status { get; set; } = new();

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

internal sealed class ZohoStatus
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

// ---------------------------------------------------------------------------
// Account
// ---------------------------------------------------------------------------

internal sealed class ZohoMailAccount
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("emailAddress")]
    public ZohoEmailAddress? EmailAddress { get; set; }

    [JsonPropertyName("accountName")]
    public string? AccountName { get; set; }
}

internal sealed class ZohoEmailAddress
{
    [JsonPropertyName("mailId")]
    public string? MailId { get; set; }
}

// ---------------------------------------------------------------------------
// Folder
// ---------------------------------------------------------------------------

internal sealed class ZohoFolder
{
    [JsonPropertyName("folderId")]
    public string FolderId { get; set; } = string.Empty;

    [JsonPropertyName("folderName")]
    public string FolderName { get; set; } = string.Empty;

    [JsonPropertyName("folderPath")]
    public string? FolderPath { get; set; }

    [JsonPropertyName("folderType")]
    public string? FolderType { get; set; }

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; set; }

    [JsonPropertyName("unreadMessageCount")]
    public int UnreadMessageCount { get; set; }
}

// ---------------------------------------------------------------------------
// Message list item (summary)
// ---------------------------------------------------------------------------

internal sealed class ZohoMessageSummary
{
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    [JsonPropertyName("folderId")]
    public string FolderId { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("sender")]
    public string? Sender { get; set; }

    [JsonPropertyName("toAddress")]
    public string? ToAddress { get; set; }

    [JsonPropertyName("ccAddress")]
    public string? CcAddress { get; set; }

    [JsonPropertyName("receivedTime")]
    public long ReceivedTime { get; set; }

    [JsonPropertyName("sentDateInGMT")]
    public long SentDateInGmt { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("status2")]
    public string? Status2 { get; set; }

    [JsonPropertyName("flagid")]
    public string? FlagId { get; set; }

    [JsonPropertyName("hasAttachment")]
    public bool HasAttachment { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("hasInline")]
    public bool HasInline { get; set; }

    [JsonPropertyName("inReplyTo")]
    public string? InReplyTo { get; set; }

    [JsonPropertyName("priority")]
    public string? Priority { get; set; }
}

// ---------------------------------------------------------------------------
// Message detail (full body)
// ---------------------------------------------------------------------------

internal sealed class ZohoMessageDetail
{
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    [JsonPropertyName("folderId")]
    public string FolderId { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("sender")]
    public string? Sender { get; set; }

    [JsonPropertyName("toAddress")]
    public string? ToAddress { get; set; }

    [JsonPropertyName("ccAddress")]
    public string? CcAddress { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("htmlContent")]
    public string? HtmlContent { get; set; }

    [JsonPropertyName("receivedTime")]
    public long ReceivedTime { get; set; }

    [JsonPropertyName("sentDateInGMT")]
    public long SentDateInGmt { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("flagid")]
    public string? FlagId { get; set; }

    [JsonPropertyName("hasAttachment")]
    public bool HasAttachment { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("inReplyTo")]
    public string? InReplyTo { get; set; }

    [JsonPropertyName("messageIdHeader")]
    public string? MessageIdHeader { get; set; }

    [JsonPropertyName("references")]
    public string? References { get; set; }
}

// ---------------------------------------------------------------------------
// Send message request body
// ---------------------------------------------------------------------------

internal sealed class ZohoSendRequest
{
    [JsonPropertyName("fromAddress")]
    public string FromAddress { get; set; } = string.Empty;

    [JsonPropertyName("toAddress")]
    public string ToAddress { get; set; } = string.Empty;

    [JsonPropertyName("ccAddress")]
    public string? CcAddress { get; set; }

    [JsonPropertyName("bccAddress")]
    public string? BccAddress { get; set; }

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("inReplyTo")]
    public string? InReplyTo { get; set; }

    [JsonPropertyName("mailFormat")]
    public string MailFormat { get; set; } = "plaintext";
}

// ---------------------------------------------------------------------------
// Move message request body
// ---------------------------------------------------------------------------

internal sealed class ZohoMoveRequest
{
    [JsonPropertyName("destfolderId")]
    public string DestFolderId { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Flag update request body
// ---------------------------------------------------------------------------

internal sealed class ZohoFlagUpdateRequest
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("flagid")]
    public string? FlagId { get; set; }
}
