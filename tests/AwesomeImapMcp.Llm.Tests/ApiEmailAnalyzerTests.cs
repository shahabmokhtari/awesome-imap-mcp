using AwesomeImapMcp.Llm;

namespace AwesomeImapMcp.Llm.Tests;

public class ApiEmailAnalyzerTests
{
    [Fact]
    public void ExtractJson_ValidJson_ReturnsAsIs()
    {
        var json = """{"score": 42, "label": "not_spam"}""";
        var result = ApiEmailAnalyzer.ExtractJson(json);
        Assert.Equal(json, result);
    }

    [Fact]
    public void ExtractJson_MarkdownWrapped_ExtractsJson()
    {
        var input = """
            ```json
            {"score": 42, "label": "not_spam"}
            ```
            """;
        var result = ApiEmailAnalyzer.ExtractJson(input);
        Assert.Contains("\"score\"", result);
        Assert.Contains("42", result);
    }

    [Fact]
    public void ExtractJson_InvalidJson_WrapsInError()
    {
        var result = ApiEmailAnalyzer.ExtractJson("not valid json at all");
        Assert.Contains("parse_error", result);
        Assert.Contains("raw_response", result);
    }

    [Theory]
    [InlineData("gpt-4o-mini", 1000, 500, true)]
    [InlineData("claude-haiku-4-5", 1000, 500, true)]
    [InlineData("unknown-model", 1000, 500, false)]
    public void EstimateCost_ReturnsExpectedValues(string model, int input, int output, bool hasCost)
    {
        var cost = ApiEmailAnalyzer.EstimateCost(model, input, output);
        if (hasCost)
        {
            Assert.NotNull(cost);
            Assert.True(cost > 0);
        }
        else
        {
            Assert.Null(cost);
        }
    }

    [Fact]
    public void EstimateCost_NullTokens_ReturnsNull()
    {
        var cost = ApiEmailAnalyzer.EstimateCost("gpt-4o", null, null);
        Assert.Null(cost);
    }

    [Theory]
    [InlineData(AnalysisType.SpamScore, "spam")]
    [InlineData(AnalysisType.Category, "category")]
    [InlineData(AnalysisType.Priority, "priority")]
    [InlineData(AnalysisType.Summary, "summary")]
    [InlineData(AnalysisType.Custom, "analysis")]
    public void BuildSystemPrompt_ContainsRelevantKeywords(AnalysisType type, string keyword)
    {
        var prompt = ApiEmailAnalyzer.BuildSystemPrompt(type);
        Assert.Contains(keyword, prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUserPrompt_IncludesEmailFields()
    {
        var email = new EmailContent("Important Meeting", "boss@work.com", "Please join the meeting", null);
        var prompt = ApiEmailAnalyzer.BuildUserPrompt(email, AnalysisType.Priority);

        Assert.Contains("Important Meeting", prompt);
        Assert.Contains("boss@work.com", prompt);
        Assert.Contains("Please join the meeting", prompt);
    }

    [Fact]
    public void BuildUserPrompt_TruncatesLongBody()
    {
        var longBody = new string('x', 5000);
        var email = new EmailContent("Subject", "from@test.com", longBody, null);
        var prompt = ApiEmailAnalyzer.BuildUserPrompt(email, AnalysisType.Summary);

        Assert.Contains("[truncated]", prompt);
        Assert.True(prompt.Length < longBody.Length);
    }

    [Fact]
    public void BuildUserPrompt_UsesSnippetWhenNoBody()
    {
        var email = new EmailContent("Subject", "from@test.com", null, "A snippet");
        var prompt = ApiEmailAnalyzer.BuildUserPrompt(email, AnalysisType.Category);

        Assert.Contains("A snippet", prompt);
    }
}
