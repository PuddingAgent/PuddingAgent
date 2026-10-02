# Persistent Event Queue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the in-memory priority event queue with a SQLite-backed queue that survives process restarts and supports lease, retry, and dead-letter states.

**Architecture:** Store event queue records in the existing Platform SQLite database. `PriorityEventQueue` remains the `IPriorityEventQueue` implementation, but now persists queue records and leases work atomically. `EventDispatcher` keeps the same handler routing model and updates queue status instead of re-enqueueing duplicate retry records.

**Tech Stack:** .NET 10, EF Core, SQLite, existing `PuddingPlatform.Data.PlatformDbContext`, `PuddingRuntime.Services.Events`.

---

## Tasks

### Task 1: Event Queue Table

**Files:**
- Create: `Source/PuddingPlatform/Data/Entities/EventQueueEntity.cs`
- Modify: `Source/PuddingPlatform/Data/PlatformDbContext.cs`
- Modify: `Source/PuddingAgent/Program.cs`

- [ ] Add `event_queue` table with `event_id`, `priority`, `event_type`, `source_type`, `source_id`, `connector_id`, `session_id`, `workspace_id`, `agent_id`, `payload`, `status`, `retry_count`, `available_at`, `lease_until`, `started_at`, `completed_at`, `created_at`, `updated_at`, `error_message`, and trace metadata columns.
- [ ] Add indexes for dequeue ordering and diagnostics.
- [ ] Add non-destructive startup DDL.

### Task 2: SQLite Queue Implementation

**Files:**
- Modify: `Source/PuddingRuntime/Services/Events/PriorityEventQueue.cs`
- Modify: `Source/PuddingCore/Models/InternalEvent.cs`

- [ ] Replace in-memory queues with `PlatformDbContext` persistence.
- [ ] `EnqueueAsync` inserts a pending row, upserts same `event_id` only when an existing row is terminal.
- [ ] `DequeueAsync` selects the highest-priority pending/retrying row where `available_at <= now`, assigns a `lease_until`, increments `retry_count`, and returns a `QueuedEvent`.
- [ ] `PeekAsync` reads the next available row without leasing.
- [ ] `UpdateStatusAsync` updates completed/dead-letter/retrying states and timestamps.
- [ ] `GetStatsAsync` returns pending/retrying counts by priority and processing count.

### Task 3: Dispatcher Retry Semantics

**Files:**
- Modify: `Source/PuddingRuntime/Services/Events/EventDispatcher.cs`

- [ ] Remove fire-and-forget delayed re-enqueue.
- [ ] On retry, call `UpdateStatusAsync(qe.Id, "retrying", "...")`; queue computes next `available_at`.
- [ ] On success, mark completed.
- [ ] On retry exhaustion, mark dead_letter.

### Task 4: Verification

**Commands:**
- `dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo`
- `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo`
- `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter "RuntimeTraceContextTests|StartupSchemaSafetyTests" --logger "console;verbosity=minimal"`

Expected: builds and focused tests pass. Existing warnings may remain.

