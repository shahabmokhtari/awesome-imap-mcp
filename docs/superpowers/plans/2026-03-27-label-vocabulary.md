# Label Vocabulary System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a configurable, global label vocabulary to the MCP server so CLI clients can use consistent labels across sessions, with CRUD via MCP tools and dashboard management.

**Architecture:** Labels are stored in `config.json` as a `labels` section with `allow_cli_edits` toggle and `items` array. A new `LabelVocabularyTools` MCP tool class provides list/add/update/remove operations. The existing `label_messages` tool warns when applying labels not in the vocabulary. Dashboard settings API and frontend are extended to manage labels.

**Tech Stack:** .NET 10 / C# 13, xUnit, React/TypeScript, TailwindCSS

**Spec:** `docs/superpowers/specs/2026-03-27-label-vocabulary-design.md`

---

### Task 1: Config Model — `LabelsConfig` and `LabelDefinition`

**Files:**
- Modify: `src/AwesomeImapMcp.Core/Configuration/AppConfig.cs:36-38` (add Labels property after OAuthProviders)
- Modify: `src/AwesomeImapMcp.Core/Configuration/ConfigLoader.cs:69-109` (add Labels to SaveToFile sanitized copy)
- Test: `tests/AwesomeImapMcp.Core.Tests/Configuration/ConfigLoaderTests.cs`

- [ ] **Step 1: Write test — labels config loads from JSON**

Add to `tests/AwesomeImapMcp.Core.Tests/Configuration/ConfigLoaderTests.cs`:

```csharp
[Fact]
public void Load_LabelsConfig_ParsesCorrectly()
{
    var json = """
    {
      "labels": {
        "allow_cli_edits": false,
        "items": [
          { "name": "urgent", "description": "Needs fast response", "category": "Priority" },
          { "name": "follow-up", "description": "Check back later", "category": "Status" }
        ]
      }
    }
    """;
    var tmpFile = Path.GetTempFileName();
    File.WriteAllText(tmpFile, json);
    try
    {
        var config = ConfigLoader.LoadFromFile(tmpFile);
        Assert.False(config.Labels.AllowCliEdits);
        Assert.Equal(2, config.Labels.Items.Count);
        Assert.Equal("urgent", config.Labels.Items[0].Name);
        Assert.Equal("Needs fast response", config.Labels.Items[0].Description);
        Assert.Equal("Priority", config.Labels.Items[0].Category);
    }
    finally { File.Delete(tmpFile); }
}

[Fact]
public void Load_NoLabelsSection_DefaultsApplied()
{
    var json = """{ "accounts": [] }""";
    var tmpFile = Path.GetTempFileName();
    File.WriteAllText(tmpFile, json);
    try
    {
        var config = ConfigLoader.LoadFromFile(tmpFile);
        Assert.True(config.Labels.AllowCliEdits);
        Assert.Empty(config.Labels.Items);
    }
    finally { File.Delete(tmpFile); }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/AwesomeImapMcp.Core.Tests/ --filter "Load_LabelsConfig|Load_NoLabelsSection_DefaultsApplied" -v n`
Expected: FAIL — `AppConfig` has no `Labels` property.

- [ ] **Step 3: Add `LabelsConfig`, `LabelDefinition`, and `Labels` property**

In `src/AwesomeImapMcp.Core/Configuration/AppConfig.cs`, add after the `OAuthProviders` property (line 37):

```csharp
[JsonPropertyName("labels")]
public LabelsConfig Labels { get; set; } = new();
```

Add two new classes at the end of the file (before the closing of the namespace, which is file-scoped):

```csharp
/// <summary>Global label vocabulary configuration.</summary>
public class LabelsConfig
{
    [JsonPropertyName("allow_cli_edits")]
    public bool AllowCliEdits { get; set; } = true;

    [JsonPropertyName("items")]
    public List<LabelDefinition> Items { get; set; } = [];
}

/// <summary>A single label definition in the vocabulary.</summary>
public class LabelDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/AwesomeImapMcp.Core.Tests/ --filter "Load_LabelsConfig|Load_NoLabelsSection_DefaultsApplied" -v n`
Expected: PASS

