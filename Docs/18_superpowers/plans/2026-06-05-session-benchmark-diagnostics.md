# Session Benchmark Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an MVP that turns one Pudding chat session into a structured Hermes/Pudding benchmark diagnostics report.

**Architecture:** Add a read-only diagnostics service in `PuddingPlatform` that aggregates existing JSONL, tool approval stores, runtime timeline, and session logs. Expose the report through an authenticated diagnostics API and add a compact Chat UI entry point that opens a report drawer.

**Tech Stack:** ASP.NET Core controllers/services, existing Pudding file stores, System.Text.Json, React/TypeScript, Ant Design, Jest/Vitest-style existing frontend tests, xUnit backend tests.

---

### Task 1: Backend Diagnostics Service

**Files:**
- Create: `Source/PuddingPlatform/Services/Diagnostics/SessionBenchmarkDiagnosticsService.cs`
- Test: `Source/PuddingPlatformTests/Services/SessionBenchmarkDiagnosticsServiceTests.cs`

- [ ] Write failing tests for tool call/result counts, failed result classification, approval counts, tickets, and score calculation.
- [ ] Implement JSONL parsing with UTF-8 BOM tolerance and malformed-line skipping.
- [ ] Aggregate approval audit events and tickets through existing approval stores.
- [ ] Read the latest session timeline JSONL and session log file when present.
- [ ] Compute friction points and scores.
- [ ] Run targeted backend tests.

### Task 2: Diagnostics API

**Files:**
- Modify: `Source/PuddingPlatform/Controllers/Api/DiagnosticsTimelineController.cs`

- [ ] Write or extend controller tests if an existing test pattern is available.
- [ ] Add `GET /api/diagnostics/sessions/{sessionId}/benchmark`.
- [ ] Validate empty session ids and return 404 when the session JSONL is missing.
- [ ] Redact text fields that may contain sensitive command output.
- [ ] Run targeted backend tests.

### Task 3: Frontend API Contract

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/services/platform/api.ts`

- [ ] Add TypeScript DTOs for benchmark diagnostics.
- [ ] Add `getSessionBenchmarkDiagnostics(sessionId)`.
- [ ] Keep numeric counters as numbers; keep large ids as strings.
- [ ] Run TypeScript or targeted lint check.

### Task 4: Chat UI Drawer

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/SessionBenchmarkDrawer.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageProcessSummary.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentMessageBubble.tsx`
- Test: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageProcessSummary.test.tsx`

- [ ] Add a secondary "诊断报告" action near the process summary.
- [ ] Pass the session id into message bubbles and process summaries.
- [ ] Load diagnostics lazily when the drawer opens.
- [ ] Show overview, friction points, approval counts, and failure summary.
- [ ] Add a focused UI test for opening the diagnostics action.
- [ ] Run targeted frontend tests and lint.

### Task 5: Verification

- [ ] Run backend diagnostics service tests.
- [ ] Run frontend process summary tests.
- [ ] Call the new endpoint against session `3835ea4c18fd4361a0393b3c19259e80`.
- [ ] Open the current chat page in the in-app browser and verify the diagnostics drawer renders without overlapping or broken text.
- [ ] Record any remaining gaps as follow-up items.
