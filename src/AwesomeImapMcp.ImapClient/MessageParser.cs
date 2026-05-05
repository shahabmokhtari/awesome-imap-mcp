using System.Text.RegularExpressions;

namespace AwesomeImapMcp.ImapClient;

public static partial class MessageParser
{
    public static string? GenerateSnippet(string? text, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var normalized = WhitespacePattern().Replace(text, " ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    public static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    public static string ExtractEmailFromAddress(string address)
    {
        var match = EmailInAngleBrackets().Match(address);
        if (match.Success)
            return NormalizeEmail(match.Groups[1].Value);
        return NormalizeEmail(address);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"<([^>]+)>")]
    private static partial Regex EmailInAngleBrackets();
}