- [ ] **Step 5: Write test — SaveToFile persists labels**

Add to `ConfigLoaderTests.cs`:

```csharp
[Fact]
public void SaveToFile_PersistsLabels()
{
    var config = new AppConfig
    {
        Labels = new LabelsConfig
        {
            AllowCliEdits = false,
            Items =
            [
                new LabelDefinition { Name = "urgent", Description = "Fast response", Category = "Priority" }
            ]
        }
    };
    var tmpFile = Path.GetTempFileName();
    try
    {
        ConfigLoader.SaveToFile(config, tmpFile);
        var reloaded = ConfigLoader.LoadFromFile(tmpFile);
        Assert.False(reloaded.Labels.AllowCliEdits);
        Assert.Single(reloaded.Labels.Items);
        Assert.Equal("urgent", reloaded.Labels.Items[0].Name);
    }
    finally { File.Delete(tmpFile); }
}
```

- [ ] **Step 6: Run test to verify it fails**

Run: `dotnet test tests/AwesomeImapMcp.Core.Tests/ --filter "SaveToFile_PersistsLabels" -v n`
Expected: FAIL — `SaveToFile` doesn't include `Labels` in the sanitized copy.

- [ ] **Step 7: Add Labels to SaveToFile sanitized copy**

In `src/AwesomeImapMcp.Core/Configuration/ConfigLoader.cs`, in the `SaveToFile` method's `new AppConfig { ... }` block, add after line 108 (`OAuthProviders = config.OAuthProviders,`):

```csharp
Labels = config.Labels,
```

- [ ] **Step 8: Run all config tests to verify everything passes**

Run: `dotnet test tests/AwesomeImapMcp.Core.Tests/ -v n`
Expected: All tests PASS

- [ ] **Step 9: Commit**

```bash
git add src/AwesomeImapMcp.Core/Configuration/AppConfig.cs src/AwesomeImapMcp.Core/Configuration/ConfigLoader.cs tests/AwesomeImapMcp.Core.Tests/Configuration/ConfigLoaderTests.cs
git commit -m "feat: add LabelsConfig and LabelDefinition to config model"
```

---

### Task 2: MCP Tool — `list_labels`

**Files:**
- Create: `src/AwesomeImapMcp.McpServer/Tools/LabelVocabularyTools.cs`

- [ ] **Step 1: Create `LabelVocabularyTools` with `list_labels` tool**

