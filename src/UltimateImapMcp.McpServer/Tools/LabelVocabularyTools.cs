using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public partial class LabelVocabularyTools(AppConfig config)
{
    private readonly AppConfig _config = config;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static bool IsValidImapKeyword(string name) =>
        !string.IsNullOrEmpty(name) && ImapKeywordPattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_$-]+$")]
    private static partial Regex ImapKeywordPattern();

    [McpServerTool, Description(
        "List the configured label vocabulary. Returns all label definitions with name, description, and category. " +
        "Use these labels for consistency across sessions. You may also use labels not in this list, but you'll get a warning.")]
    public string ListLabels()
    {
        var labels = _config.Labels.Items.Select(l => new { l.Name, l.Description, l.Category }).ToList();
        return JsonSerializer.Serialize(new
        {
            allow_cli_edits = _config.Labels.AllowCliEdits,
            labels
        }, JsonOptions);
    }

    [McpServerTool, Description(
        "Add a new label to the vocabulary. The label name must be a valid IMAP keyword (letters, digits, hyphens, underscores, $). " +
        "This helps maintain a consistent set of labels across CLI sessions.")]
    public string AddLabel(
        [Description("Label name (used as IMAP keyword)")] string name,
        [Description("Short description of when to use this label")] string description,
        [Description("Category for grouping (e.g. Priority, Status, Topic)")] string category = "")
    {
        try
        {
            if (!_config.Labels.AllowCliEdits)
                return JsonSerializer.Serialize(new { error = "Label vocabulary editing is disabled by the administrator" }, JsonOptions);
            if (!IsValidImapKeyword(name))
                return JsonSerializer.Serialize(new { error = $"Invalid label name '{name}'. Must match pattern: letters, digits, hyphens, underscores, or $." }, JsonOptions);
            if (_config.Labels.Items.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return JsonSerializer.Serialize(new { error = $"Label '{name}' already exists" }, JsonOptions);

            var label = new LabelDefinition { Name = name, Description = description, Category = category };
            _config.Labels.Items.Add(label);
            PersistConfig();
            return JsonSerializer.Serialize(new { added = new { label.Name, label.Description, label.Category }, total_labels = _config.Labels.Items.Count }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool, Description(
        "Update an existing label's description or category. Label names are immutable \u2014 to rename, remove and re-add.")]
    public string UpdateLabel(
        [Description("Name of the label to update")] string name,
        [Description("New description (omit to keep current)")] string? description = null,
        [Description("New category (omit to keep current)")] string? category = null)
    {
        try
        {
            if (!_config.Labels.AllowCliEdits)
                return JsonSerializer.Serialize(new { error = "Label vocabulary editing is disabled by the administrator" }, JsonOptions);
            var label = _config.Labels.Items.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (label is null)
                return JsonSerializer.Serialize(new { error = $"Label '{name}' not found" }, JsonOptions);
            if (description is not null) label.Description = description;
            if (category is not null) label.Category = category;
            PersistConfig();
            return JsonSerializer.Serialize(new { updated = new { label.Name, label.Description, label.Category } }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool, Description(
        "Remove a label from the vocabulary. This does NOT remove the IMAP keyword from messages that already have it.")]
    public string RemoveLabel(
        [Description("Name of the label to remove")] string name)
    {
        try
        {
            if (!_config.Labels.AllowCliEdits)
                return JsonSerializer.Serialize(new { error = "Label vocabulary editing is disabled by the administrator" }, JsonOptions);
            var label = _config.Labels.Items.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (label is null)
                return JsonSerializer.Serialize(new { error = $"Label '{name}' not found" }, JsonOptions);
            _config.Labels.Items.Remove(label);
            PersistConfig();
            return JsonSerializer.Serialize(new { removed = name, remaining_labels = _config.Labels.Items.Count }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    private void PersistConfig()
    {
        if (_config.SourcePath is not null)
            ConfigLoader.SaveToFile(_config, _config.SourcePath);
    }
}
