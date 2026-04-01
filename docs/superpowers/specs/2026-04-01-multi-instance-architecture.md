# Multi-Instance Architecture + Batch Body Fetch + MCP Tooling Improvements

## Status: Pending Implementation

## 1. Primary/Secondary Instance Model

### Requirements
- Only ONE instance processes emails, syncs, and runs queue operations (the "primary")
- All other instances are proxies that forward MCP tool calls to the primary via HTTP
- If the primary goes down, another instance takes over automatically (leader election)
- The HTTP port is configurable via config (`server.http_port`, default 3846)
- Dashboard is optional — headless mode must be supported
- Concurrency: primary must handle many parallel requests from multiple secondary instances without perf degradation

### Architecture
- On startup, check if configured HTTP port is in use
- If available → **Primary mode**: bind HTTP port, start sync, queue, dashboard (if enabled)
- If in use → **Secondary mode**: no sync, no queue, no dashboard. MCP tools proxy to primary
- Leader failover: secondary instances monitor primary via heartbeat. If primary dies, one secondary takes over (bind HTTP port, start sync/queue)
- All MCP tool execution goes through HTTP API (`/api/tools/{name}/execute`) which already exists

### Proxy Implementation
- Create `ProxyToolExecutor` using `HttpClient` to call primary's HTTP API
- Each MCP tool checks mode: primary → execute locally, secondary → proxy to primary
- Response JSON passed back directly to MCP client
- HttpClient should use connection pooling, retries, and timeouts

### Concurrency
- Primary's HTTP API runs on Kestrel which handles concurrent requests natively
- Database writes are serialized via existing `ExecuteWrite` semaphore
- Read operations are lock-free (separate read connections)
- Tool execution should be async and non-blocking

## 2. New MCP Tools for Account Management

### `start_dashboard`
- Starts the dashboard if not already running
- Returns the dashboard URL
- If dashboard is already running, just returns the URL

### `add_account_imap`
- Adds a new IMAP account via CLI parameters
- Params: name, imap_host, imap_port, smtp_host, smtp_port, username, password, provider
- Writes to accounts.json, triggers sync

### `add_account_oauth`
- Starts OAuth flow for adding an account
- Opens dashboard in browser at the add-account page with OAuth pre-selected
- Params: provider (gmail, outlook, yahoo, zoho)
- Returns status/instructions

## 3. Batch Body Fetch + Search with Bodies

### `fetch_bodies` tool
- Takes accountId + list of message IDs or UIDs
- Fetches all bodies in one IMAP session (batch operation)
- Caches all fetched bodies via existing UpdateBody path
- Returns count of successfully fetched bodies

### Update `search_emails` tool
- Add `fetchBodies` parameter (default: false)
- When true, auto-fetch bodies for all search results before returning
- Bodies are cached for future access

## 4. MCP Tooling Improvements

### Research Task
- Survey open-source email MCP servers for feature ideas
- Propose new tools based on findings
- Get user approval before implementing

### Candidate areas (to research)
- Email composition drafts
- Calendar/meeting detection from emails
- Contact extraction
- Email threading improvements
- Bulk operations (mark all as read, bulk label, bulk move)
- Smart search (natural language → IMAP query)
- Email templates
- Unsubscribe detection