Create `src/AwesomeImapMcp.McpServer/Tools/LabelVocabularyTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using AwesomeImapMcp.Core.Configuration;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public partial class LabelVocabularyTools(AppConfig config)
{
    private readonly AppConfig _config = config;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Validates that a label name is a valid IMAP keyword (RFC 3501 atom subset).</summary>
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
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/AwesomeImapMcp.McpServer/`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/AwesomeImapMcp.McpServer/Tools/LabelVocabularyTools.cs
git commit -m "feat: add list_labels MCP tool"
```

---

### Task 3: MCP Tools — `add_label`, `update_label`, `remove_label`

**Files:**
- Modify: `src/AwesomeImapMcp.McpServer/Tools/LabelVocabularyTools.cs`

- [ ] **Step 1: Add `add_label` tool**

Append to `LabelVocabularyTools` class:

```csharp
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

        return JsonSerializer.Serialize(new
        {
            added = new { label.Name, label.Description, label.Category },
            total_labels = _config.Labels.Items.Count
        }, JsonOptions);
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
    }
}
```

- [ ] **Step 2: Add `update_label` tool**

```csharp
[McpServerTool, Description(
    "Update an existing label's description or category. Label names are immutable — to rename, remove and re-add.")]
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

        return JsonSerializer.Serialize(new
        {
            updated = new { label.Name, label.Description, label.Category }
        }, JsonOptions);
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
    }
}
```

- [ ] **Step 3: Add `remove_label` tool**

```csharp
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

        return JsonSerializer.Serialize(new
        {
            removed = name,
            remaining_labels = _config.Labels.Items.Count
        }, JsonOptions);
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
    }
}
```

- [ ] **Step 4: Add the `PersistConfig` helper method**

Add to the class:

```csharp
private void PersistConfig()
{
    if (_config.SourcePath is not null)
        ConfigLoader.SaveToFile(_config, _config.SourcePath);
}
```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build src/AwesomeImapMcp.McpServer/`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/AwesomeImapMcp.McpServer/Tools/LabelVocabularyTools.cs
git commit -m "feat: add add_label, update_label, remove_label MCP tools"
```

---

### Task 4: Unit Tests for MCP Label Vocabulary Tools

**Files:**
- Create: `tests/AwesomeImapMcp.McpServer.Tests/Tools/LabelVocabularyToolsTests.cs`

Note: There is no existing `AwesomeImapMcp.McpServer.Tests` project. Create it first, or add tests to an existing test project that references the McpServer project. The simplest approach is to test via the `AppConfig` object directly since the tools are plain methods on a class that takes `AppConfig` in the constructor.

- [ ] **Step 1: Create McpServer test project if it doesn't exist**

Run:
```bash
dotnet new xunit -o tests/AwesomeImapMcp.McpServer.Tests
dotnet sln add tests/AwesomeImapMcp.McpServer.Tests/
dotnet add tests/AwesomeImapMcp.McpServer.Tests/ reference src/AwesomeImapMcp.McpServer/
dotnet add tests/AwesomeImapMcp.McpServer.Tests/ reference src/AwesomeImapMcp.Core/
```

If the project already exists, skip this step.

- [ ] **Step 2: Write tests for LabelVocabularyTools**

Create `tests/AwesomeImapMcp.McpServer.Tests/Tools/LabelVocabularyToolsTests.cs`:

```csharp
using System.Text.Json;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.McpServer.Tools;

