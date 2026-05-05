# Multi-Instance Coordination Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable multiple MCP server instances to coordinate via a shared SQLite health database, with leader election determining which instance runs periodic operations, and dashboard UI for managing all live instances.

**Architecture:** A separate `health.db` SQLite database stores heartbeat rows from all instances. An `InstanceCoordinator` BackgroundService in each instance writes heartbeats, computes leader election deterministically, and checks for remote shutdown requests. Six existing periodic services are gated behind `IInstanceCoordinator.IsLeader`. The dashboard UI shows live instances and allows individual shutdown.

**Tech Stack:** .NET 10 / C# 13, SQLite (Microsoft.Data.Sqlite), React 19 + TanStack Query, ASP.NET Core Minimal APIs

**Spec:** `docs/superpowers/specs/2026-03-25-multi-instance-coordination-design.md`

---

### Task 1: HealthDatabase

**Files:**
- Create: `src/AwesomeImapMcp.Core/Database/HealthDatabase.cs`
- Test: `tests/AwesomeImapMcp.Core.Tests/Database/HealthDatabaseTests.cs`

- [ ] **Step 1: Write failing tests for HealthDatabase**

Create `tests/AwesomeImapMcp.Core.Tests/Database/HealthDatabaseTests.cs` with tests for:
- `Upsert_and_read_heartbeat` — upsert one row, read all, verify fields
- `Upsert_updates_existing_row` — upsert twice, verify single row with updated values
- `PruneStale_removes_old_heartbeats` — insert stale + fresh rows, prune, verify only fresh remains
- `SetShutdownRequested_flags_instance` — upsert, set flag, verify `ShutdownRequested == true`
- `SetShutdownRequested_returns_false_for_missing` — call on nonexistent ID, verify returns false
- `DeleteHeartbeat_removes_own_row` — upsert then delete, verify empty

Each test creates a temp file DB and disposes it after.

- [ ] **Step 2: Run tests — verify they fail** (class not found)

Run: `dotnet test --filter "HealthDatabaseTests" --nologo -v q`

- [ ] **Step 3: Implement HealthDatabase**

Create `src/AwesomeImapMcp.Core/Database/HealthDatabase.cs`:

- `HeartbeatRecord` — positional record with all table columns
- `HealthDatabase` — sealed, `IDisposable`, follows `AppDatabase` pattern:
  - Constructor: create directory, open write connection, set `PRAGMA journal_mode=WAL;` and `PRAGMA busy_timeout=5000;` (both via PRAGMA after open, NOT connection string), call `EnsureSchema()`
  - `EnsureSchema()` — CREATE TABLE IF NOT EXISTS `instance_heartbeats`
  - `UpsertHeartbeat(...)` — INSERT ... ON CONFLICT DO UPDATE (updates `last_heartbeat`, `accounts_count`, `cpu_time_ms`, `memory_mb`)
  - `GetAllHeartbeats()` — SELECT * ORDER BY started_at, returns `List<HeartbeatRecord>`
  - `PruneStale(TimeSpan staleThreshold)` — DELETE WHERE last_heartbeat < cutoff
  - `SetShutdownRequested(string instanceId)` — UPDATE ... SET shutdown_requested=1, returns bool (rows affected > 0)
  - `DeleteHeartbeat(string instanceId)` — DELETE WHERE instance_id = $id
  - `GetReadConnection()` — fresh read-only connection with `PRAGMA busy_timeout=5000`
  - Write methods use `_writeLock.Wait()`/`Release()` pattern

Table schema (no `is_leader` column — leadership computed locally):
```sql
CREATE TABLE IF NOT EXISTS instance_heartbeats (
    instance_id TEXT PRIMARY KEY, process_id INTEGER NOT NULL,
    cwd TEXT NOT NULL, transport TEXT NOT NULL,
    is_dashboard_host INTEGER NOT NULL DEFAULT 0,
    started_at TEXT NOT NULL, last_heartbeat TEXT NOT NULL,
    accounts_count INTEGER NOT NULL DEFAULT 0,
    cpu_time_ms INTEGER NOT NULL DEFAULT 0, memory_mb INTEGER NOT NULL DEFAULT 0,
    shutdown_requested INTEGER NOT NULL DEFAULT 0
);
```

