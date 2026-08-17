# SUBAGENTS.md

## Purpose

Pudding uses this document as the stable delegation contract for parent agents that call `spawn_sub_agent`.
The goal is to stop encoding sub-agent work as loose natural language, and instead make delegation, execution, and returned evidence predictable enough for batching, UI display, auditing, and future tool wrapping.

## Delegation Request

Prefer structured fields over a free-form `task`.

```json
{
  "question": "One clear question the sub-agent must answer.",
  "scope": "Files, directories, PR, session, or other bounded review surface.",
  "already_known": "Known facts; do not repeat this work.",
  "effort": "quick | medium | thorough",
  "stop_condition": "When the sub-agent must stop.",
  "output": "SUMMARY, CHANGES, EVIDENCE, RISKS, BLOCKERS",
  "sync": true
}
```

Field rules:

- `question` is the preferred task entry. `task` remains supported for legacy callers.
- `scope` must narrow the work. Avoid global repo-wide requests unless the parent intentionally wants a broad scan.
- `already_known` is mandatory for non-trivial follow-up work because it prevents repeated exploration.
- `effort` controls depth only: `quick`, `medium`, or `thorough`.
- `stop_condition` prevents runaway exploration.
- `output` defaults to `SUMMARY, CHANGES, EVIDENCE, RISKS, BLOCKERS`.

## Batch Delegation

Batch mode must use a JSON array. Do not encode multiple tasks with newline delimiters.

```json
{
  "tasks": [
    {
      "task_id": "qa-runtime",
      "question": "Are runtime sub-agent locks correct?",
      "scope": "Source/PuddingRuntime and Source/PuddingPlatform",
      "already_known": "Sub-agent execution now uses runtime.execution.json.",
      "effort": "medium",
      "stop_condition": "Stop after reviewing lock identity and timeout flow."
    }
  ],
  "sync": true
}
```

`task_id` is required for every batch item and must be unique inside the request. The backend maps returned results back to this id.

## Output Contract

Sub-agents must return these top-level sections in order:

```text
SUMMARY:
CHANGES:
EVIDENCE:
RISKS:
BLOCKERS:
```

Section meaning:

- `SUMMARY`: One paragraph explaining what was done and the conclusion.
- `CHANGES`: Files changed by the sub-agent, or `none` for review-only work.
- `EVIDENCE`: `path:line` references or concrete runtime evidence.
- `RISKS`: Remaining risks, uncertainty, or follow-up risks.
- `BLOCKERS`: Blocking issues that prevented completion, or `none`.

The tool wraps child output into a JSON result envelope. A direct `spawn_sub_agent` call still preserves
non-conforming child output in `rawOutput`, but structured fields may be incomplete.

The seven `smart_*` workflow wrappers use a stricter contract:

- each role defines detailed, role-specific fields inside the same five top-level sections;
- `SmartWorkflowToolBase` extracts `rawOutput` from the result envelope and rejects reports shorter than
  the minimum useful size or missing/empty canonical sections;
- `SUMMARY` and `EVIDENCE` must contain substantive content;
- a response such as `done`, `completed`, or a bare status sentence is a failed Smart workflow result,
  not successful work;
- the wrapper does not automatically retry an invalid report because that could silently double model
  cost. The failure exposes `subAgentId`, `runId`, and the validation reason while preserving the complete
  child result envelope and `rawOutput` for diagnosis.

## Runtime Controls

Sub-agent execution is controlled by `config/runtime.execution.json` under the active `PuddingDataPaths` data root.

Current defaults:

```json
{
  "turns": {
    "defaultHardTimeoutSeconds": 86400,
    "maxHardTimeoutSeconds": 86400,
    "noProgressTimeoutSeconds": 3600,
    "watchdogPollIntervalSeconds": 5,
    "llmFirstChunkTimeoutSeconds": 300,
    "llmStreamIdleTimeoutSeconds": 120
  },
  "subAgents": {
    "maxConcurrentPerTemplate": 3,
    "maxConcurrentPerWorkspace": 6,
    "maxRounds": 600,
    "maxToolCallsTotal": 2400,
    "maxTimeoutSeconds": 86400,
    "budgetGraceRounds": 20,
    "budgetGraceTimeoutSeconds": 1800,
    "parentFinalizationReserveSeconds": 120,
    "defaultPermissionMode": "inherit",
    "transientDirectoryRetention": {
      "enabled": true,
      "scanIntervalMinutes": 360,
      "scaffoldRetentionHours": 24,
      "orphanRetentionHours": 168,
      "quarantineRetentionDays": 7,
      "maxItemsPerSweep": 200
    }
  }
}
```