namespace AwesomeImapMcp.McpServer.Tests.Tools;

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
        var tools = new LabelVocabularyTools(config);

        var result = Parse(tools.ListLabels());
        Assert.True(result.GetProperty("allow_cli_edits").GetBoolean());
        Assert.Single(result.GetProperty("labels").EnumerateArray());
    }

    [Fact]
    public void AddLabel_Success()
    {
        var config = MakeConfig();
        var tools = new LabelVocabularyTools(config);

        var result = Parse(tools.AddLabel("urgent", "Fast response", "Priority"));
        Assert.Equal("urgent", result.GetProperty("added").GetProperty("Name").GetString());
        Assert.Equal(1, result.GetProperty("total_labels").GetInt32());
        Assert.Single(config.Labels.Items);
    }

    [Fact]
    public void AddLabel_RejectsInvalidName()
    {
        var config = MakeConfig();
        var tools = new LabelVocabularyTools(config);

        var result = Parse(tools.AddLabel("invalid name", "desc", "cat"));
        Assert.True(result.TryGetProperty("error", out _));
        Assert.Empty(config.Labels.Items);
    }

    [Fact]
    public void AddLabel_RejectsDuplicateCaseInsensitive()
    {
        var config = MakeConfig(true,
            new LabelDefinition { Name = "urgent", Description = "Fast", Category = "Priority" });
        var tools = new LabelVocabularyTools(config);

        var result = Parse(tools.AddLabel("URGENT", "desc", "cat"));
        Assert.True(result.TryGetProperty("error", out _));
        Assert.Single(config.Labels.Items);
    }

    [Fact]
    public void AddLabel_RejectsWhenCliEditsDisabled()
    {
        var config = MakeConfig(allowCliEdits: false);
        var tools = new LabelVocabularyTools(config);

        var result = Parse(tools.AddLabel("urgent", "desc", "cat"));
        Assert.Contains("disabled", result.GetProperty("error").GetString());
    }

    [Fact]
    public void UpdateLabel_Success()
    {
        var config = MakeConfig(true,
            new LabelDefinition { Name = "urgent", Description = "Old", Category = "Priority" });
        var tools = new LabelVocabularyTools(config);

        var result = Parse(tools.UpdateLabel("URGENT", description: "New description"));
        Assert.Equal("New description", result.GetProperty("updated").GetProperty("Description").GetString());
        Assert.Equal("New description", config.Labels.Items[0].Description);
    }

    [Fact]
    public void UpdateLabel_NotFound()
    {
        var config = MakeConfig();
        var tools = new LabelVocabularyTools(config);

        var result = Parse(tools.UpdateLabel("nonexistent", description: "desc"));
        Assert.True(result.TryGetProperty("error", out _));
    }

    [Fact]
    public void UpdateLabel_RejectsWhenCliEditsDisabled()
    {
        var config = MakeConfig(allowCliEdits: false,
            new LabelDefinition { Name = "urgent", Description = "Old", Category = "Priority" });
        var tools = new LabelVocabularyTools(config);

        var result = Parse(tools.UpdateLabel("urgent", description: "New"));
        Assert.Contains("disabled", result.GetProperty("error").GetString());
    }

    [Fact]
    public void RemoveLabel_Success()
    {
        var config = MakeConfig(true,
            new LabelDefinition { Name = "urgent", Description = "Fast", Category = "Priority" });
        var tools = new LabelVocabularyTools(config);

        var result = Parse(tools.RemoveLabel("urgent"));
        Assert.Equal("urgent", result.GetProperty("removed").GetString());
        Assert.Equal(0, result.GetProperty("remaining_labels").GetInt32());
        Assert.Empty(config.Labels.Items);
    }

    [Fact]
    public void RemoveLabel_NotFound()
    {
        var config = MakeConfig();
        var tools = new LabelVocabularyTools(config);

        var result = Parse(tools.RemoveLabel("nonexistent"));
        Assert.True(result.TryGetProperty("error", out _));
    }

    [Fact]
    public void RemoveLabel_RejectsWhenCliEditsDisabled()
    {
        var config = MakeConfig(allowCliEdits: false,
            new LabelDefinition { Name = "urgent", Description = "Fast", Category = "Priority" });
        var tools = new LabelVocabularyTools(config);

        var result = Parse(tools.RemoveLabel("urgent"));
        Assert.Contains("disabled", result.GetProperty("error").GetString());
        Assert.Single(config.Labels.Items);
    }
}
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test tests/AwesomeImapMcp.McpServer.Tests/ -v n`
Expected: All 10 tests PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/AwesomeImapMcp.McpServer.Tests/
git commit -m "test: add unit tests for LabelVocabularyTools"
```

---

### Task 5: Vocabulary Warning in `label_messages`

**Files:**
- Modify: `src/AwesomeImapMcp.McpServer/Tools/OrganizeTools.cs:10,140-163`

- [ ] **Step 1: Add `AppConfig` to `OrganizeTools` constructor**

In `src/AwesomeImapMcp.McpServer/Tools/OrganizeTools.cs`, change line 10 from:

```csharp
public class OrganizeTools(QueueManager queueManager)
```

to:

```csharp
public class OrganizeTools(QueueManager queueManager, AppConfig config)
```

Add the field:

```csharp
private readonly AppConfig _config = config;
```

Add the using at the top of the file:

```csharp
using AwesomeImapMcp.Core.Configuration;
```

- [ ] **Step 2: Add vocabulary warning to `LabelMessages`**

Replace the entire `LabelMessages` method including its attribute (lines 140-163) with the following. The key change is adding vocabulary check logic after the enqueue call:

