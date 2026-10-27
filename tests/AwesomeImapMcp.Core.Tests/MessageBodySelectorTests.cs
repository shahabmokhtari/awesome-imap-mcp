using AwesomeImapMcp.Core;

namespace AwesomeImapMcp.Core.Tests;

/// <summary>
/// The failing shapes here mirror real-world sender behaviour: a stub
/// text/plain part is picked over a populated text/html part, so the body reads
/// as empty even though the content is sitting in the HTML part.
/// </summary>
public class MessageBodySelectorTests
{
    // A realistic HTML part: enough prose that falling back to it is clearly better.
    private const string RichHtml = """
        <html><head><style>.x{color:red}</style></head><body>
        <p>Your order #12345 has shipped and is expected to arrive on Tuesday.</p>
        <p>Tracking number 1Z999AA10123456784. You can follow the delivery in your account.</p>
        <p>Thanks for shopping with us. Questions? Reply to this email.</p>
        </body></html>
        """;

    // ---- the five measured failure shapes ----

    [Fact]
    public void Select_EmptyTextPart_FallsBackToHtml()
    {
        // A payments notice: body_text length 0, body_html ~23 KB.
        var (body, format) = MessageBodySelector.Select("", RichHtml);

        Assert.Equal("html_converted", format);
        Assert.Contains("has shipped", body!);
    }

    [Fact]
    public void Select_NewlineOnlyTextPart_FallsBackToHtml()
    {
        // A bank newsletter: body is a single newline.
        var (body, format) = MessageBodySelector.Select("\n", RichHtml);

        Assert.Equal("html_converted", format);
        Assert.Contains("has shipped", body!);
    }

    [Fact]
    public void Select_UndefinedPlaceholder_FallsBackToHtml()
    {
        // A retailer promo: body_text is the literal 9-char token "undefined".
        var (body, format) = MessageBodySelector.Select("undefined", RichHtml);

        Assert.Equal("html_converted", format);
        Assert.Contains("has shipped", body!);
    }

    [Fact]
    public void Select_NilPlaceholder_FallsBackToHtml()
    {
        // An exchange price alert: body and snippet are both the literal token "<nil>".
        var (body, format) = MessageBodySelector.Select("<nil>", RichHtml);

        Assert.Equal("html_converted", format);
        Assert.Contains("has shipped", body!);
    }

    [Fact]
    public void Select_HtmlCapabilityStub_FallsBackToHtml()
    {
        // A parking receipt: 115 KB message, 40-char text part.
        var (body, format) = MessageBodySelector.Select(
            "Your email client does not support HTML.", RichHtml);

        Assert.Equal("html_converted", format);
        Assert.Contains("has shipped", body!);
    }

    [Fact]
    public void Select_ViewWebVersionStub_FallsBackToHtml()
    {
        // A home-improvement retailer's promo, reproduced byte-for-byte in shape: 1,618 bytes made
        // up of one tracking URL, the "cannot display HTML" notice, and an ASCII rule.
        // Nothing a reader wants is in here.
        var retailerStub = "View Web Version: \nhttps://click.e.example-retailer.com/?qs="
            + new string('a', 1200)
            + "\n\nUnfortunately, your email client cannot display HTML or your settings "
            + "are turned off. To view this email, please copy and paste the link above "
            + "into your browser.\n\n"
            + new string('=', 40);

        var (body, format) = MessageBodySelector.Select(retailerStub, RichHtml);

        Assert.Equal("html_converted", format);
        Assert.Contains("has shipped", body!);
    }

    [Fact]
    public void Select_NewsletterWithViewInBrowserLine_KeepsItsTextPart()
    {
        // The opposite case, and the reason boilerplate is SUBTRACTED rather than
        // merely detected: 1,195 cached rows carry one of these lines above a
        // perfectly good text body. They must not be swapped for an HTML rendering.
        var newsletter = "View this email in your browser: https://news.example.com/x/9f2\n\n"
            + "Good morning. The council voted 7-2 last night to approve the Rainier "
            + "Avenue redesign, which adds protected bike lanes between Henderson and "
            + "Alaska. Construction is expected to begin in March and run through the "
            + "end of next year. Business owners along the corridor asked for staged "
            + "closures; the transportation department agreed to study it and report "
            + "back in January.";

        var (body, format) = MessageBodySelector.Select(newsletter, RichHtml);

        Assert.Equal("text", format);
        Assert.Equal(newsletter, body);
    }