- [ ] **Step 4: Run tests — verify all pass**

Run: `dotnet test --filter "HealthDatabaseTests" --nologo -v q`

- [ ] **Step 5: Commit**

```
git add src/AwesomeImapMcp.Core/Database/HealthDatabase.cs tests/AwesomeImapMcp.Core.Tests/Database/HealthDatabaseTests.cs
git commit -m "feat: add HealthDatabase for multi-instance heartbeats"
```

---

### Task 2: Configuration — Heartbeat Settings

**Files:**
- Modify: `src/AwesomeImapMcp.Core/Configuration/AppConfig.cs`

- [ ] **Step 1: Add two properties to `ServerConfig` class** (after `LogDir`):

```csharp
[JsonPropertyName("heartbeat_interval")]
public int HeartbeatInterval { get; set; } = 10;

[JsonPropertyName("heartbeat_stale_after")]
public int HeartbeatStaleAfter { get; set; } = 5;
```

- [ ] **Step 2: Build — verify 0 errors**

Run: `dotnet build --nologo -v q`

- [ ] **Step 3: Commit**

```
git add src/AwesomeImapMcp.Core/Configuration/AppConfig.cs
git commit -m "feat: add heartbeat_interval and heartbeat_stale_after config"
```

---

### Task 3: IInstanceCoordinator Interface + InstanceHeartbeat Record

**Files:**
- Create: `src/AwesomeImapMcp.Core/Coordination/IInstanceCoordinator.cs`
- Create: `src/AwesomeImapMcp.Core/Coordination/InstanceHeartbeat.cs`

- [ ] **Step 1: Create IInstanceCoordinator**

```csharp
namespace AwesomeImapMcp.Core.Coordination;

public interface IInstanceCoordinator
{
    bool IsLeader { get; }
    string InstanceId { get; }
    IReadOnlyList<InstanceHeartbeat> GetLiveInstances();
    Task<bool> RequestShutdownAsync(string instanceId);
}
```

- [ ] **Step 2: Create InstanceHeartbeat record**

```csharp
namespace AwesomeImapMcp.Core.Coordination;

public record InstanceHeartbeat(
    string InstanceId, int ProcessId, string Cwd, string Transport,
    bool IsDashboardHost, bool IsLeader, string StartedAt,
    string LastHeartbeat, int AccountsCount, long CpuTimeMs,
    int MemoryMb, bool ShutdownRequested);
```

- [ ] **Step 3: Build, commit**

```
git add src/AwesomeImapMcp.Core/Coordination/
git commit -m "feat: add IInstanceCoordinator interface and InstanceHeartbeat record"
```

---

### Task 4: InstanceCoordinator BackgroundService

**Files:**
- Create: `src/AwesomeImapMcp.Core/Coordination/InstanceCoordinator.cs`
- Test: `tests/AwesomeImapMcp.Core.Tests/Coordination/InstanceCoordinatorTests.cs`

**IMPORTANT — Circular dependency avoidance:** `InstanceCoordinator` lives in `AwesomeImapMcp.Core` but needs account count (from `AccountRepository` in `ImapClient`). To avoid a Core→ImapClient cycle, the coordinator accepts a `Func<int> accountCountProvider` delegate instead of `AccountRepository` directly. This delegate is wired in `Program.cs` (Task 5).

- [ ] **Step 1: Write tests for leader election logic**

Create `tests/AwesomeImapMcp.Core.Tests/Coordination/InstanceCoordinatorTests.cs`.

Test `ComputeLeaderId` (a static pure function on the class):
- `ComputeLeader_dashboard_host_wins` — 2 instances, one is dashboard host → dashboard wins
- `ComputeLeader_oldest_wins_when_no_dashboard` — 2 non-dashboard instances → earliest started_at wins
- `ComputeLeader_stale_instances_excluded` — stale dashboard + fresh stdio → fresh wins
- `ComputeLeader_multiple_dashboards_oldest_wins` — 2 dashboard hosts → earliest started_at wins
- `ComputeLeader_returns_null_when_no_instances` — empty list → null

