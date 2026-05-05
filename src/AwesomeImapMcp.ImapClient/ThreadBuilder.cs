using System.Security.Cryptography;
using System.Text;

namespace AwesomeImapMcp.ImapClient;

public static class ThreadBuilder
{
    /// <summary>
    /// Computes a thread ID from a message's Message-ID and References header.
    /// The thread ID is SHA256(root_message_id) where root is the first entry
    /// in the References chain, or the message's own Message-ID if no References.
    /// </summary>
    public static string? ComputeThreadId(string? messageId, string? referencesHeader)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return null;

        string rootId;

        if (!string.IsNullOrWhiteSpace(referencesHeader))
        {
            // References header is space-separated list of Message-IDs
            // First one is the root of the thread
            var refs = referencesHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rootId = refs[0];
        }
        else
        {
            rootId = messageId;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rootId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
