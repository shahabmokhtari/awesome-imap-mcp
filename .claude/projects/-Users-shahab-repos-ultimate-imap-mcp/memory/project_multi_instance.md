---
name: Multi-instance architecture pending
description: Major refactor pending - primary/secondary instance model, proxy tools, batch body fetch, new MCP tools
type: project
---

Large architectural change planned for next session. Spec at `docs/superpowers/specs/2026-04-01-multi-instance-architecture.md`.

**Why:** User wants only one instance to own sync/queue/cache. All other MCP instances proxy to primary via HTTP. Primary failover when leader goes down.

**How to apply:** Start next session by reading the spec file and implementing in phases:
1. Primary/secondary detection + proxy tool executor
2. Leader failover (promote secondary when primary dies)
3. Batch body fetch tool + search with bodies
4. Research open-source email MCP servers for new tool ideas
