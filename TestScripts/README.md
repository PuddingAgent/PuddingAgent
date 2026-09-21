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

- **Judgement is case identity, not a failure count.** The budget is **derived** from `KnownRed.Count`;
  the `AllowedFailures` knob has been **removed**. Replacing an old red with a brand-new red is therefore a
  FAIL, never a silent pass - an optimiser must not be able to move the goalposts. `-SelfTest` prints the
  legacy count-based verdict side by side to show exactly what the old rule let through.
- **Evidence is structured only.** Counts and failing-case names come from machine-readable artifacts:
  dotnet => `--logger "trx;LogFileName=<suite>.trx"` (UTF-8 XML), jest => `--json --outputFile`.
  Human-readable text is **never** parsed. Measured 2026-09-21: the human log is mojibake (CP936 bytes
  decoded as UTF-8), so text regexes silently matched **nothing** and three dotnet suites were
  mis-reported as unmeasured. A missing or unparseable structured artifact means `UNMEASURED`
  (fail-closed; no fallback to text guessing).
- **TRX needs namespace-agnostic XPath.** All TRX elements live under the `VisualStudio/TeamTest/2010`
  namespace, so `//ResultSummary/Counters` matches **nothing**; use `//*[local-name()='Counters']`.
  (Same incident, second cause.)
- **Case names are compared after structural normalisation**: any run of non-letter/non-digit/non-underscore
  characters collapses to a single space, so the jest separator, `>`, and odd whitespace all agree. Both
  sides use the same function, and the script contains no non-ASCII literal in any pattern.
- **An unregistered list cannot pass.** `KnownRed = $null` with observed failures is never a PASS.
- **Known-red lists expire.** `KnownRedMeasuredAt` plus `KnownRedMaxAgeDays` (default 30); an expired list is
  a FAIL, so a known red cannot become a permanent exemption. A declared case that no longer fails is
  reported as `known_red_stale` - a hint to tighten the list, not a FAIL.
- **Required suites must be measured.** `Required = $true` (default) with `UNMEASURED` or `SKIPPED_LOCKED` is a
  FAIL (`required_not_measured`) and a non-zero exit. Not measuring is no longer equivalent to passing.
- **Exemptions carry an expiry.** `Required = $false` requires `Exemption = @{ Reason; ExpiresOn; Owner }`;
  a missing or expired exemption is a FAIL. No silent opt-outs.
- **Source fingerprint** (`HEAD` + dirty-entry hash) is recorded next to each `BaselineCommit`, so a baseline
  can be traced to the revision it was measured on. Fingerprint drift is reported, not enforced - a gate that
  is always red simply gets ignored.
- The per-case disposition ledger for frontend known-red lives in `TestScripts/known-red-dispositions.md`
  (class A = test lag / B = config-copy lag / C = real defect / D = broken test, each with evidence).
  Only cases registered there may be tightened into `KnownRed`; never guess a disposition.
- Evidence artifacts per suite: `temp/suite-gates/<suite>.raw.log` (human-readable, **not** used for
  judgement) plus `temp/suite-gates/<suite>.trx` or `<suite>.jest.json` (**the** judgement source).
- Exit code `0` = every suite PASS **and** the operator architecture gate green; `1` = any FAIL or a non-zero
  operator gate (see "Operator architecture gate" below).
- When a baseline changes, update it **inside the script** and state the measurement date plus evidence in the
  commit message (the baseline is a contract, not a convenience).

Current baselines (2026-09-21, measured with this script; `-Only Core,AdminJest` and
`-Only Runtime,Platform` both exited 0): `Core` 910 passed / **1 known-red**
(`ProcessSwarmAsync_WithInvalidSwarmDirectory_HandlesError`), `Runtime` **1739** / 0,
`Platform` **1379** / 0, `AdminJest` 1349 / 3 known-red - and those three cases are now
**registered** in `KnownRed`, so that suite is judged by case identity rather than by budget alone.
All three are the voice family; thirteen cases were fixed on 2026-09-21 - one **real defect**
(missing admin menu icon mappings for `hdd`/`key`), one flaky suite calibrated (`jest.setTimeout`),
one time-bomb fixture (relative dates), three copy/UI/carrier-migration cases, and seven other
test-lag cases; see `known-red-dispositions.md`.

