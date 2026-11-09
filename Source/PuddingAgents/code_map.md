# Agents

| File | Responsibility |
|---|---|
| `AgentOwnershipPolicy.cs` | Host-bound identity checks: workspace fence, child output/cost limits that may only tighten, capability allowlist; no caller string can widen the bound scope. |
| `AgentDirectoryService.cs` | Borrowed-handle directory reads: bound workspace only, page size/cursor bounds, Cursor/Complete passthrough, absence decided by Get (a truncated page is never "not found"). |
| `AgentWorkControlService.cs` | Observe binds an exact operation id and event cursor (retention returns `snapshot_required`); cancel is an idempotent persistent command keyed by OperationId with content conflict detection; Requested never claims termination. |
| `AgentDelegationService.cs` | Delegation injects the bound parent work, validates parent equality, child limits and capabilities; acquire/release use the atomic lease pool with owner/generation fencing. |