Transient directory retention only targets the exact empty `data/agents/{subSessionId}/skills/index.json`
scaffold left by older builds. A terminal run must be older than 24 hours; a scaffold with no run index must
be older than 7 days. Eligible directories are quarantined under `retention-archive` for another 7 days before
purge. Running runs, stateful directories, run archives, and any unknown directory shape are never removed.
Sub-sessions still registered in the reusable child-agent pool are also protected regardless of their latest run status.

Permission mode:

- `inherit`: default. The child inherits the parent agent capability policy.
- `low`: the child can only use low-risk tools exposed by the current registry.

Execution budgets:

- Parent agents do not receive `max_rounds`, `max_tool_calls_total`, or `timeout_seconds` fields on
  `spawn_sub_agent`. These ceilings are system policy, not delegation hints.
- A normal `spawn_sub_agent` run receives the configured `maxRounds`, `maxToolCallsTotal`, and
  `maxTimeoutSeconds` budgets: currently 600 rounds, 2400 tool calls, and 24 hours.
- Before the first child LLM call, Runtime injects the current run budget. When the remaining normal
  round budget falls below 80% and 50%, it injects one notice for each threshold with the exact
  remaining round count.
- The hard timeout includes a reserved cleanup window. Reaching the normal round or time budget does
  not fail the child immediately: Runtime injects a cleanup instruction and grants 20 additional
  rounds, while reserving up to 30 minutes inside the same hard deadline. When a parent deadline
  shortens the run, the time reserve is additionally capped at 25% of that effective hard window, so
  at least 75% remains available for normal work. The configured grace round count is clamped to 10-50.
- If the child still has not finished after cleanup, the canonical terminal status is
  `budget_exhausted`, not `failed`. Its staged report and run archive remain available, and the parent
  receives `resumable=true` plus the stable child session id.
- A parent may continue that child with `resume_sub_agent_id`. Pudding reuses the same `SubSessionId`
  and preserved conversation, creates a fresh immutable `runId`, and resets the new run's round,
  tool-call, elapsed-time, and deadline counters from system configuration. The parent cannot grant or
  override numeric budgets. Batch and pool calls cannot combine with `resume_sub_agent_id`.
- Unknown legacy budget fields in a parent tool call are ignored and are not copied into the invocation.
- Smart workflows use the same system-managed child budget; their public schemas do not expose budget fields.
- A conversation Turn freezes one absolute execution deadline. Tool and sub-agent boundaries propagate
  that timestamp; no child may replace it with a later `now + timeout`.
- The absolute deadline is a 24-hour final safety ceiling. Normal stall detection uses the one-hour
  sliding meaningful-progress window. Lease renewals, SSE keepalives, and empty provider frames do not
  renew it; new LLM output, tool results, or child-agent progress do.
- Identical progress fingerprints for the same run and stage do not renew the window. A no-progress
  cancellation commits `execution_stalled`; reaching the hard ceiling commits `execution_timeout`.
- Provider streaming has its own operation watchdog: 300 seconds to the first chunk, then 120 seconds
  between chunks.
- Every synchronous sub-agent invocation reserves `parentFinalizationReserveSeconds` at the end of the
  parent Turn for result consumption, final response generation, and terminal commit. Asynchronous
  children are still bounded by the parent deadline but do not consume this synchronous reserve.
- The effective synchronous child timeout is the minimum of the system ceiling and the remaining parent
  budget after the reserve.
- Waiting for workspace/template concurrency gates consumes the same deadline budget.
- If the reserve cannot be satisfied, scheduling returns `insufficient_execution_budget` before creating
  a child run. This also prevents a retry from consuming the parent's final two minutes.
- Terminal totals are reconciled from the durable run event archive. A cancellation or timeout after many
  rounds must not reset visible round/tool/duration counters to zero.

## Design Notes

The parent agent owns task decomposition. The sub-agent owns only the assigned scope.

The `spawn_sub_agent` tool must not silently infer large missing boundaries. If scope, stop condition, model, or template cannot be resolved, it should return an explicit error with available options where possible.

This protocol is intentionally small. More detailed execution logs belong in run archives and diagnostics, not in the delegation contract.
