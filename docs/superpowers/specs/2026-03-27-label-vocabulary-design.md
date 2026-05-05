# Label Vocabulary System — Design Spec

## Problem

MCP CLI clients (Claude, Copilot, etc.) apply ad-hoc label strings to messages via the `label_messages` tool. Without a shared vocabulary, different sessions use different labels for the same concept (e.g., "urgent" vs "high-priority" vs "important"), making cross-session search and filtering unreliable.

## Solution

A configurable, global label vocabulary stored in `config.json`. CLIs can read the vocabulary to use consistent labels, and optionally manage it via MCP tools. The vocabulary is advisory — CLIs can still use any label string, but get a warning when applying a label not in the vocabulary.

## Decisions

- **Global, not per-account** — labels represent concepts (e.g., "urgent"), not account-specific state.
- **Config file is source of truth** — no database tables. Dashboard edits the config file directly.
- **Advisory, not enforcing** — `label_messages` accepts any label but warns on unknown ones.
- **CLI edits toggleable** — `allow_cli_edits` setting lets dashboard admins lock down the vocabulary to read-only for MCP clients.

## Config Schema

New top-level `labels` section in `config.json`:

```json
{
  "labels": {
    "allow_cli_edits": true,
    "items": [
      {
        "name": "urgent",
        "description": "Needs response within 24 hours",
        "category": "Priority"
      },
      {
        "name": "follow-up",
        "description": "Requires follow-up action later",
        "category": "Status"
      }
    ]
  }
}
```

### Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `allow_cli_edits` | `bool` | `true` | Whether MCP tools can add/update/remove labels |
| `items` | `LabelDefinition[]` | `[]` | The label definitions |
| `items[].name` | `string` | required | The IMAP keyword string used when labeling messages |
| `items[].description` | `string` | `""` | Short explanation of when/why to use this label |
| `items[].category` | `string` | `""` | Grouping for display (e.g., "Priority", "Status", "Topic") |

### Validation Rules

- `name` must be non-empty and unique (case-insensitive) within the list.
- `name` must be a valid IMAP keyword per RFC 3501 atom syntax: alphanumeric, hyphens, underscores, and `$` only (regex: `^[A-Za-z0-9_$-]+$`). No spaces, parentheses, quotes, backslashes, or control characters.

## C# Config Model

In `AppConfig.cs`:

