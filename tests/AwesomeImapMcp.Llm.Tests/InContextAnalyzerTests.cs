using System.Text.Json;
using AwesomeImapMcp.Llm;

namespace AwesomeImapMcp.Llm.Tests;

public class InContextAnalyzerTests
{
    private readonly InContextAnalyzer _analyzer = new();

    [Fact]
    public void SupportsBackgroundAnalysis_ReturnsFalse()
    {
        Assert.False(_analyzer.SupportsBackgroundAnalysis);
    }

    [Theory]
    [InlineData(AnalysisType.SpamScore)]
    [InlineData(AnalysisType.Category)]
    [InlineData(AnalysisType.Priority)]
    [InlineData(AnalysisType.Summary)]
    [InlineData(AnalysisType.Custom)]
    public async Task AnalyzeAsync_ReturnsInContextResult_ForAllTypes(AnalysisType type)
    {
        var email = new EmailContent("Test Subject", "sender@example.com", "Hello world", null);

        var result = await _analyzer.AnalyzeAsync(email, type);

        Assert.Equal(type, result.Type);
        Assert.Equal("in_context", result.ModelUsed);
        Assert.Null(result.TokensInput);
        Assert.Null(result.TokensOutput);
        Assert.Null(result.CostUsd);

        // ResultJson should be valid JSON with mode and instruction
        var json = JsonDocument.Parse(result.ResultJson);
        Assert.Equal("in_context", json.RootElement.GetProperty("mode").GetString());
        Assert.NotNull(json.RootElement.GetProperty("instruction").GetString());
        Assert.Equal("Test Subject", json.RootElement.GetProperty("email").GetProperty("subject").GetString());
    }

    [Fact]
    public async Task AnalyzeAsync_UsesSnippet_WhenBodyIsNull()
    {
        var email = new EmailContent("Subject", "from@test.com", null, "A short snippet");

        var result = await _analyzer.AnalyzeAsync(email, AnalysisType.Summary);

        var json = JsonDocument.Parse(result.ResultJson);
        Assert.Equal("A short snippet", json.RootElement.GetProperty("email").GetProperty("body").GetString());
    }

    [Fact]
    public void BuildPrompt_SpamScore_ContainsScoreInstruction()
    {
        var prompt = InContextAnalyzer.BuildPrompt(
            new EmailContent("s", "f", null, null), AnalysisType.SpamScore);

        Assert.Contains("score", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spam", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPrompt_Category_ContainsCategoryOptions()
    {
        var prompt = InContextAnalyzer.BuildPrompt(
            new EmailContent("s", "f", null, null), AnalysisType.Category);

        Assert.Contains("newsletter", prompt);
        Assert.Contains("transactional", prompt);
        Assert.Contains("personal", prompt);
    }

    [Fact]
    public void BuildPrompt_Priority_ContainsPriorityLevels()
    {
        var prompt = InContextAnalyzer.BuildPrompt(
            new EmailContent("s", "f", null, null), AnalysisType.Priority);

        Assert.Contains("low", prompt);
        Assert.Contains("normal", prompt);
        Assert.Contains("high", prompt);
        Assert.Contains("urgent", prompt);
    }
}
