# Diagnostic Timeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add backend/server diagnostic timeline logging and a standalone Python SQLite/log query toolkit for diagnosing slow chat rendering.

**Architecture:** Keep business logs separate from machine-readable diagnostics. Backend diagnostic events go through a `SessionTimelineRecorder` that writes `runtime_activity` and JSONL under `data/logs/diagnostics`. Local Python tools under `Tools/Diagnostics` query SQLite and assemble export bundles without touching production code paths.

**Tech Stack:** .NET 10, MSTest, SQLite, Python stdlib.

---

### Task 1: Backend Timeline Recorder

**Files:**
- Create: `Source/PuddingPlatform/Services/Diagnostics/SessionTimelineRecorder.cs`
- Test: `Source/PuddingPlatformTests/Services/SessionTimelineRecorderTests.cs`
- Modify: `Source/PuddingAgent/Program.cs`

- [ ] Write MSTest coverage for enabled/disabled recorder behavior.
- [ ] Implement recorder with stable JSONL schema and runtime_activity mirroring.
- [ ] Register recorder and options in DI.

### Task 2: Chat/SSE Timeline Events

**Files:**
- Modify: `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs`
- Modify: `Source/PuddingPlatform/Controllers/Api/SessionEventsController.cs`

- [ ] Record request, route, dispatch, metadata wait, return, SSE subscribe, SSE write, and SSE flush events.
- [ ] Keep failures non-fatal and isolated from request handling.

### Task 3: Proxy JSONL Diagnostics

**Files:**
- Modify: `dev-up.py`
- Test: `TestScripts/dev_up_tests.py`

- [ ] Write proxy diagnostic events to `data/logs/diagnostics/proxy/YYYYMMDD.jsonl`.
- [ ] Keep existing human-readable `dev-up-YYYY-MM-DD.log` output.

### Task 4: Standalone Python Tools

**Files:**
- Create: `Tools/Diagnostics/README.md`
- Create: `Tools/Diagnostics/inspect_schema.py`
- Create: `Tools/Diagnostics/query_timeline.py`
- Create: `Tools/Diagnostics/export_session_bundle.py`
- Create: `Tools/Diagnostics/tests/test_query_timeline.py`

- [ ] Use repo `data` as default input root.
- [ ] Support `--session-id`, `--trace-id`, `--format table|json`, and bundle output under `temp/diagnostics`.
- [ ] Use only Python stdlib initially.

### Task 5: Documentation

**Files:**
- Modify: `Agent.md`
- Modify: `README.md`

- [ ] Add diagnostics tool location, common commands, and output conventions.

### Task 6: Verification

- [ ] Run focused .NET tests.
- [ ] Run Python unittest tests.
- [ ] Run dev-up proxy tests when relevant.
