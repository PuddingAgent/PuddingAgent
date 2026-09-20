# TestScripts

This directory contains lightweight local diagnostics and test helpers for the
PuddingAgent development workspace.

`TestScripts` is part of the observability presentation layer. Scripts here
should extract structured, quantitative findings first, then link back to raw
logs only when the original evidence is needed.

Preferred script output includes:

- stable IDs such as session, workspace, trace, benchmark case, tool, and ticket
- counts, durations, rates, line counts, character counts, and threshold levels
- failure categories and recovery paths
- a compact raw-evidence pointer instead of full log dumps

For long-term statistics, prefer SQLite telemetry facts such as
`telemetry_metric_events`; use JSONL and text logs as replay evidence.

## Session log diagnostics

Use `diagnose_session_logs.py` when a chat session needs a focused postmortem
without searching the entire repository.

```powershell
python TestScripts\diagnose_session_logs.py <session-id>
python TestScripts\diagnose_session_logs.py <session-id> --json
python TestScripts\diagnose_session_logs.py <session-id> --max-errors 20
python TestScripts\diagnose_session_logs.py <session-id> --data-dir data
```

The script reads these local runtime files when present:

- `data/jsonl/<session-id>.jsonl`
- `data/runtime/tool-approval/audit-events.jsonl`
- `data/runtime/tool-approval/tickets.json`
- `data/logs/diagnostics/session-timeline/**/<session-id>.jsonl`
- `data/logs/sessions/<session-id>/session-*.log`

The text report is optimized for quick diagnosis:

- token usage
- tool call/result counts
- failed tool results and paired commands
- approval event counts and ticket mismatch reasons
- approval tickets for the session
- timeline failures
- warning/error lines from the session log

Use `--json` when the output needs to feed another script or dashboard.

## Tests

Run the diagnostic script tests with:

```powershell
python TestScripts\diagnose_session_logs_tests.py
```

## Test suite gates

Run the declared suite list and compare each suite's counts against a declared
baseline budget:

```powershell
pwsh -File TestScripts\test-pudding-suite-gates.ps1              # all declared suites
pwsh -File TestScripts\test-pudding-suite-gates.ps1 -Only Core   # a single suite
pwsh -File TestScripts\test-pudding-suite-gates.ps1 -ListOnly    # show baselines only
```

Why this exists: whole suites once stayed red without anyone noticing
(`PuddingCoreTests` contract-freeze tests were red from 2026-07-23, i.e. ~2 months).
The red tests were the symptom; the missing gate was the cause. "The suites I happened
to run" is not the same as "all suites".

Contract:

- each suite declares `AllowedFailures` (known-red budget) and `KnownRed` (test names allowed to fail);
- `AllowedFailures = $null` means **report only** (`UNMEASURED`) - an unknown baseline is never treated as a pass;
- full per-suite output is written to `temp/suite-gates/<suite>.log`; the script prints only a summary;
- exit code `0` = every suite within budget, `1` = at least one suite over budget;
- when counts change, update the baseline **inside the script** and state the measurement
  date plus evidence in the commit message (the baseline is a contract, not a convenience).

Current baselines (2026-09-21): `Core` 910 passed / 1 known-red,
`Runtime` 1658 / 0, `Platform` 1363 / 0, `AdminJest` 1336 / 17 known-red, `WebApi` unmeasured.
