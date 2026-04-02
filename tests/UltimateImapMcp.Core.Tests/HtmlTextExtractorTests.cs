using UltimateImapMcp.Core;

namespace UltimateImapMcp.Core.Tests;

public class HtmlTextExtractorTests
{
    [Fact]
    public void Convert_NullInput_ReturnsNull()
    {
        Assert.Null(HtmlTextExtractor.Convert(null));
    }

    [Fact]
    public void Convert_EmptyInput_ReturnsNull()
    {
        Assert.Null(HtmlTextExtractor.Convert(""));
    }

    [Fact]
    public void Convert_WhitespaceOnly_ReturnsNull()
    {
        Assert.Null(HtmlTextExtractor.Convert("   \n\t  "));
    }

    [Fact]
    public void Convert_PlainText_ReturnsSameText()
    {
        Assert.Equal("Hello world", HtmlTextExtractor.Convert("Hello world"));
    }

    [Fact]
    public void Convert_StripsTags()
    {
        var html = "<p>Hello <b>world</b></p>";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Contains("Hello", result!);
        Assert.Contains("world", result);
        Assert.DoesNotContain("<p>", result);
        Assert.DoesNotContain("<b>", result);
    }

    [Fact]
    public void Convert_BrTags_BecomeNewlines()
    {
        var html = "Line one<br>Line two<br/>Line three<BR />Line four";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Contains("Line one\nLine two\nLine three\nLine four", result!);
    }

    [Fact]
    public void Convert_ParagraphTags_BecomeNewlines()
    {
        var html = "<p>First paragraph</p><p>Second paragraph</p>";
        var result = HtmlTextExtractor.Convert(html);
        Assert.NotNull(result);
        Assert.Contains("First paragraph", result);
        Assert.Contains("Second paragraph", result);
        // Should have newline separation
        var firstIdx = result.IndexOf("First paragraph");
        var secondIdx = result.IndexOf("Second paragraph");
        var between = result[firstIdx..secondIdx];
        Assert.Contains("\n", between);
    }

    [Fact]
    public void Convert_DivTags_BecomeNewlines()
    {
        var html = "<div>Block one</div><div>Block two</div>";
        var result = HtmlTextExtractor.Convert(html);
        Assert.NotNull(result);
        Assert.Contains("Block one", result);
        Assert.Contains("Block two", result);
        var firstIdx = result.IndexOf("Block one");
        var secondIdx = result.IndexOf("Block two");
        var between = result[firstIdx..secondIdx];
        Assert.Contains("\n", between);
    }

    [Fact]
    public void Convert_HeadingTags_BecomeNewlines()
    {
        var html = "<h1>Title</h1><p>Content</p>";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Contains("Title", result!);
        Assert.Contains("Content", result);
    }

    [Fact]
    public void Convert_Anchor_ConvertedToMarkdown()
    {
        var html = """<a href="https://example.com">Click here</a>""";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Equal("[Click here](https://example.com)", result);
    }

    [Fact]
    public void Convert_Anchor_TextMatchesUrl_JustShowsUrl()
    {
        var html = """<a href="https://example.com">https://example.com</a>""";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Equal("https://example.com", result);
    }

    [Fact]
    public void Convert_Anchor_EmptyText_JustShowsUrl()
    {
        var html = """<a href="https://example.com"></a>""";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Equal("https://example.com", result);
    }

    [Fact]
    public void Convert_DecodesHtmlEntities()
    {
        var html = "Tom &amp; Jerry &lt;friends&gt; &quot;forever&quot; &#39;always&#39;";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Equal("Tom & Jerry <friends> \"forever\" 'always'", result);
    }

    [Fact]
    public void Convert_NbspEntity_BecomesSpace()
    {
        var html = "Hello&nbsp;world";
        var result = HtmlTextExtractor.Convert(html);
        // &nbsp; decodes to \u00A0 which gets normalized
        Assert.Contains("Hello", result!);
        Assert.Contains("world", result);
    }

    [Fact]
    public void Convert_StyleAndScript_Removed()
    {
        var html = """
            <html>
            <head><style>body { color: red; }</style></head>
            <body>
            <script>alert('xss');</script>
            <p>Visible content</p>
            </body>
            </html>
            """;
        var result = HtmlTextExtractor.Convert(html);
        Assert.Contains("Visible content", result!);
        Assert.DoesNotContain("color: red", result);
        Assert.DoesNotContain("alert", result);
    }

