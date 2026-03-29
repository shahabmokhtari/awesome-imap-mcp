using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public partial class LabelVocabularyTools(AppConfig config, ILogger<LabelVocabularyTools> logger)
{
    private static bool IsValidImapKeyword(string name) =>
        !string.IsNullOrEmpty(name) && ImapKeywordPattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_$-]+$")]
    private static partial Regex ImapKeywordPattern();

    [McpServerTool, Description(
        "List the configured label vocabulary. Returns all label definitions with name, description, and category. " +
        "Use these labels for consistency across sessions. You may also use labels not in this list, but you'll get a warning.")]
    public string ListLabels()
    {
        return McpJsonDefaults.LogToolCall(logger, "list_labels",
            new Dictionary<string, object?>(),
            () =>
            {
                var labels = config.Labels.Items.Select(l => new { l.Name, l.Description, l.Category }).ToList();
                return JsonSerializer.Serialize(new
                {
                    allow_cli_edits = config.Labels.AllowCliEdits,
                    labels
                }, McpJsonDefaults.Options);
            }, config);
    }

    [McpServerTool, Description(
        "Add a new label to the vocabulary. The label name must be a valid IMAP keyword (letters, digits, hyphens, underscores, $). " +
        "This helps maintain a consistent set of labels across CLI sessions.")]
    public string AddLabel(
        [Description("Label name (used as IMAP keyword)")] string name,
        [Description("Short description of when to use this label")] string description,
        [Description("Category for grouping (e.g. Priority, Status, Topic)")] string category = "")
    {
        return McpJsonDefaults.LogToolCall(logger, "add_label",
            new Dictionary<string, object?> { ["name"] = name, ["category"] = category },
            () =>
            {
                try
                {
                    if (!config.Labels.AllowCliEdits)
                        return McpJsonDefaults.Error("Label vocabulary editing is disabled by the administrator");
                    if (!IsValidImapKeyword(name))
                        return McpJsonDefaults.Error($"Invalid label name '{name}'. Must match pattern: letters, digits, hyphens, underscores, or $.");
                    if (config.Labels.Items.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        return McpJsonDefaults.Error($"Label '{name}' already exists");

                    var label = new LabelDefinition { Name = name, Description = description, Category = category };
                    config.Labels.Items.Add(label);
                    PersistConfig();
                    return JsonSerializer.Serialize(new { added = new { label.Name, label.Description, label.Category }, total_labels = config.Labels.Items.Count }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description(
        "Update an existing label's description or category. Label names are immutable \u2014 to rename, remove and re-add.")]
    public string UpdateLabel(
        [Description("Name of the label to update")] string name,
        [Description("New description (omit to keep current)")] string? description = null,
        [Description("New category (omit to keep current)")] string? category = null)
    {
        return McpJsonDefaults.LogToolCall(logger, "update_label",
            new Dictionary<string, object?> { ["name"] = name },
            () =>
            {
                try
                {
                    if (!config.Labels.AllowCliEdits)
                        return McpJsonDefaults.Error("Label vocabulary editing is disabled by the administrator");
                    var label = config.Labels.Items.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (label is null)
                        return McpJsonDefaults.Error($"Label '{name}' not found");
                    if (description is not null) label.Description = description;
                    if (category is not null) label.Category = category;
                    PersistConfig();
                    return JsonSerializer.Serialize(new { updated = new { label.Name, label.Description, label.Category } }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    [McpServerTool, Description(
        "Remove a label from the vocabulary. This does NOT remove the IMAP keyword from messages that already have it.")]
    public string RemoveLabel(
        [Description("Name of the label to remove")] string name)
    {
        return McpJsonDefaults.LogToolCall(logger, "remove_label",
            new Dictionary<string, object?> { ["name"] = name },
            () =>
            {
                try
                {
                    if (!config.Labels.AllowCliEdits)
                        return McpJsonDefaults.Error("Label vocabulary editing is disabled by the administrator");
                    var label = config.Labels.Items.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (label is null)
                        return McpJsonDefaults.Error($"Label '{name}' not found");
                    config.Labels.Items.Remove(label);
                    PersistConfig();
                    return JsonSerializer.Serialize(new { removed = name, remaining_labels = config.Labels.Items.Count }, McpJsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }

    private void PersistConfig()
    {
        if (config.SourcePath is not null)
            ConfigLoader.SaveToFile(config, config.SourcePath);
    }
}