These tests only call the static method, no DI needed.

- [ ] **Step 2: Run tests — verify they fail**

Run: `dotnet test --filter "InstanceCoordinatorTests" --nologo -v q`

- [ ] **Step 3: Implement InstanceCoordinator**

Create `src/AwesomeImapMcp.Core/Coordination/InstanceCoordinator.cs`:

Constructor takes: `HealthDatabase`, `InstanceInfo`, `AppConfig`, `Func<int> accountCountProvider`, `IHostApplicationLifetime`, `ILogger<InstanceCoordinator>`

- `IsLeader` property (volatile bool, set in heartbeat loop)
- `InstanceId` → `_instanceInfo.Id`
- `_isDashboardHost` → `config.Server.DashboardEnabled`
- `_startedAt` → `Process.GetCurrentProcess().StartTime.ToUniversalTime()` (with try/catch fallback)

`ExecuteAsync`:
1. Compute `staleThreshold = interval × staleAfter`
2. **Startup prune:** `_healthDb.PruneStale(staleThreshold)` — removes zombie rows before first election
3. Log startup info
4. Loop:
   - `WriteHeartbeat()` — collects PID, CWD, CPU, memory, account count (via `_accountCountProvider()` wrapped in try/catch)
   - `var all = _healthDb.GetAllHeartbeats()`
   - `var leaderId = ComputeLeaderId(all, staleThreshold)`
   - Set `IsLeader = (leaderId == InstanceId)`, log on change
   - If leader: `_healthDb.PruneStale(staleThreshold)`
   - Check own `shutdown_requested` → if true, log + `_lifetime.StopApplication()` + return
   - `await Task.Delay(interval, stoppingToken)`

`StopAsync`: delete own heartbeat row (with try/catch)

`GetLiveInstances()`: reads all heartbeats, filters fresh ones, computes leader ID, maps to `InstanceHeartbeat` records with `IsLeader` computed

`RequestShutdownAsync(id)`: wraps `_healthDb.SetShutdownRequested(id)` in `Task.FromResult`

`ComputeLeaderId(IReadOnlyList<HeartbeatRecord>, TimeSpan)` — **static public** method:
- Filter to fresh heartbeats (last_heartbeat > cutoff)
- If any have `IsDashboardHost`, pick earliest `StartedAt` among them
- Else pick earliest `StartedAt` overall
- Return null if no fresh instances

- [ ] **Step 4: Run tests — verify all pass**

Run: `dotnet test --filter "InstanceCoordinatorTests" --nologo -v q`

- [ ] **Step 5: Build full solution**

Run: `dotnet build --nologo -v q`

- [ ] **Step 6: Commit**

```
git add src/AwesomeImapMcp.Core/Coordination/InstanceCoordinator.cs tests/AwesomeImapMcp.Core.Tests/Coordination/InstanceCoordinatorTests.cs
git commit -m "feat: add InstanceCoordinator with leader election"
```

---

### Task 5: Register HealthDatabase + InstanceCoordinator in Program.cs

**Files:**
- Modify: `src/AwesomeImapMcp.McpServer/Program.cs`

- [ ] **Step 1: Add registrations after `var database = new AppDatabase(dbPath);` and `MigrationRunner.Migrate(database);`**

Insert BEFORE any hosted service registrations (before `AddDashboard`, `AddHostedService`, etc.):

```csharp
// Health database for multi-instance coordination (separate from main DB)
var healthDbPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "health.db");
var healthDatabase = new HealthDatabase(healthDbPath);
builder.Services.AddSingleton(healthDatabase);

// Instance coordination (heartbeats + leader election)
// Account count delegate avoids circular Core→ImapClient dependency
builder.Services.AddSingleton<Func<int>>(sp =>
{
    var repo = sp.GetRequiredService<AccountRepository>();
    return () => { try { return repo.GetAll().Count; } catch { return 0; } };
});
builder.Services.AddSingleton<IInstanceCoordinator, InstanceCoordinator>();
builder.Services.AddHostedService(sp => (InstanceCoordinator)sp.GetRequiredService<IInstanceCoordinator>());
```

