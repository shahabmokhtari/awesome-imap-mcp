# Contributing to Ultimate IMAP MCP

Thanks for your interest in contributing! This project is built with .NET 10 / C# 13 (server) and React + TypeScript (dashboard).

## Quick Start

```bash
git clone https://github.com/shahabmokhtari/ultimate-imap-mcp.git
cd ultimate-imap-mcp
dotnet build
dotnet test                                # 278+ tests
cd dashboard/client && npm install && npm run build
```

## Development Workflow

1. **Open an issue first** for non-trivial changes so we can discuss design.
2. **Branch from `main`** with a descriptive name: `feat/...`, `fix/...`, `chore/...`, `docs/...`.
3. **Build and run tests locally**:
   ```bash
   dotnet build
   dotnet test
   cd dashboard/client && npm run build
   ```
4. **Open a pull request** against `main`. CI must pass (build + tests on Ubuntu + frontend build).
5. **Squash-merge** is preferred to keep `main` history linear.

## Code Standards

- **C#**: Follow standard .NET conventions. Nullable reference types are enabled. Prefer expression-bodied members for simple cases.
- **TypeScript**: Strict mode is on. Avoid `any`; use proper types or `unknown`.
- **API responses**: Dashboard JSON uses `camelCase` (configured globally via `ConfigureHttpJsonOptions`).
- **Errors**: Return `{ error: "message" }` with appropriate HTTP status from dashboard endpoints.
- **SQLite**: Always use parameterized queries. WAL mode is required.
- **Tests**: Add tests for new logic. Integration tests use real SQLite (`InMemory` is not enough).

## Architecture Layout

- `src/UltimateImapMcp.McpServer/` — MCP server entry point + tool definitions
- `src/UltimateImapMcp.Dashboard/` — ASP.NET dashboard (REST + SignalR)
- `src/UltimateImapMcp.ImapClient/` — IMAP sync, repositories
- `src/UltimateImapMcp.Llm/` — LLM analysis (API + ACP pool)
- `src/UltimateImapMcp.Core/` — Config, DB, coordination
- `src/UltimateImapMcp.Queue/` — Operation queue with priority tiers
- `dashboard/client/` — React SPA (Vite + TailwindCSS)
- `tests/` — xUnit test projects mirroring the layout above
- `docs/` — User and developer documentation

## Conventions

- **Sync boundaries** are derived from actual DB data (`GetMaxUid` / `GetMinUid`), not stored columns.
- **Messages are deduplicated** across folders via the `message_folders` junction table.
- **ACP providers** use `claude-code-acp` (npx) for Claude and `gh copilot --acp` for Copilot.
- **Bulk operations** chunk UIDs into batches of 50 to avoid IMAP server timeouts.

## Reporting Bugs

Open an issue with:

- The version / commit
- Provider (Gmail, Outlook, Yahoo, Zoho, etc.)
- Steps to reproduce
- Relevant logs from `~/.ultimate-imap-mcp/logs/` (redact account IDs and message contents)

For security issues, see [SECURITY.md](SECURITY.md).

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