    [Fact]
    public void Select_ZeroWidthPaddedPreheader_FallsBackToHtml()
    {
        // Security notices (new-login and account-change alerts): 3.5-8.8 KB text
        // parts made entirely of invisible padding used to control the inbox
        // preview. \s matches none of it.
        var padding = string.Concat(Enumerable.Repeat("‌‍‎‏­͏﻿ ", 500));

        var (body, format) = MessageBodySelector.Select("Was this you? " + padding, RichHtml);

        Assert.Equal("html_converted", format);
        Assert.Contains("has shipped", body!);
    }

    [Fact]
    public void ProseLength_CountsOnlyVisibleCharacters()
    {
        Assert.Equal(0, MessageBodySelector.ProseLength(new string('‌', 5000)));
        Assert.Equal(0, MessageBodySelector.ProseLength("﻿­͏​"));
        Assert.Equal(5, MessageBodySelector.ProseLength("He‌llo"));
    }

    [Fact]
    public void ProseLength_IgnoresUrlsAndRuleDecoration()
    {
        Assert.Equal(0, MessageBodySelector.ProseLength("https://example.com/a?b=c"));
        Assert.Equal(0, MessageBodySelector.ProseLength("========================"));
    }

    [Fact]
    public void ProseLength_KeepsPersianText()
    {
        // Farsi mail is in scope; stripping invisible marks must not gut real text.
        const string farsi = "سلام، لطفاً فاکتور ماه گذشته را برای من ارسال کنید. متشکرم.";
        Assert.True(MessageBodySelector.ProseLength(farsi) > 40);
    }

    // ---- behaviour that must NOT change ----

    [Fact]
    public void Select_SubstantiveTextPart_IsPreferredOverHtml()
    {
        var realBody = "Hi Alex,\n\nThanks for sending the signed documents over this "
            + "morning. I have forwarded them to the title company and will confirm once "
            + "they acknowledge receipt. Nothing further is needed from you today.\n\nBest,\nJordan";

        var (body, format) = MessageBodySelector.Select(realBody, RichHtml);

        Assert.Equal("text", format);
        Assert.Equal(realBody, body);
    }

    [Fact]
    public void Select_StubTextPart_NoHtml_KeepsTheStub()
    {
        // Nothing better exists — returning null here would LOSE data that
        // callers see today.
        var (body, format) = MessageBodySelector.Select("undefined", null);

        Assert.Equal("text", format);
        Assert.Equal("undefined", body);
    }

    [Fact]
    public void Select_StubTextPart_EmptyHtml_KeepsTheStub()
    {
        var (body, format) = MessageBodySelector.Select("undefined", "   \n  ");

        Assert.Equal("text", format);
        Assert.Equal("undefined", body);
    }

    [Fact]
    public void Select_NullText_FallsBackToHtml_AsBefore()
    {
        var (body, format) = MessageBodySelector.Select(null, RichHtml);

        Assert.Equal("html_converted", format);
        Assert.Contains("has shipped", body!);
    }

    [Fact]
    public void Select_BothMissing_ReturnsNulls()
    {
        var (body, format) = MessageBodySelector.Select(null, null);

        Assert.Null(body);
        Assert.Null(format);
    }

    [Fact]
    public void Select_ShortRealMessage_NotReplacedByWeakerHtml()
    {
        // A short but genuine reply must not be swapped for an HTML part that
        // carries less prose than the text part already has.
        var (body, format) = MessageBodySelector.Select(
            "Confirmed, see you at 5.", "<html><body><p>&nbsp;</p></body></html>");

        Assert.Equal("text", format);
        Assert.Equal("Confirmed, see you at 5.", body);
    }

    [Fact]
    public void Select_AttachmentOnlyMessage_StaysUnrecoverable()
    {
        // Content lives in a PDF; no converter fix can help. Must not throw.
        var (body, format) = MessageBodySelector.Select("\n", null);

        Assert.Equal("text", format);
        Assert.Equal("\n", body);
    }
}