Add usings:
```csharp
using AwesomeImapMcp.Core.Coordination;
```

Verify: `DashboardHost` registration (`AddDashboard`) comes AFTER this block — it needs `HealthDatabase` and `IInstanceCoordinator` to be registered. Check `Program.cs` to confirm ordering.

- [ ] **Step 2: Build and run all tests**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`

- [ ] **Step 3: Commit**

```
git add src/AwesomeImapMcp.McpServer/Program.cs
git commit -m "feat: register HealthDatabase and InstanceCoordinator in DI"
```

---

### Task 6: Add Leader Gates to 6 Periodic Services

**Files:**
- Modify: `src/AwesomeImapMcp.ImapClient/SyncManager.cs`
- Modify: `src/AwesomeImapMcp.Queue/QueueWorker.cs`
- Modify: `src/AwesomeImapMcp.ImapClient/CacheEvictor.cs`
- Modify: `src/AwesomeImapMcp.Core/MetricsCollector.cs`
- Modify: `src/AwesomeImapMcp.Core/OAuth/OAuthTokenRefreshService.cs`
- Modify: `src/AwesomeImapMcp.RestBackend/Zoho/ZohoSyncService.cs`

- [ ] **Step 1: For each service, inject `IInstanceCoordinator` and add leader gate**

Read each file first. Add `IInstanceCoordinator coordinator` to the constructor (primary constructor or traditional — match existing pattern). Add a gate at the top of the main work loop:

```csharp
if (!coordinator.IsLeader)
{
    await Task.Delay(existingServiceInterval, stoppingToken);
    continue;
}
```

**Use each service's OWN existing interval** for the non-leader delay (not HeartbeatInterval):
- SyncManager: use its existing poll interval
- QueueWorker: use `TimeSpan.FromSeconds(1)` (its existing loop delay)
- CacheEvictor: use `TimeSpan.FromMinutes(10)` (its `Interval` constant)
- MetricsCollector: use `TimeSpan.FromSeconds(30)` (its `CollectInterval` constant)
- OAuthTokenRefreshService: use `TimeSpan.FromMinutes(5)` (its `Interval` constant)
- ZohoSyncService: use `TimeSpan.FromMinutes(5)` (its `DefaultPollInterval`)

This avoids unnecessary wakeups and maintains each service's natural rhythm.

Add `using AwesomeImapMcp.Core.Coordination;` to each file.

Note: All projects already reference `AwesomeImapMcp.Core` (verified: ImapClient→Core, Queue→Core, Core has it, RestBackend→Core). `IInstanceCoordinator` is in Core.Coordination, so no new project references needed.

- [ ] **Step 2: Build full solution**

Run: `dotnet build --nologo -v q`

- [ ] **Step 3: Run all tests**

Run: `dotnet test --nologo -v q`

- [ ] **Step 4: Commit** (list all 6 files explicitly)

```
git add src/AwesomeImapMcp.ImapClient/SyncManager.cs src/AwesomeImapMcp.Queue/QueueWorker.cs src/AwesomeImapMcp.ImapClient/CacheEvictor.cs src/AwesomeImapMcp.Core/MetricsCollector.cs src/AwesomeImapMcp.Core/OAuth/OAuthTokenRefreshService.cs src/AwesomeImapMcp.RestBackend/Zoho/ZohoSyncService.cs
git commit -m "feat: add leader gates to 6 periodic services"
```

---

### Task 7: Forward Services to Dashboard DI + Update ServerApi

**Files:**
- Modify: `src/AwesomeImapMcp.Dashboard/DashboardHost.cs`
- Modify: `src/AwesomeImapMcp.Dashboard/ServerApi.cs`

- [ ] **Step 1: Forward HealthDatabase and IInstanceCoordinator in DashboardHost.StartDashboard**

Add after the existing `InstanceInfo` / `RootLifetime` forwarding lines:

```csharp
// Multi-instance coordination
builder.Services.AddSingleton(_rootServices.GetRequiredService<AwesomeImapMcp.Core.Database.HealthDatabase>());
builder.Services.AddSingleton(_rootServices.GetRequiredService<AwesomeImapMcp.Core.Coordination.IInstanceCoordinator>());
```

- [ ] **Step 2: Rewrite ServerApi endpoints**

Update `GET /api/server/instances` to use `IInstanceCoordinator` instead of `LogsRepository`:

```csharp
app.MapGet("/api/server/instances", (IInstanceCoordinator coordinator) =>
{
    var instances = coordinator.GetLiveInstances();
    return Results.Ok(new { current = coordinator.InstanceId, instances });
});
```

Add `POST /api/server/instances/{instanceId}/shutdown`:

```csharp
app.MapPost("/api/server/instances/{instanceId}/shutdown", async (
    string instanceId, IInstanceCoordinator coordinator, InstanceInfo self,
    ILogger<RootLifetime> logger) =>
{
    if (instanceId == self.Id)
        return Results.BadRequest(new { error = "Use /api/server/shutdown to stop the dashboard instance." });

    logger.LogWarning("Remote shutdown requested for instance {InstanceId}", instanceId);
    var success = await coordinator.RequestShutdownAsync(instanceId).ConfigureAwait(false);
    return success
        ? Results.Ok(new { shutting_down = true, instance_id = instanceId })
        : Results.NotFound(new { error = $"Instance '{instanceId}' not found or already stale." });
});
```

Add `using AwesomeImapMcp.Core.Coordination;` to ServerApi.cs.

- [ ] **Step 3: Build and test**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`