```csharp
[McpServerTool, Description(
    "Add or remove a label from one or more messages. This operation is queued. " +
    "Use list_labels to see the configured vocabulary for consistent labeling.")]
public string LabelMessages(
    [Description("Account ID")] string accountId,
    [Description("Comma-separated list of message UIDs")] string uids,
    [Description("Label name")] string label,
    [Description("Action: \"add\" or \"remove\"")] string action)
{
    try
    {
        var uidList = ParseUids(uids);
        var operationType = action.Equals("remove", StringComparison.OrdinalIgnoreCase)
            ? OperationType.Unlabel
            : OperationType.Label;
        var payload = JsonSerializer.Serialize(new { uids = uidList, label, action });
        var pendingId = _queueManager.EnqueueOperation(accountId, operationType, payload);

        // Advisory vocabulary warning (only on add, only if vocabulary is non-empty)
        string? warning = null;
        if (!action.Equals("remove", StringComparison.OrdinalIgnoreCase)
            && _config.Labels.Items.Count > 0
            && !_config.Labels.Items.Any(l => l.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
        {
            var known = string.Join(", ", _config.Labels.Items.Select(l => l.Name));
            warning = $"Label '{label}' is not in the configured vocabulary. Known labels: {known}";
        }

        return JsonSerializer.Serialize(new { pending_id = pendingId, operation = $"label_{action}", uids = uidList, label, warning }, JsonOptions);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[OrganizeTools.LabelMessages] {ex}");
        return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/AwesomeImapMcp.McpServer/`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/AwesomeImapMcp.McpServer/Tools/OrganizeTools.cs
git commit -m "feat: add vocabulary warning to label_messages tool"
```

---

### Task 6: Dashboard Settings API — Labels Section

**Files:**
- Modify: `src/AwesomeImapMcp.Dashboard/SettingsApi.cs:17-72` (GET), `75-203` (PUT), `210-218` (DTOs)

- [ ] **Step 1: Add labels to GET `/api/settings` response**

In `SettingsApi.cs`, inside the `Results.Ok(new { ... })` block (around line 71, before `accountCount`), add:

```csharp
labels = new
{
    config.Labels.AllowCliEdits,
    config.Labels.Items,
},
```

- [ ] **Step 2: Add `LabelsSettingsUpdate` DTO**

After the existing `MetricsSettingsUpdate` record (around line 271), add:

```csharp
file record LabelsSettingsUpdate
{
    public bool? AllowCliEdits { get; init; }
    public List<LabelDefinition>? Items { get; init; }
}
```

Add `using AwesomeImapMcp.Core.Configuration;` at the top if not already present (it is already imported).

Add to `SettingsUpdateRequest`:

```csharp
public LabelsSettingsUpdate? Labels { get; init; }
```

- [ ] **Step 3: Add labels handling to PUT `/api/settings`**

In the PUT handler, after the Metrics section (around line 173), add:

```csharp
// Labels settings
if (updates.Labels is { } lb)
{
    if (lb.AllowCliEdits is { } ace) { config.Labels.AllowCliEdits = ace; changed.Add("labels.allow_cli_edits"); }
    if (lb.Items is { } items)
    {
        // Validate: unique names, valid IMAP keywords
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
                return Results.BadRequest(new { error = "Label name cannot be empty" });
            if (!System.Text.RegularExpressions.Regex.IsMatch(item.Name, @"^[A-Za-z0-9_$-]+$"))
                return Results.BadRequest(new { error = $"Invalid label name '{item.Name}'. Must contain only letters, digits, hyphens, underscores, or $." });
            if (!names.Add(item.Name))
                return Results.BadRequest(new { error = $"Duplicate label name '{item.Name}'" });
        }
        config.Labels.Items = items;
        changed.Add("labels.items");
    }
}
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/AwesomeImapMcp.Dashboard/`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/AwesomeImapMcp.Dashboard/SettingsApi.cs
git commit -m "feat: expose labels in dashboard settings API"
```

---

### Task 7: Dashboard Frontend — Labels Management UI

**Files:**
- Modify: `dashboard/client/src/pages/Settings.tsx`

- [ ] **Step 1: Add `LabelsCard` component**

Add a new component before the main `Settings` function (around line 890, before the `// Main Settings page` comment). This provides a full CRUD interface for labels:

