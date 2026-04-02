---
name: email-analysis
description: Analyze, categorize, and report on emails using LLM analysis and statistical tools. Use when asked to analyze emails, generate reports, categorize inbox, triage, or understand email patterns.
---

# Email Analysis

## Available Tools

### Analysis (requires LLM)
- `analyze_email` — Analyze a single email (types: summary, spam_score, category, priority, custom)
- `analyze_folder` — Batch analyze emails in a folder
- `get_analysis` — Retrieve cached analysis results
- `get_analysis_budget` — Check LLM token/cost budget

### Reports (no LLM needed)
- `mailbox_report` — Volume stats, folder breakdown, size analysis
- `top_senders` — Most frequent senders over a period
- `category_breakdown` — Distribution of email categories

### Labeling
- `label_messages` — Apply labels to messages (action: "add" or "remove")
- `list_labels` — Show configured label vocabulary
- `add_label` / `update_label` / `remove_label` — Manage label vocabulary

## Workflow

1. **List accounts**: `list_accounts` to see available accounts
2. **Check budget**: `get_analysis_budget` before batch analysis
3. **Analyze**: Use `analyze_folder` for batch, `analyze_email` for individual
4. **Label**: Apply labels based on analysis results using `label_messages`
5. **Report**: Use `mailbox_report` and `top_senders` for summaries

## Tips
- Analysis results are cached — `get_analysis` retrieves without re-running LLM
- Labels are IMAP keywords on supported servers, local DB on others (Yahoo, etc.)
- Budget tracking is per-day and per-month
- Use `category_breakdown` after labeling to verify distribution
