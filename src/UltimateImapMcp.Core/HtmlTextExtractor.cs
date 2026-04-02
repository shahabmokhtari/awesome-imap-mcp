using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace UltimateImapMcp.Core;

/// <summary>
/// Converts HTML email bodies to readable plain text.
/// Handles common email HTML patterns: block elements become newlines,
/// links are converted to markdown format, entities are decoded, and
/// whitespace is normalized.
/// </summary>
public static partial class HtmlTextExtractor
{
    /// <summary>
    /// Converts an HTML string to plain text suitable for display in CLI tools.
    /// Returns null if the input is null or empty.
    /// </summary>
    public static string? Convert(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var text = html;

        // 1. Remove <head> block entirely (style, meta, etc.)
        text = HeadRegex().Replace(text, "");

        // 2. Remove <style> and <script> blocks
        text = StyleRegex().Replace(text, "");
        text = ScriptRegex().Replace(text, "");

        // 3. Replace common block elements with newlines BEFORE stripping tags
        //    These elements create visual line breaks in rendered HTML.
        text = BrRegex().Replace(text, "\n");
        text = HrRegex().Replace(text, "\n---\n");

        // Block-level closing tags that should produce newlines
        text = BlockCloseRegex().Replace(text, "\n");

        // Block-level opening tags (p, div, h1-h6, li, tr, blockquote) — add newline before content
        text = BlockOpenRegex().Replace(text, "\n");

        // List items: prefix with "- "
        text = LiRegex().Replace(text, "\n- ");

        // Table cells: add a tab between cells
        text = TdThRegex().Replace(text, "\t");

        // 4. Convert <a href="url">text</a> to [text](url) markdown format
        text = AnchorRegex().Replace(text, match =>
        {
            var href = match.Groups[1].Value.Trim();
            var linkText = match.Groups[2].Value.Trim();

            // Strip any remaining tags from link text
            linkText = TagRegex().Replace(linkText, "");

            if (string.IsNullOrWhiteSpace(linkText) || linkText.Equals(href, StringComparison.OrdinalIgnoreCase))
                return href;

            return $"[{linkText}]({href})";
        });

        // 5. Remove all remaining HTML tags
        text = TagRegex().Replace(text, "");

        // 6. Decode HTML entities
        text = WebUtility.HtmlDecode(text);

        // 7. Normalize whitespace
        //    - Replace tabs with spaces
        text = text.Replace('\t', ' ');
        //    - Collapse multiple spaces (but not newlines) into single space
        text = MultiSpaceRegex().Replace(text, " ");
        //    - Trim each line
        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length);
        foreach (var line in lines)
        {
            sb.AppendLine(line.Trim());
        }
        text = sb.ToString();
        //    - Collapse 3+ consecutive newlines into 2
        text = MultiNewlineRegex().Replace(text, "\n\n");
        //    - Final trim
        text = text.Trim();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // --- Generated Regex patterns (compiled for performance) ---

    [GeneratedRegex(@"<head[\s>].*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadRegex();

    [GeneratedRegex(@"<style[\s>].*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleRegex();

    [GeneratedRegex(@"<script[\s>].*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex(@"<hr\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HrRegex();

    [GeneratedRegex(@"</(?:p|div|h[1-6]|li|tr|blockquote|table|ul|ol|section|article|header|footer|nav|main|aside)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockCloseRegex();

    [GeneratedRegex(@"<(?:p|div|h[1-6]|blockquote|table|ul|ol|section|article|header|footer|nav|main|aside)(?:\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockOpenRegex();

    [GeneratedRegex(@"<li(?:\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex LiRegex();

    [GeneratedRegex(@"<(?:td|th)(?:\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex TdThRegex();

    [GeneratedRegex("""<a\s[^>]*href\s*=\s*["']([^"']*)["'][^>]*>(.*?)</a>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[^\S\n]+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();
}