- [ ] **Step 4: Commit**

```
git add src/AwesomeImapMcp.Dashboard/DashboardHost.cs src/AwesomeImapMcp.Dashboard/ServerApi.cs
git commit -m "feat: heartbeat-based instances endpoint and remote shutdown"
```

---

### Task 8: Update LogsApi with live_only Filter

**Files:**
- Modify: `src/AwesomeImapMcp.Dashboard/LogsApi.cs`
- Modify: `src/AwesomeImapMcp.Core/Repositories/LogsRepository.cs`

- [ ] **Step 1: Update LogsApi endpoint**

Inject `IInstanceCoordinator` as a lambda parameter (consistent with codebase pattern — NOT via service locator):

```csharp
app.MapGet("/api/logs", (HttpContext ctx, LogsRepository logsRepo, IInstanceCoordinator coordinator) =>
```

Parse `live_only` query param. When `live_only=true` AND no explicit `instance_id` is set, get live instance IDs:

```csharp
var liveOnly = ctx.Request.Query["live_only"].FirstOrDefault() == "true";
IReadOnlyList<string>? liveInstanceIds = null;
if (liveOnly && instanceId is null)
{
    liveInstanceIds = coordinator.GetLiveInstances().Select(i => i.InstanceId).ToList();
}
```

Pass `liveInstanceIds` (as `IReadOnlyList<string>?`) to `logsRepo.QueryCount(...)` and `logsRepo.Query(...)`.

- [ ] **Step 2: Update LogsRepository.BuildWhereClause**

Add `IReadOnlyList<string>? liveInstanceIds` parameter to `Query`, `QueryCount`, and `BuildWhereClause`. In `BuildWhereClause`, when `liveInstanceIds` is not null and not empty:

```csharp
if (liveInstanceIds is { Count: > 0 })
{
    var placeholders = string.Join(",", liveInstanceIds.Select((_, i) => $"$live{i}"));
    where += $" AND instance_id IN ({placeholders})";
    for (var i = 0; i < liveInstanceIds.Count; i++)
        cmd.Parameters.AddWithValue($"$live{i}", liveInstanceIds[i]);
}
```

When `live_only=true` AND `instance_id` is also set, `instance_id` takes precedence (liveInstanceIds stays null). Add a comment noting this precedence.

