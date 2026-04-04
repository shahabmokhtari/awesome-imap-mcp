# CLAUDE.md

## Critical Rules

- ALWAYS THINK TWICE AND VERIFY BEFORE MAKING DECISIONS OR MAKING CHANGES.
- NEVER REMOVE A FEATURE OR OPTION WITHOUT EXPLICIT CONFIRMATION OF THE USER.
- Test changes before claiming they work. Build and run tests.
- When fixing bugs, verify the root cause before applying fixes.
- NEVER push directly to main/master. Always create a feature branch, push it, and create a PR. Merge via PR only.

## Project

.NET 10 / C# 13 MCP server for IMAP email with dashboard (React/TypeScript).

## Build & Test

```bash
dotnet build          # Build all projects
dotnet test           # Run all 278+ tests
cd dashboard/client && npm run build  # Build frontend
```

## Key Architecture

- `src/UltimateImapMcp.McpServer/` — MCP server entry point + tools
- `src/UltimateImapMcp.Dashboard/` — ASP.NET dashboard (REST + SignalR)
- `src/UltimateImapMcp.ImapClient/` — IMAP sync, repositories
- `src/UltimateImapMcp.Llm/` — LLM analysis (API, ACP pool)
- `src/UltimateImapMcp.Core/` — Config, DB, coordination
- `dashboard/client/` — React SPA (Vite + TailwindCSS)

## Conventions

- All dashboard API responses use camelCase (via `ConfigureHttpJsonOptions`)
- Error responses: `{ error: "message" }` with appropriate HTTP status
- SQLite with WAL mode, parameterized queries only
- ACP providers use `claude-code-acp` (npx) for Claude, `gh copilot --acp` for Copilot
- Messages are deduplicated across folders via `message_folders` junction table
- Sync boundaries derived from actual DB data (`GetMaxUid`/`GetMinUid`), not stored columns
