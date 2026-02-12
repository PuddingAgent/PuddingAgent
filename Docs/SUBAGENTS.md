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

The tool wraps child output into a JSON result envelope. If a child does not follow the section format, the raw output is still preserved in `rawOutput`, but structured fields may be incomplete.

## Runtime Controls

Sub-agent execution is controlled by `config/runtime.execution.json` under the active `PuddingDataPaths` data root.

Current defaults:

```json
{
  "subAgents": {
    "maxConcurrentPerTemplate": 3,
    "maxConcurrentPerWorkspace": 6,
    "defaultTimeoutSeconds": 3600,
    "maxTimeoutSeconds": 3600,
    "defaultPermissionMode": "inherit"
  }
}
```

Permission mode:

- `inherit`: default. The child inherits the parent agent capability policy.
- `low`: the child can only use low-risk tools exposed by the current registry.

Timeout:

- `timeout_seconds` may be passed per call.
- It must not exceed `maxTimeoutSeconds`.
- If omitted, `defaultTimeoutSeconds` is used.

## Design Notes

The parent agent owns task decomposition. The sub-agent owns only the assigned scope.

The `spawn_sub_agent` tool must not silently infer large missing boundaries. If scope, stop condition, model, or template cannot be resolved, it should return an explicit error with available options where possible.

This protocol is intentionally small. More detailed execution logs belong in run archives and diagnostics, not in the delegation contract.