    [Fact]
    public void Convert_HeadSection_Removed()
    {
        var html = """
            <html>
            <head>
                <meta charset="utf-8">
                <title>Email title</title>
                <style>.class { margin: 0; }</style>
            </head>
            <body><p>Body text only</p></body>
            </html>
            """;
        var result = HtmlTextExtractor.Convert(html);
        Assert.Contains("Body text only", result!);
        Assert.DoesNotContain("Email title", result);
        Assert.DoesNotContain("margin", result);
    }

    [Fact]
    public void Convert_HrTag_BecomesSeparator()
    {
        var html = "Above<hr>Below";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Contains("Above", result!);
        Assert.Contains("---", result);
        Assert.Contains("Below", result);
    }

    [Fact]
    public void Convert_ListItems_PrefixedWithDash()
    {
        var html = "<ul><li>Item one</li><li>Item two</li></ul>";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Contains("- Item one", result!);
        Assert.Contains("- Item two", result);
    }

    [Fact]
    public void Convert_NormalizesExcessiveWhitespace()
    {
        var html = "<p>  Too    many    spaces  </p>";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Equal("Too many spaces", result);
    }

    [Fact]
    public void Convert_CollapsesExcessiveNewlines()
    {
        var html = "<p>One</p><p></p><p></p><p></p><p>Two</p>";
        var result = HtmlTextExtractor.Convert(html);
        // Should not have more than 2 consecutive newlines
        Assert.DoesNotContain("\n\n\n", result!);
        Assert.Contains("One", result);
        Assert.Contains("Two", result);
    }

    [Fact]
    public void Convert_RealWorldNewsletter_ExtractsReadableText()
    {
        var html = """
            <html>
            <head><style>body{font-family:Arial}</style></head>
            <body>
                <div style="max-width:600px;margin:auto;">
                    <h2>Weekly Newsletter</h2>
                    <p>Dear subscriber,</p>
                    <p>Here are this week's highlights:</p>
                    <ul>
                        <li>New feature: <a href="https://example.com/feature">Dark mode</a></li>
                        <li>Bug fix: Login issues resolved</li>
                    </ul>
                    <p>Best regards,<br>The Team</p>
                    <hr>
                    <p style="font-size:10px;">
                        <a href="https://example.com/unsubscribe">Unsubscribe</a>
                    </p>
                </div>
            </body>
            </html>
            """;
        var result = HtmlTextExtractor.Convert(html);

        Assert.Contains("Weekly Newsletter", result!);
        Assert.Contains("Dear subscriber", result);
        Assert.Contains("[Dark mode](https://example.com/feature)", result);
        Assert.Contains("Bug fix: Login issues resolved", result);
        Assert.Contains("Best regards", result);
        Assert.Contains("The Team", result);
        Assert.Contains("[Unsubscribe](https://example.com/unsubscribe)", result);
        // No HTML tags remaining
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
    }

    [Fact]
    public void Convert_TagsOnlyHtml_ReturnsNull()
    {
        var html = "<div><span></span></div>";
        Assert.Null(HtmlTextExtractor.Convert(html));
    }

    [Fact]
    public void Convert_MultipleAnchors_AllConverted()
    {
        var html = """See <a href="https://a.com">link A</a> and <a href="https://b.com">link B</a>""";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Contains("[link A](https://a.com)", result!);
        Assert.Contains("[link B](https://b.com)", result);
    }

    [Fact]
    public void Convert_NestedTags_ExtractsText()
    {
        var html = "<div><p>Outer <strong>bold <em>italic</em></strong> text</p></div>";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Contains("Outer bold italic text", result!);
    }

    [Fact]
    public void Convert_TableCells_SeparatedByWhitespace()
    {
        var html = "<table><tr><td>Name</td><td>Value</td></tr></table>";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Contains("Name", result!);
        Assert.Contains("Value", result);
    }

    [Fact]
    public void Convert_SingleQuoteInAnchorHref_Works()
    {
        var html = "<a href='https://example.com'>Link</a>";
        var result = HtmlTextExtractor.Convert(html);
        Assert.Equal("[Link](https://example.com)", result);
    }
}
