using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AwesomeImapMcp.Core;

/// <summary>
/// Chooses which cached body part to present for a message.
///
/// A message can carry both a <c>text/plain</c> and a <c>text/html</c> part.
/// Trusting the plain part whenever it is non-null is wrong: many senders ship a
/// placeholder plain part ("undefined", "&lt;nil&gt;", a single newline, "Your email
/// client does not support HTML.", or a bare "View Web Version" link) while the real
/// content sits in the HTML part. Those messages read as empty even though nothing
/// was lost in transit.
///
/// The discriminator is prose, not bytes: strip URLs, collapse whitespace, and see
/// how much human-readable text is actually left. A plain part with too little prose
/// loses to a populated HTML part — but only when converting the HTML genuinely
/// yields more prose, so a short real reply is never swapped for a weaker rendering.
/// </summary>
public static partial class MessageBodySelector
{
    /// <summary>
    /// Minimum prose characters (URLs stripped, whitespace collapsed) for a
    /// <c>text/plain</c> part to be trusted on its own. The longest stub measured in
    /// the wild is a 40-character "does not support HTML" notice; the
    /// shortest genuine body observed comfortably clears this bar.
    /// </summary>
    public const int MinProseChars = 120;

    /// <summary>
    /// Returns the best available body and its format: <c>"text"</c> when the plain
    /// part is used, <c>"html_converted"</c> when the HTML part was converted, or
    /// <c>(null, null)</c> when the message has neither part.
    /// </summary>
    public static (string? Body, string? Format) Select(string? bodyText, string? bodyHtml)
    {
        var textProse = ProseLength(bodyText);

        // A substantive plain part always wins — this is the common path.
        if (textProse >= MinProseChars)
            return (bodyText, "text");

        // The plain part is missing or a stub. Prefer the HTML part, but only if
        // converting it actually recovers more prose than we already have.
        var converted = HtmlTextExtractor.Convert(bodyHtml);
        if (converted is not null && ProseLength(converted) > textProse)
            return (converted, "html_converted");

        // Nothing better exists. Keep the stub rather than dropping content that
        // callers can see today.
        return bodyText is not null ? (bodyText, "text") : (null, null);
    }

    /// <summary>
    /// Counts the human-readable characters a reader would actually gain from this
    /// part: invisible padding, URLs, "your client cannot display HTML" boilerplate,
    /// and ASCII rule decoration are all removed first.
    ///
    /// Subtracting the boilerplate — rather than just pattern-matching it — is what
    /// keeps a real newsletter that merely carries a "view in browser" line at the
    /// top from being mistaken for a stub. The line is removed from both, and only
    /// the one with nothing left behind it is treated as empty.
    /// </summary>
    public static int ProseLength(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return 0;

        var stripped = StripInvisible(body);
        stripped = UrlRegex().Replace(stripped, " ");
        stripped = BoilerplateRegex().Replace(stripped, " ");
        stripped = DecorationRegex().Replace(stripped, " ");
        return WhitespaceRegex().Replace(stripped, " ").Trim().Length;
    }

    /// <summary>
    /// Removes characters that occupy no visual space. Senders pad preview text with
    /// thousands of zero-width joiners, soft hyphens and BOMs to control what an
    /// inbox shows; <c>\s</c> does not match any of them, so without this a body of
    /// 8,860 invisible characters would score as 8,860 characters of prose.
    /// </summary>
    private static string StripInvisible(string body)
    {
        var sb = new StringBuilder(body.Length);
        foreach (var ch in body)
        {
            // Cf covers ZWNJ/ZWJ, LRM/RLM, the BOM and the soft hyphen;
            // U+034F (combining grapheme joiner) is a mark but equally invisible.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format || ch == '͏')
                continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\b(?:https?|mailto|ftp):\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>
    /// Sender boilerplate that announces the plain part is not the content. These
    /// are declarations by the sender, not prose the reader wants.
    /// </summary>
    [GeneratedRegex(
        """
        (?:unfortunately,?\s*)?your\s+(?:email\s+)?(?:client|browser)\s+(?:cannot|can\s*not|can't|does\s+not|doesn't)\s+(?:display|support|render)\s+html[^.\n]*\.?
        |to\s+view\s+(?:this|the)\s+(?:email|message)[^.\n]*\.?
        |(?:if\s+you\s+(?:cannot|can't|are\s+unable\s+to)\s+(?:see|view|read)|having\s+trouble\s+(?:viewing|reading))[^.\n]*\.?
        |(?:click\s+here\s+to\s+)?view\s+(?:this\s+)?(?:email\s+)?(?:as\s+a?\s*)?(?:web\s*(?:page|version)|in\s+(?:your\s+)?browser|online(?:\s+version)?)\s*:?
        |view\s+web\s+version\s*:?
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex BoilerplateRegex();

    /// <summary>Runs of ASCII rule characters used as separators (===, ---, ___).</summary>
    [GeneratedRegex(@"[=\-_*~|#•·–—]{3,}")]
    private static partial Regex DecorationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
