using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.McpServer.Tools;

namespace UltimateImapMcp.McpServer.Tests.Tools;

public class LabelVocabularyToolsTests
{
    private static AppConfig MakeConfig(bool allowCliEdits = true, params LabelDefinition[] items) =>
        new() { Labels = new LabelsConfig { AllowCliEdits = allowCliEdits, Items = [.. items] } };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ListLabels_ReturnsConfiguredLabels()
    {
        var config = MakeConfig(true,
            new LabelDefinition { Name = "urgent", Description = "Fast", Category = "Priority" });
        var tools = new LabelVocabularyTools(config, NullLogger<LabelVocabularyTools>.Instance);
        var result = Parse(tools.ListLabels());
        Assert.True(result.GetProperty("allow_cli_edits").GetBoolean());
        Assert.Single(result.GetProperty("labels").EnumerateArray());
    }

    [Fact]
    public void AddLabel_Success()
    {
        var config = MakeConfig();
        var tools = new LabelVocabularyTools(config, NullLogger<LabelVocabularyTools>.Instance);
        var result = Parse(tools.AddLabel("urgent", "Fast response", "Priority"));
        Assert.Equal("urgent", result.GetProperty("added").GetProperty("Name").GetString());
        Assert.Equal(1, result.GetProperty("total_labels").GetInt32());
        Assert.Single(config.Labels.Items);
    }

    [Fact]
    public void AddLabel_RejectsInvalidName()
    {
        var config = MakeConfig();
        var tools = new LabelVocabularyTools(config, NullLogger<LabelVocabularyTools>.Instance);
        var result = Parse(tools.AddLabel("invalid name", "desc", "cat"));
        Assert.True(result.TryGetProperty("error", out _));
        Assert.Empty(config.Labels.Items);
    }

    [Fact]
    public void AddLabel_RejectsDuplicateCaseInsensitive()
    {
        var config = MakeConfig(true,
            new LabelDefinition { Name = "urgent", Description = "Fast", Category = "Priority" });
        var tools = new LabelVocabularyTools(config, NullLogger<LabelVocabularyTools>.Instance);
        var result = Parse(tools.AddLabel("URGENT", "desc", "cat"));
        Assert.True(result.TryGetProperty("error", out _));
        Assert.Single(config.Labels.Items);
    }

    [Fact]
    public void AddLabel_RejectsWhenCliEditsDisabled()
    {
        var config = MakeConfig(allowCliEdits: false);
        var tools = new LabelVocabularyTools(config, NullLogger<LabelVocabularyTools>.Instance);
        var result = Parse(tools.AddLabel("urgent", "desc", "cat"));
        Assert.Contains("disabled", result.GetProperty("error").GetString());
    }

    [Fact]
    public void UpdateLabel_Success()
    {
        var config = MakeConfig(true,
            new LabelDefinition { Name = "urgent", Description = "Old", Category = "Priority" });
        var tools = new LabelVocabularyTools(config, NullLogger<LabelVocabularyTools>.Instance);
        var result = Parse(tools.UpdateLabel("URGENT", description: "New description"));
        Assert.Equal("New description", result.GetProperty("updated").GetProperty("Description").GetString());
        Assert.Equal("New description", config.Labels.Items[0].Description);
    }

    [Fact]
    public void UpdateLabel_NotFound()
    {
        var config = MakeConfig();
        var tools = new LabelVocabularyTools(config, NullLogger<LabelVocabularyTools>.Instance);
        var result = Parse(tools.UpdateLabel("nonexistent", description: "desc"));
        Assert.True(result.TryGetProperty("error", out _));
    }

    [Fact]
    public void UpdateLabel_RejectsWhenCliEditsDisabled()
    {
        var config = MakeConfig(allowCliEdits: false,
            new LabelDefinition { Name = "urgent", Description = "Old", Category = "Priority" });
        var tools = new LabelVocabularyTools(config, NullLogger<LabelVocabularyTools>.Instance);
        var result = Parse(tools.UpdateLabel("urgent", description: "New"));
        Assert.Contains("disabled", result.GetProperty("error").GetString());
    }

    [Fact]
    public void RemoveLabel_Success()
    {
        var config = MakeConfig(true,
            new LabelDefinition { Name = "urgent", Description = "Fast", Category = "Priority" });
        var tools = new LabelVocabularyTools(config, NullLogger<LabelVocabularyTools>.Instance);
        var result = Parse(tools.RemoveLabel("urgent"));
        Assert.Equal("urgent", result.GetProperty("removed").GetString());
        Assert.Equal(0, result.GetProperty("remaining_labels").GetInt32());
        Assert.Empty(config.Labels.Items);
    }

    [Fact]
    public void RemoveLabel_NotFound()
    {
        var config = MakeConfig();
        var tools = new LabelVocabularyTools(config, NullLogger<LabelVocabularyTools>.Instance);
        var result = Parse(tools.RemoveLabel("nonexistent"));
        Assert.True(result.TryGetProperty("error", out _));
    }

    [Fact]
    public void RemoveLabel_RejectsWhenCliEditsDisabled()
    {
        var config = MakeConfig(allowCliEdits: false,
            new LabelDefinition { Name = "urgent", Description = "Fast", Category = "Priority" });
        var tools = new LabelVocabularyTools(config, NullLogger<LabelVocabularyTools>.Instance);
        var result = Parse(tools.RemoveLabel("urgent"));
        Assert.Contains("disabled", result.GetProperty("error").GetString());
        Assert.Single(config.Labels.Items);
    }
}