```tsx
// ---------------------------------------------------------------------------
// Labels Vocabulary card
// ---------------------------------------------------------------------------

function LabelsCard({ settings, onSave, saving }: {
  settings: Record<string, unknown>
  onSave: (section: string, data: Record<string, unknown>) => void
  saving: boolean
}) {
  const labelsData = settings.labels as { allowCliEdits?: boolean; items?: Array<{ name: string; description: string; category: string }> } | undefined
  const [allowCliEdits, setAllowCliEdits] = useState(labelsData?.allowCliEdits ?? true)
  const [items, setItems] = useState<Array<{ name: string; description: string; category: string }>>(labelsData?.items ?? [])
  const [dirty, setDirty] = useState(false)
  const [editIdx, setEditIdx] = useState<number | null>(null)
  const [form, setForm] = useState({ name: '', description: '', category: '' })
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setAllowCliEdits(labelsData?.allowCliEdits ?? true)
    setItems(labelsData?.items ?? [])
    setDirty(false)
  }, [labelsData?.allowCliEdits, labelsData?.items])

  const validateName = (name: string, excludeIdx?: number): string | null => {
    if (!name.trim()) return 'Name is required'
    if (!/^[A-Za-z0-9_$-]+$/.test(name)) return 'Name must contain only letters, digits, hyphens, underscores, or $'
    const dup = items.findIndex((l, i) => i !== excludeIdx && l.name.toLowerCase() === name.toLowerCase())
    if (dup >= 0) return `Label '${name}' already exists`
    return null
  }

  const handleAdd = () => {
    const err = validateName(form.name)
    if (err) { setError(err); return }
    setItems([...items, { name: form.name.trim(), description: form.description.trim(), category: form.category.trim() }])
    setForm({ name: '', description: '', category: '' })
    setError(null)
    setDirty(true)
  }

  const handleUpdate = () => {
    if (editIdx === null) return
    const err = validateName(form.name, editIdx)
    if (err) { setError(err); return }
    const updated = [...items]
    updated[editIdx] = { name: form.name.trim(), description: form.description.trim(), category: form.category.trim() }
    setItems(updated)
    setEditIdx(null)
    setForm({ name: '', description: '', category: '' })
    setError(null)
    setDirty(true)
  }

  const handleRemove = (idx: number) => {
    setItems(items.filter((_, i) => i !== idx))
    if (editIdx === idx) { setEditIdx(null); setForm({ name: '', description: '', category: '' }) }
    setDirty(true)
  }

  const handleEdit = (idx: number) => {
    setEditIdx(idx)
    setForm({ ...items[idx] })
    setError(null)
  }

  const handleCancelEdit = () => {
    setEditIdx(null)
    setForm({ name: '', description: '', category: '' })
    setError(null)
  }

  const handleToggleCliEdits = () => {
    setAllowCliEdits(!allowCliEdits)
    setDirty(true)
  }

  const handleSave = () => {
    onSave('labels', { allowCliEdits, items })
    setDirty(false)
  }

  // Group items by category for display
  const categories = [...new Set(items.map(l => l.category || 'Uncategorized'))]

  return (
    <div className="bg-white rounded-lg shadow p-5">
      <h3 className="text-lg font-medium text-gray-800 mb-2">Label Vocabulary</h3>
      <p className="text-sm text-gray-500 mb-4">
        Define a shared set of labels for consistent use across CLI sessions. Labels are advisory — CLIs can still use any label.
      </p>

      {/* Allow CLI edits toggle */}
      <label className="flex items-center gap-2 mb-4 cursor-pointer">
        <input type="checkbox" checked={allowCliEdits} onChange={handleToggleCliEdits} className="rounded" />
        <span className="text-sm text-gray-700">Allow CLI tools to add/update/remove labels</span>
      </label>

      {/* Label list grouped by category */}
      {items.length > 0 ? (
        <div className="space-y-4 mb-4">
          {categories.map(cat => (
            <div key={cat}>
              <p className="text-xs font-semibold text-gray-500 uppercase tracking-wide mb-2">{cat}</p>
              <div className="space-y-1">
                {items.map((label, idx) => {
                  if ((label.category || 'Uncategorized') !== cat) return null
                  return (
                    <div key={idx} className="flex items-center justify-between py-2 px-3 bg-gray-50 rounded-md">
                      <div className="flex-1 min-w-0">
                        <span className="text-sm font-mono font-medium text-gray-900">{label.name}</span>
                        {label.description && (
                          <span className="text-sm text-gray-500 ml-2">— {label.description}</span>
                        )}
                      </div>
                      <div className="flex gap-1 ml-2">
                        <button
                          onClick={() => handleEdit(idx)}
                          className="px-2 py-1 text-xs text-blue-600 hover:bg-blue-50 rounded transition-colors"
                        >
                          Edit
                        </button>
                        <button
                          onClick={() => handleRemove(idx)}
                          className="px-2 py-1 text-xs text-red-600 hover:bg-red-50 rounded transition-colors"
                        >
                          Remove
                        </button>
                      </div>
                    </div>
                  )
                })}
              </div>
            </div>
          ))}
        </div>
      ) : (
        <p className="text-sm text-gray-400 mb-4">No labels configured.</p>
      )}

      {/* Add / Edit form */}
      <div className="border border-gray-200 rounded-lg p-3 mb-4">
        <p className="text-sm font-medium text-gray-700 mb-2">
          {editIdx !== null ? `Edit label: ${items[editIdx]?.name}` : 'Add a new label'}
        </p>
        <div className="grid grid-cols-3 gap-2 mb-2">
          <input
            type="text"
            placeholder="Name"
            value={form.name}
            onChange={e => { setForm({ ...form, name: e.target.value }); setError(null) }}
            disabled={editIdx !== null}
            className="border border-gray-300 rounded-md px-3 py-1.5 text-sm disabled:bg-gray-100"
          />
          <input
            type="text"
            placeholder="Description"
            value={form.description}
            onChange={e => setForm({ ...form, description: e.target.value })}
            className="border border-gray-300 rounded-md px-3 py-1.5 text-sm"
          />
          <input
            type="text"
            placeholder="Category"
            value={form.category}
            onChange={e => setForm({ ...form, category: e.target.value })}
            className="border border-gray-300 rounded-md px-3 py-1.5 text-sm"
          />
        </div>
        {error && <p className="text-xs text-red-600 mb-2">{error}</p>}
        <div className="flex gap-2">
          {editIdx !== null ? (
            <>
              <button onClick={handleUpdate} className="px-3 py-1.5 bg-blue-600 text-white text-sm rounded-md hover:bg-blue-700 transition-colors">
                Update
              </button>
              <button onClick={handleCancelEdit} className="px-3 py-1.5 text-sm text-gray-600 hover:bg-gray-100 rounded-md transition-colors">
                Cancel
              </button>
            </>
          ) : (
            <button onClick={handleAdd} className="px-3 py-1.5 bg-blue-600 text-white text-sm rounded-md hover:bg-blue-700 transition-colors">
              Add Label
            </button>
          )}
        </div>
      </div>

      {/* Save button */}
      {dirty && (
        <button
          onClick={handleSave}
          disabled={saving}
          className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors disabled:opacity-50"
        >
          {saving ? 'Saving...' : 'Save Labels'}
        </button>
      )}
    </div>
  )
}
```

