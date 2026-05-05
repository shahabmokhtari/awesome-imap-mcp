using AwesomeImapMcp.ImapClient;

namespace AwesomeImapMcp.ImapClient.Tests;

public class MessageParserTests
{
    [Fact]
    public void GenerateSnippet_TruncatesAt200Chars()
    {
        var longText = new string('a', 500);
        var snippet = MessageParser.GenerateSnippet(longText);
        Assert.True(snippet!.Length <= 200);
    }

    [Fact]
    public void GenerateSnippet_StripsExtraWhitespace()
    {
        var text = "Hello\n\n\n   World   \t\tfoo";
        var snippet = MessageParser.GenerateSnippet(text);
        Assert.Equal("Hello World foo", snippet);
    }

    [Fact]
    public void GenerateSnippet_NullInput_ReturnsNull()
    {
        Assert.Null(MessageParser.GenerateSnippet(null));
    }

    [Fact]
    public void NormalizeEmail_LowercasesAndTrims()
    {
        Assert.Equal("alice@test.com", MessageParser.NormalizeEmail("  Alice@Test.COM  "));
    }

    [Fact]
    public void ExtractEmailFromAddress_ParsesNameAndEmail()
    {
        Assert.Equal("alice@test.com", MessageParser.ExtractEmailFromAddress("Alice Smith <alice@test.com>"));
    }

    [Fact]
    public void ExtractEmailFromAddress_BareEmail()
    {
        Assert.Equal("alice@test.com", MessageParser.ExtractEmailFromAddress("alice@test.com"));
    }
}
