---
name: email-analysis
description: Analyze, categorize, label, and report on emails from all IMAP accounts via MCP tools. Use this skill whenever the user asks to analyze emails, generate an email report, categorize their inbox, label emails, check what's new in their mail, triage their inbox, or review recent messages. Also trigger when they say things like "check my email", "what's in my inbox", "process my emails", "email summary", "inbox report", "label my emails", or "what happened in my email this week/month". Works with the awesome-imap-mcp MCP server.
---

# Email Analysis & Report

Analyze emails across all IMAP accounts, categorize them, apply IMAP keyword labels, and generate an actionable report split by account with an aggregated summary.

## Prerequisites

- The `awesome-imap-mcp` MCP server must be connected
- Use `mcp__awesome-imap-mcp__*` tools throughout

## Agent Architecture

**The main agent is a coordinator only. All email fetching, analysis, and labeling is done by subagents.**

### Model Selection
- **Main agent (orchestrator)**: Use Opus (most capable) for coordination, report generation, and decisions
- **Subagents (email analysis)**: Use Sonnet (`model: "sonnet"`) for email fetching, categorization, and labeling

Main agent responsibilities:
1. Determine scope (date range or count)
2. Discover accounts and fetch the complete list of email IDs per account/week
3. Dispatch subagents with specific batches of IDs
4. Monitor subagent completion and track overall progress
5. Assemble the final report from subagent results

Subagent responsibilities:
1. Fetch email bodies via `get_message` for every assigned email
2. Categorize each email
3. Apply IMAP labels
4. Mark emails with `awesome-imap-mcp` flag
5. Return a structured summary including any failed IDs

**If a subagent's context window fills up**, close it and start a fresh one with the remaining unprocessed IDs. Never compact — close and restart.

**If body fetch fails for some IDs**, the subagent must NOT categorize from snippets alone, skip labeling, and report the failed IDs. The main agent should report failures to the user and ask what to do.

---

## Weekly Processing Loop

**Always start with the most recent week and go backward.** Process one week at a time — complete ALL accounts for that week before moving to the next older week.

After each week completes:
1. Generate/update the running report
2. Update the handover document (for session resumption)
3. Update the HTML report (cumulative, all completed weeks)
4. Move to the next older week

### Handover Document

Save to `~/.awesome-imap-mcp/email-report-handover.md` after each week for session resumption:

```markdown
# Email Report — Handover Document
Last updated: <ISO timestamp>
Scope: <date range>
Overall progress: <N> of <total weeks> weeks complete

## Completed Weeks
| Week | Date Range | Accounts | Emails |

## Remaining Weeks
- W7: Feb 11–18 — Account1(108), Account2(10)

## Running Report
<current report markdown>
```

---

## Workflow

### Phase 0: Scope — Ask the User

Ask ONE question to determine scope:

> **How would you like to scope the analysis?**
> - **By count**: most recent N emails (e.g., 100, 500)
> - **By time**: a date range (e.g., "last week", "March 2026", "last 6 months")
> Default: most recent 100 emails

For large time ranges (> 2 weeks), process week by week starting from the most recent.

### Phase 1: Discovery

1. `list_accounts` — skip disabled accounts
2. `list_labels` — get current label vocabulary
3. `list_folders` — determine which folders to search (INBOX or all)

### Phase 2: Collect All Email IDs

For each account and week, collect all email DB IDs via `search_emails`.

#### Pagination
Always paginate until exhausted:
```
offset = 0, all_ids = []
loop:
  results = search_emails(fromDate, toDate, maxResults=100, offset=offset)
  all_ids += results.ids
  if results.count < 100: break
  offset += 100
```

#### Server Search
After cache pagination, check `cache_info.backfill_complete`:
- `true` → cache has all emails, done
- `false` → run `search_emails` with `serverSearch=true` and merge results

For **Gmail**: use `folder=[Gmail]/All Mail`. For all others: use `folder=INBOX`.

### Phase 3: Dispatch Subagents

Split IDs into batches of 40–50 per subagent. Launch multiple simultaneously.

#### Subagent Prompt Template

```
You are processing IMAP emails. Complete all steps without asking questions.

Account ID: [id]
DB message IDs to process: [comma-separated list]

## Step 1: Fetch Bodies
Call get_message for EVERY ID. Record: uid, from, subject, body_text.

## Step 2: Categorize
Assign labels per email:
- marketing-promo — newsletters, sales, promotions
- finance — banking, payments, statements
- dev-tools — GitHub, cloud, API services, developer content
- personal — real people, direct correspondence
- housing — lease, rent, property
- healthcare — medical, pharmacy, insurance
- transactional — receipts, confirmations, deliveries
- travel — airlines, hotels, rideshare
- government — official documents, regulatory
- action-needed — requires response (payment due, deadline)
- spam-likely — aggressive marketing
- spam-bad — unsolicited, deceptive marketing
- spam-dangerous — phishing, scams, impersonation

Also label by language: lang-en, lang-fr, etc.

## Step 3: Apply Labels
Group UIDs by label. Call label_messages per label (max 50 UIDs per call).

## Step 4: Mark Processed
Apply label "awesome-imap-mcp" to all processed UIDs.

## Step 5: Extract Unsubscribe URLs
For each unique sender categorized as marketing/spam:
- Check ONE email per sender for unsubscribe links
- Record: sender_email, unsubscribe_url

## Step 6: Report
Return: bodies fetched, labels applied, language distribution,
spam findings, action items, unsubscribe URLs, errors.
```

### Phase 4: Label Setup

Before dispatching subagents, ensure label vocabulary is complete via `list_labels` and `add_label`.

### Phase 5: Report

Generate a comprehensive report with:

```markdown
# Email Report — [date range]

## Summary
| Account | Email | Emails | Date Range |

## Account: [Name] — N emails

### Category Breakdown
| Label | Count | % |

### Top Senders
| Sender | Count | Type |

### Spam Analysis
| Classification | Count | Examples |

### Action Items
| Item | Urgency | Details |

## Aggregated Summary

### Combined Category Totals
| Category | Account1 | Account2 | Total |

### Inbox Health Score: X/100
| Factor | Score | Notes |
| Signal-to-noise | X/10 | |
| Marketing clutter | X/10 | |
| Spam penetration | X/10 | |
| Dangerous spam | X/10 | |
| Subscription hygiene | X/10 | |

### Security Alerts
(If spam-dangerous found.)

## Recommendations
### Priority 1 — Immediate (bills, security, dangerous spam)
### Priority 2 — Unsubscribe (with URLs)
### Priority 3 — Set Up Filters
### Priority 4 — Housekeeping
```

### HTML Report

Update `~/.awesome-imap-mcp/email-report.html` after each week:
- Single-file HTML with embedded CSS
- Collapsible sections per account and category
- Highlight dangerous and action items prominently

### Phase 6: Suggested Actions

Present executable actions:
1. Unsubscribe from senders (auto via browser or list URLs)
2. Delete marketing emails
3. Delete spam
4. Archive transactional emails
5. Move bills to Bills folder
6. Block senders with no unsubscribe links

Execute with `delete_messages`, `move_messages`, `label_messages`, `create_folder`.

---

## Important Rules

- **Always paginate fully**: Stop only when `count < 100`
- **Always server-search when backfill incomplete**
- **Always fetch bodies**: Call `get_message` for every email — never categorize from snippets alone
- **Batch labeling**: Max 50 UIDs per `label_messages` call
- **Multi-label**: Apply all relevant labels per email
- **Account ID**: Use `id` from `list_accounts`, not the name
- **Disabled accounts**: Skip entirely
- **MCP tools only**: Never access databases or files directly