- [ ] **Step 2: Add `LabelsCard` to the Settings page render**

In the `Settings` component's render, add the `LabelsCard` inside the `{settings && (...)}` block, before the `Object.entries(settings)` map (around line 986). Add it after the `<CacheManagementCard />` and before `{settings && (`:

```tsx
{/* Labels vocabulary card */}
{settings && (
  <LabelsCard settings={settings as Record<string, unknown>} onSave={handleSave} saving={updateSettings.isPending} />
)}
```

Also, filter out the `labels` section from the generic `Object.entries` map to avoid rendering it twice. In the map callback (line 987), add a check at the top:

```tsx
if (section === 'labels') return null
```

This goes right after the existing `if (typeof values !== 'object' || values === null)` check.

- [ ] **Step 3: Build the frontend**

Run: `cd dashboard/client && npm run build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add dashboard/client/src/pages/Settings.tsx
git commit -m "feat: add Labels management UI to dashboard settings"
```

---

### Task 8: Full Integration Test

**Files:** No new files — this is a verification step.

- [ ] **Step 1: Run full backend test suite**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 2: Run full frontend build**

Run: `cd dashboard/client && npm run build`
Expected: Build succeeded.

- [ ] **Step 3: Run full backend build**

Run: `dotnet build`
Expected: Build succeeded.
