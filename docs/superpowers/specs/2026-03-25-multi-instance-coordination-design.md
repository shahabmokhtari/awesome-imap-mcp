# Multi-Instance Coordination Design

## Problem

Multiple MCP server instances can run simultaneously (e.g., one per IDE window, each using stdio transport, plus one with the dashboard). Currently:
- All instances run all periodic services (sync, queue, cache eviction, etc.), causing duplicate work and potential conflicts
- The dashboard has no visibility into other running instances
- No way to shut down a specific instance from the dashboard
- Logs page shows all historical logs, not just live sessions

## Solution

A hybrid coordination system using a separate SQLite database for health/heartbeats, with leader election that determines which instance runs periodic operations.

## Architecture

### Databases

Two separate SQLite databases, both in `~/.ultimate-imap-mcp/`:

| Database | Purpose | Writers |
|----------|---------|---------|
| `cache.db` | Main app data (messages, accounts, logs, etc.) | Leader instance primarily |
| `health.db` | Instance heartbeats only | All instances (every 10s) |

Separating health from the main DB avoids write contention between non-leader heartbeat writes and leader data operations.

### Health Database

**New class: `HealthDatabase`**
- Minimal SQLite wrapper (WAL mode + `busy_timeout=5000`)
- Manages a single connection for writes, fresh connections for reads
- Same pattern as `AppDatabase` but lightweight

**Table: `instance_heartbeats`**

```sql
CREATE TABLE IF NOT EXISTS instance_heartbeats (
    instance_id        TEXT PRIMARY KEY,
    process_id         INTEGER NOT NULL,
    cwd                TEXT NOT NULL,
    transport          TEXT NOT NULL,
    is_dashboard_host  INTEGER NOT NULL DEFAULT 0,
    is_leader          INTEGER NOT NULL DEFAULT 0,
    started_at         TEXT NOT NULL,
    last_heartbeat     TEXT NOT NULL,
    accounts_count     INTEGER NOT NULL DEFAULT 0,
    cpu_time_ms        INTEGER NOT NULL DEFAULT 0,
    memory_mb          INTEGER NOT NULL DEFAULT 0,
    shutdown_requested INTEGER NOT NULL DEFAULT 0
);
```

### InstanceCoordinator Service

**New class: `InstanceCoordinator` (BackgroundService)**

Implements `IInstanceCoordinator`:

```csharp
public interface IInstanceCoordinator
{
    bool IsLeader { get; }
    string InstanceId { get; }
    IReadOnlyList<InstanceHeartbeat> GetLiveInstances();
    void RequestShutdown(string instanceId);
}
```

**Heartbeat loop (every `heartbeat_interval` seconds, default 10):**

1. Collect process metrics (PID, CWD, CPU time, memory, account count)
2. Upsert own row in `instance_heartbeats`
3. Read all rows from table
4. Compute leader:
   - Among instances with fresh heartbeats (last_heartbeat > now - staleThreshold):
     - If any instance has `is_dashboard_host = 1`, that instance is leader
     - Otherwise, instance with the earliest `started_at` is leader
5. Update own `is_leader` flag in the table
6. **If this instance is the leader:** prune rows where `last_heartbeat < now - staleThreshold`
7. Check own `shutdown_requested` flag — if `1`, call `IHostApplicationLifetime.StopApplication()`

**Stale threshold:** `heartbeat_interval * heartbeat_stale_after` (default: 10 * 5 = 50 seconds).

**On graceful shutdown:** `StopAsync()` deletes own heartbeat row from the table.

**On crash:** Heartbeat stops updating. After 50 seconds, the leader prunes the stale row and a new leader is elected.

### Leader-Gated Services