- [ ] **Step 3: Build and test**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`

- [ ] **Step 4: Commit**

```
git add src/AwesomeImapMcp.Dashboard/LogsApi.cs src/AwesomeImapMcp.Core/Repositories/LogsRepository.cs
git commit -m "feat: add live_only filter to logs API with parameterized IN clause"
```

---

### Task 9: Frontend — Update useApi.ts Hooks

**Files:**
- Modify: `dashboard/client/src/hooks/useApi.ts`

- [ ] **Step 1: Add instance-related types and hooks**

```typescript
export interface InstanceHeartbeat {
  instanceId: string
  processId: number
  cwd: string
  transport: string
  isDashboardHost: boolean
  isLeader: boolean
  startedAt: string
  lastHeartbeat: string
  accountsCount: number
  cpuTimeMs: number
  memoryMb: number
  shutdownRequested: boolean
}

export interface InstancesResponse {
  current: string
  instances: InstanceHeartbeat[]
}

export function useInstances() {
  return useQuery({
    queryKey: ['instances'],
    queryFn: () => apiFetch<InstancesResponse>('/api/server/instances'),
    refetchInterval: 10000,
  })
}

export function useShutdownInstance() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (instanceId: string) =>
      apiFetch<{ shutting_down: boolean }>(
        `/api/server/instances/${encodeURIComponent(instanceId)}/shutdown`,
        { method: 'POST' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['instances'] }),
  })
}
```

Update `useLogs` to accept `live_only?: boolean`:
```typescript
if (params.live_only) qs.set('live_only', 'true')
```

- [ ] **Step 2: Build frontend**

Run: `cd dashboard/client && npm run build`

- [ ] **Step 3: Commit**

```
git add dashboard/client/src/hooks/useApi.ts
git commit -m "feat: add useInstances, useShutdownInstance hooks and live_only to useLogs"
```

---

### Task 10: Frontend — Settings > Instance Table

**Files:**
- Modify: `dashboard/client/src/pages/Settings.tsx`

- [ ] **Step 1: Rewrite ServerControlsCard as multi-instance table**

Replace the existing `ServerControlsCard` with a new version that:
- Uses `useInstances()` instead of `useServerInfo()`
- Renders a table of all live instances with columns:
  - PID (mono), CWD (truncated, mono), Transport, Role (Leader badge + Dashboard badge), Uptime (computed from `startedAt`), CPU (formatted), Mem (MB)
  - Actions column: Shutdown button per row
- Dashboard's own row (where `instanceId === current`): disable shutdown button, add "(this)" label
- Keep existing self-shutdown button below the table (using `useShutdownServer`)
- Auto-refreshes every 10s via the hook's `refetchInterval`
- Shutdown button uses `useShutdownInstance` mutation with confirm pattern

- [ ] **Step 2: Build frontend**

Run: `cd dashboard/client && npm run build`

- [ ] **Step 3: Commit**

```
git add dashboard/client/src/pages/Settings.tsx
git commit -m "feat: multi-instance server controls table with remote shutdown"
```

---

### Task 11: Frontend — Logs Page (Live Instances Default)

**Files:**
- Modify: `dashboard/client/src/pages/Logs.tsx`

- [ ] **Step 1: Update instance filter with "Live instances" default**

- Change initial `instanceId` state from `''` to `'__live__'`
- In the instance dropdown:
  - First option: `<option value="__live__">Live instances</option>`
  - Second option: `<option value="">All instances</option>`
  - Then individual instance IDs from `useLogInstances()`
- When building `useLogs` params:
  - If `instanceId === '__live__'`: pass `live_only: true` (no `instance_id`)
  - If `instanceId === ''`: pass neither
  - Otherwise: pass `instance_id: instanceId`

- [ ] **Step 2: Build frontend**

Run: `cd dashboard/client && npm run build`

- [ ] **Step 3: Commit**

```
git add dashboard/client/src/pages/Logs.tsx
git commit -m "feat: default logs filter to live instances"
```

---

### Task 12: Final Verification

- [ ] **Step 1: Run full test suite**

Run: `dotnet test --nologo -v q`

- [ ] **Step 2: Build frontend**

Run: `cd dashboard/client && npm run build`

- [ ] **Step 3: Full solution build**

Run: `dotnet build --nologo -v q`

- [ ] **Step 4: Push**

```
git push origin main
```