The `Runtime` baseline moved 1659 → 1710 without any behaviour change, and the delta is **accounted for**:
1710 − 22 = **1688**, where the 22 are the new operator-port cases added by the S1b slice
(`Source/PuddingRuntimeTests/Operators/`: 3 wiring + 9 registry + 10 adapter-equivalence), matching the
parent-declared 1688 pre-S1b baseline exactly. The older 1659 figure predates commits made in parallel with
this work (1688 − 1659 = +29 cases from other commits). Two systematic reasons the number moves between
measurement points: (a) parallel collaborators keep adding cases to the same suite, and (b) this gate
**always** filters `TestCategory!=Live`, so Live cases (real-model tests - 3 attributes in 2 files in this
suite) never enter the count. A baseline is a contract: when the number moves, state the measurement date and
the raw evidence in the commit message.

`WebApi` is **not measurable while the Core process is running**: its build needs to write
`Source/PuddingAgent/bin/Debug/net10.0/*.dll`, which the live process locks (`MSB3027`/`MSB3021`).
The script reports that case as `SKIPPED_LOCKED` - a third honest state that is neither a pass nor a
failure, so a build lock is never misread as a red suite. Measure it with the Core stopped
failure, so a build lock is never misread as a red suite. It is declared with an **explicit, expiring
exemption** (`Required = $false` plus `Exemption.ExpiresOn = 2026-10-05`); once that date passes the gate
**fails**, so the exemption cannot quietly become permanent.

## Operator architecture gate

`test-operators-architecture-gates.ps1` is **not** an optional standalone script. The suite gate runs it
after the declared suites, prints its raw output, and **its non-zero exit propagates to the main gate's exit
code** (fail-closed). A *missing* gate script is also treated as non-zero, so deleting the guard cannot
silently re-open the boundary.

Checks (recursive over `Source/PuddingCore/Operators/**` and `Source/PuddingRuntime/Operators/**`):

| check | pattern | expected |
|---|---|---|
| vendor isolation | `(?i)jev\|openai\|anthropic\|typesafe` | `hits=0` |
| reverse dependency | `(?i)\bRsi\|GoalService\|ToolApproval\b` | `hits=0` |

Why it must be a gate: a contract layer that quietly starts depending on a concrete implementation or on one
vendor still compiles, and no unit test turns red. Only an architecture gate holds that line.

How the propagation works: the operator gate is launched as an **independent `pwsh` process** and its native
process exit code is read. Measured 2026-09-21: reading `$LASTEXITCODE` after calling a `.ps1` with `&` is
**unreliable when the caller is itself a script** - a nested `exit 1` did *not* update the caller's
`$LASTEXITCODE` (observed `0`), i.e. the guard would have failed **open**. Native process exit codes were
reliable in all three contexts (direct call / inside a pipeline / nested script).

Verification recipe (used on 2026-09-21; restores the tree afterwards):

```powershell
# 1. plant a deliberate violation under a scanned directory (e.g. a .cs comment containing a vendor name)
# 2. plant ⇒ the guard must fail and the main gate must turn non-zero
pwsh -File TestScripts\test-pudding-suite-gates.ps1 -Only Core   # expect OperatorsGateExit: 1 and exit 1
# 3. remove the planted file ⇒ the guard must pass again
pwsh -File TestScripts\test-operators-architecture-gates.ps1     # expect "GATES: PASS" and exit 0
```

Known limitation of the patterns: `\bToolApproval\b` is a **word-boundary** match, so prefixed identifiers
(`ToolApprovalPortalService`) and the snake-case scene key `tool_approval` do **not** match. The check
catches bare-word mentions, not every dependency on the approval domain - widen it deliberately if that
matters (the adapter slice relies on this: it wraps the approval classifier on purpose).