Each periodic BackgroundService checks `IInstanceCoordinator.IsLeader` before executing work:

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    if (!_coordinator.IsLeader)
    {
        await Task.Delay(TimeSpan.FromSeconds(_config.Server.HeartbeatInterval), stoppingToken);
        continue;
    }
    // ... existing work
}
```

**Gated services (6):**
- `SyncManager` — IMAP sync + IDLE listeners
- `QueueWorker` — send/delete/move/flag operations
- `CacheEvictor` — size/age-based cache eviction
- `MetricsCollector` — system metrics sampling
- `OAuthTokenRefreshService` — proactive OAuth token refresh
- `ZohoSyncService` — Zoho REST API polling

**Not gated (instance-specific):**
- `DashboardHost` — only runs when dashboard_enabled, handles own port standby
- `HttpMcpTransportHost` — only runs for http/both transport, handles own port standby
- `DashboardHubRelay` — only relevant when dashboard is active
- `InstanceCoordinator` — must always run in every instance

### Configuration

New fields in `ServerConfig`:

```json
{
  "server": {
    "heartbeat_interval": 10,
    "heartbeat_stale_after": 5
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `heartbeat_interval` | int | 10 | Seconds between heartbeat writes |
| `heartbeat_stale_after` | int | 5 | Number of missed heartbeats before instance is considered dead |

### Dashboard API Changes

**`GET /api/server/instances` (updated)**
- Returns live instances from `HealthDatabase` heartbeat table instead of log-based discovery
- Response: `{ current: string, instances: InstanceHeartbeat[] }`

**`POST /api/server/instances/{instanceId}/shutdown` (new)**
- Sets `shutdown_requested = 1` for the target instance in the heartbeat table
- Returns 404 if instance not found or already stale
- Cannot shutdown the dashboard's own instance (use existing shutdown endpoint for that)

**`GET /api/logs` (updated)**
- New query parameter: `live_only=true`
- When set, fetches live instance IDs from `HealthDatabase` and adds `AND instance_id IN (...)` to the SQL query
- Default behavior unchanged when parameter is absent

### Dashboard UI Changes

**Logs page:**
- Add "Live instances" as the default option in the instance filter dropdown
- Dropdown options: "Live instances" (default) | "All instances" | individual instance IDs
- "Live instances" passes `live_only=true` to the API

**Settings > Server Controls card:**
- Replace single-instance info with a table of all live instances
- Columns: Instance ID (truncated), PID, CWD (truncated), Transport, Role (Leader/Dashboard badges), Uptime, CPU, Memory
- Each row has a "Shutdown" button
- Dashboard's own row: disabled shutdown button (use the existing "Shutdown Server" button below the table)
- Auto-refreshes every 10 seconds (matches heartbeat interval)

### Startup Flow

1. `HealthDatabase` is created/opened at `~/.ultimate-imap-mcp/health.db`
2. `InstanceCoordinator` is registered as a singleton + hosted service
3. On first heartbeat cycle, instance writes its row and computes leader
4. If leader, gated services begin executing work immediately
5. If not leader, gated services idle-loop until leadership changes

### Shutdown Flows

**Graceful shutdown (own process):**
1. `InstanceCoordinator.StopAsync()` deletes own heartbeat row
2. If this was the leader, remaining instances elect a new leader on their next heartbeat cycle

**Remote shutdown (via dashboard):**
1. Dashboard sets `shutdown_requested = 1` on target instance's row
2. Target instance detects the flag on next heartbeat cycle (max 10s)
3. Target instance calls `StopApplication()` and deletes its row

**Crash:**
1. Instance stops updating heartbeat
2. After 50 seconds, leader's heartbeat cycle prunes the stale row
3. If crashed instance was leader, a new leader is elected

### File Structure

New files:
- `src/UltimateImapMcp.Core/Database/HealthDatabase.cs`
- `src/UltimateImapMcp.Core/Coordination/IInstanceCoordinator.cs`
- `src/UltimateImapMcp.Core/Coordination/InstanceCoordinator.cs`
- `src/UltimateImapMcp.Core/Coordination/InstanceHeartbeat.cs`

Modified files:
- `src/UltimateImapMcp.Core/Configuration/AppConfig.cs` — add heartbeat config fields
- `src/UltimateImapMcp.McpServer/Program.cs` — register HealthDatabase + InstanceCoordinator
- `src/UltimateImapMcp.Dashboard/ServerApi.cs` — update instances endpoint, add remote shutdown
- `src/UltimateImapMcp.Dashboard/DashboardHost.cs` — forward HealthDatabase + IInstanceCoordinator to dashboard DI
- `src/UltimateImapMcp.Dashboard/LogsApi.cs` — add live_only parameter
- `src/UltimateImapMcp.ImapClient/SyncManager.cs` — add leader gate
- `src/UltimateImapMcp.Queue/QueueWorker.cs` — add leader gate
- `src/UltimateImapMcp.Core/Repositories/CacheEvictor.cs` — add leader gate
- `src/UltimateImapMcp.Core/Repositories/MetricsCollector.cs` — add leader gate
- `src/UltimateImapMcp.Core/OAuth/OAuthTokenRefreshService.cs` — add leader gate
- `src/UltimateImapMcp.ImapClient/ZohoSyncService.cs` — add leader gate
- `dashboard/client/src/pages/Logs.tsx` — live instances default filter
- `dashboard/client/src/pages/Settings.tsx` — multi-instance server controls table
- `dashboard/client/src/hooks/useApi.ts` — update hooks