```csharp
public class LabelsConfig
{
    [JsonPropertyName("allow_cli_edits")]
    public bool AllowCliEdits { get; set; } = true;

    [JsonPropertyName("items")]
    public List<LabelDefinition> Items { get; set; } = [];
}

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

`AppConfig` gains:

```csharp
[JsonPropertyName("labels")]
public LabelsConfig Labels { get; set; } = new();
```

## MCP Tools

New tool class: `LabelVocabularyTools` in `src/AwesomeImapMcp.McpServer/Tools/`. Receives `AppConfig` via constructor injection: `LabelVocabularyTools(AppConfig config)`.

### `list_labels`

- **Parameters:** none
- **Returns:** `{ allow_cli_edits: bool, labels: [{ name, description, category }] }`
- **Always available** regardless of `allow_cli_edits`.

### `add_label`

- **Parameters:** `name` (required), `description` (required), `category` (optional, defaults to `""`)
- **Behavior:**
  - If `allow_cli_edits` is false, return error: `"Label vocabulary editing is disabled by the administrator"`
  - If a label with the same name (case-insensitive) already exists, return error
  - Validate name is a valid IMAP keyword
  - Add to `config.Labels.Items`, persist via `ConfigLoader.SaveToFile()`
- **Returns:** `{ added: { name, description, category }, total_labels: int }`

### `update_label`

- **Parameters:** `name` (required — identifies the label to update), `description` (optional), `category` (optional)
- **Behavior:**
  - If `allow_cli_edits` is false, return error
  - If label not found (case-insensitive), return error
  - Update provided fields, persist
- **Returns:** `{ updated: { name, description, category } }`
- **Note:** Label names are immutable. To rename a label, remove the old one and add a new one.

### `remove_label`

- **Parameters:** `name` (required)
- **Behavior:**
  - If `allow_cli_edits` is false, return error
  - If label not found (case-insensitive), return error
  - Remove from list, persist
- **Returns:** `{ removed: name, remaining_labels: int }`
- **Note:** Removing a label definition does NOT remove the IMAP keyword from messages that already have it.

## Warning in `label_messages`

Modify `OrganizeTools.LabelMessages` to check the vocabulary when `action` is `"add"`. This requires adding `AppConfig` to the `OrganizeTools` constructor: `OrganizeTools(QueueManager queueManager, AppConfig config)`.

1. Look up the label name (case-insensitive) in `config.Labels.Items`.
2. If not found and the vocabulary is non-empty, add a `warning` field to the response:

```json
{
  "pending_id": "abc123",
  "operation": "label_add",
  "uids": [1, 2, 3],
  "label": "urgnt",
  "warning": "Label 'urgnt' is not in the configured vocabulary. Known labels: urgent, follow-up, review-needed"
}
```

If the vocabulary is empty (no labels configured), no warning is issued. No warning is issued when `action` is `"remove"`, even if the label is not in the vocabulary.

## Dashboard Integration

### GET `/api/settings`

Add to the response:

```json
{
  "labels": {
    "allowCliEdits": true,
    "items": [
      { "name": "urgent", "description": "Needs response within 24 hours", "category": "Priority" }
    ]
  }
}
```

### PUT `/api/settings`

Accept a `labels` section in the update request:

```json
{
  "labels": {
    "allowCliEdits": false,
    "items": [...]
  }
}
```

When `items` is provided, it replaces the entire list (not a partial merge — labels are managed as a set). The endpoint must validate incoming items: reject duplicate names (case-insensitive) and invalid IMAP keyword names, returning 400 with an error message. When only `allowCliEdits` is provided, only that flag changes.

DTO addition to `SettingsApi.cs`:

```csharp
file record LabelsSettingsUpdate
{
    public bool? AllowCliEdits { get; init; }
    public List<LabelDefinition>? Items { get; init; }
}
```

Add `public LabelsSettingsUpdate? Labels { get; init; }` to `SettingsUpdateRequest`.

### Frontend

Add a "Labels" section to the Settings page:
- Toggle for "Allow CLI edits"
- Table of labels with name, description, category columns
- Add/edit/remove buttons
- Group by category in the display

## `ConfigLoader.SaveToFile` Update

In the `SaveToFile` method, add `Labels = config.Labels,` to the `new AppConfig { ... }` initializer block, after the `OAuthProviders` line. No sensitive fields to strip.

## Files Changed

| File | Change |
|------|--------|
| `src/AwesomeImapMcp.Core/Configuration/AppConfig.cs` | Add `LabelsConfig`, `LabelDefinition`, `Labels` property |
| `src/AwesomeImapMcp.Core/Configuration/ConfigLoader.cs` | Include `Labels` in `SaveToFile` |
| `src/AwesomeImapMcp.McpServer/Tools/LabelVocabularyTools.cs` | New file — 4 MCP tools |
| `src/AwesomeImapMcp.McpServer/Tools/OrganizeTools.cs` | Add vocabulary warning to `LabelMessages` |
| `src/AwesomeImapMcp.Dashboard/SettingsApi.cs` | Expose labels in GET/PUT |
| `dashboard/client/` | Labels section in Settings page |

## Out of Scope

- **`LabelExecutor`** — the existing `label_messages` tool queues Label/Unlabel operations but no executor processes them. This is a pre-existing gap, not part of this feature.
- **Label colors** — can be added later if needed for dashboard display.
- **Per-account labels** — not needed; vocabulary is global.
