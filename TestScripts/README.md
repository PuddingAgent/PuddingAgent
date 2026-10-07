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

## Skill observability helpers

Read-only reports over the skill portfolio and over RSI skill-usage telemetry. Both emit
`KEY=VALUE` machine-readable lines as a first-class output, so callers never have to parse prose.

### `report-skill-usage.ps1`

Aggregates the telemetry written by `JsonlSkillUsageTelemetrySink`
(`<DataRoot>/skill-usage/skill-usage-YYYYMMDD.jsonl`) into a per-skill leaderboard
(injected / readFailed / records / distinct keywords / active agents / first-last seen), a
most-matched-keyword table, and - with `-SkillsRoot` - the list of **installed but never
observed** skills. Malformed lines are counted separately rather than silently dropped.

Why the empty case is stated explicitly: the emitter lives **inside the host process** and only
activates once that process restarts with the telemetry DI registration loaded, so the directory
can legitimately be missing. `STATUS=NO_TELEMETRY_DIR` / `STATUS=NO_TELEMETRY_FILES` means
"no instrumentation yet"; it must **never** be read as "no skill was ever used". Absence of
records and absence of instrumentation are not distinguishable yet.

Validate the aggregation against a synthetic fixture before trusting it in either direction -
an instrument that has never been seen to fail is not evidence.

```powershell
pwsh -File TestScripts\report-skill-usage.ps1 -TelemetryDir D:\data\skill-usage
pwsh -File TestScripts\report-skill-usage.ps1 -TelemetryDir <dir> -SkillsRoot <agent skills root> -OutFile report.md
```

### `goal-rotate.ps1`

Keeps `goal.md` under the `goal_read` truncation limit (measured: a 16,692-byte file came back as
tail-only), by moving the oldest entries **verbatim** into an archive file. Rotation is mechanical
work; if it is not a command, it does not happen.

Contract:

- **dry-run by default**; `-Apply` is required to write anything.
- **One structural check decides everything**: `preamble + all entries`, concatenated, must equal
the original byte-for-byte. If not, the script refuses and writes nothing.
- **Refuses when the budget is unreachable** even at `-KeepEntries` (`BUDGET_INFEASIBLE`).
- `-Apply` **backs up first**, then verifies the written size on read-back.
- **The script body is pure ASCII on purpose**: Windows PowerShell 5.1 decodes a BOM-less UTF-8
  script as CP936, which turns non-ASCII source text into a parse error or mojibake. Non-ASCII
  content here is data (goal text, `-PointerText`), read/written as explicit UTF-8 without BOM.
- The pointer line is only inserted when entries actually move, so a no-op plan cannot report
  `BYTES_AFTER > BYTES_BEFORE`.

Statuses: `DRY_RUN_OK`, `APPLIED`, `NOTHING_TO_DO`, `NO_ENTRIES` (exit 0);
`GOAL_NOT_FOUND`, `SLICE_NOT_LOSSLESS`, `BUDGET_INFEASIBLE`, `CONSERVATION_FAILED` (exit 3).

Measured 2026-09-21 on a synthetic fixture (gitignored `TestScripts/temp/`): mutation of the slice
boundary (`$starts[0] - 2`) produced `REBUILD_OK=False` and `SLICE_NOT_LOSSLESS` - i.e. the
lossless check is **not** vacuous; `-Apply` on a copy produced independent conservation evidence
`ENTRIES_TOTAL=4 IN_SLIM=2 IN_ARCHIVE=2 IN_BOTH=0` with `SLIM_STARTS_WITH_PREAMBLE=True`.

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

## Coverage: the invocation that actually produces a report (measured 2026-10-07)

> **Why this section exists**: `Directory.Build.targets` declares `CollectCoverage=true`,
> `CoverletOutputFormat=cobertura,json` and `CoverletOutput=$(MSBuildProjectDirectory)/TestResults/`,
> and both `coverlet.collector` and `coverlet.msbuild` are restored. Those settings are **not
> sufficient** - measured on 2026-10-07, the coverlet.msbuild path never runs.

```
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj -c Debug ^
  --collect:"XPlat Code Coverage" --results-directory <outputDir>
```

Measured evidence (same project, same machine, same day):

| invocation | coverage artifact |
|---|---|
| `dotnet test <proj>` (props already say `CollectCoverage=true`) | **none** (no `coverage.cobertura.xml`, no `coverage.json`, no `TestResults/`) |
| `dotnet test <proj> -p:CollectCoverage=true -p:CoverletOutputFormat=cobertura -v:n` | **none**; `GenerateCoverageResultAfterTest` appears **0 times** in 1121 verbose log lines |
| `dotnet test <proj> --collect:"XPlat Code Coverage" --results-directory <dir>` | **`coverage.cobertura.xml` (25,285,961 B)** |

So the working mechanism in this repo is `coverlet.collector` + `--collect`, **not** the msbuild properties.
Root cause of the msbuild path not running is **not established** (candidates: interaction with
`Microsoft.Testing.Platform 2.0.1` / `MSTest 4.0.1` / `Microsoft.NET.Test.Sdk 18.0.0`, property evaluation
order, `dotnet test` execution path). Do not assume it works just because the props and packages are present.

**Reading the report**: with no `--include`/`--exclude` filter the report aggregates every *loaded*
assembly, including ones the suite never exercises. Measured 2026-10-07 on `PuddingMemoryEngineTests`:

| package | line-rate | branch-rate |
|---|---|---|
| `PuddingMemoryEngine` | 0.6112 | 0.4545 |
| `PuddingCore` | 0.0682 | 0.0477 |
| `PuddingRuntime` | 0.0572 | 0.0397 |
| `PuddingFullTextIndex` | 0.0127 | 0.0079 |
| `PuddingCodeIndex` / `PuddingCodeIntelligence` / `PuddingPathFiltering` | 0 | 0 |

Aggregate root value was `line-rate=0.0883` / `branch-rate=0.06` (10533/119222 lines) - **do not use the
aggregate as a gate metric**; take the value per package, and freeze the invocation (including filters)
before comparing two runs.

## Environment note: `pwsh` does not exist on the Windows dev host (measured 2026-10-07)

Every recipe in this README (and the usage comments inside the scripts) is written as
`pwsh -File TestScripts\...`. Measured on this host on 2026-10-07:

```
Get-Command pwsh   -> not found
where.exe pwsh     -> (empty)
$PSVersionTable    -> 5.1.26100.9444, PSEdition = Desktop
```

So **use `powershell -File ...` when driving these scripts on this Windows box**; keep `pwsh` for
CI/Linux runners where it is the PowerShell name.

**Consequence inside the gate itself** - `test-pudding-suite-gates.ps1:516`:

```powershell
$pwshExe = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
& $pwshExe -NoProfile -File $operatorsGatePath -RepoRoot $repoRoot
```

`$IsWindows` is an automatic variable **only in PowerShell 6+**; under 5.1 it is `$null`, so this
resolves to `$PSHOME\pwsh` (`...\WindowsPowerShell\v1.0\pwsh`) - a path that does not exist, because
`pwsh` is not installed at all. **The OperatorsGate step therefore cannot be launched on this host.**

This is reported, **not patched**: pointing the launcher at `powershell.exe` would change which engine
runs a gate (and the exit-code-reliability measurements above were taken under `pwsh`), so the switch
needs an explicit decision from the gate's owner rather than a silent edit.

---

## ⚠️ 已知缺陷：**4/18 脚本在 Windows PowerShell 5.1 下无法解析**（2026-10-08 实测）

**根因（已证，非猜测）**：这些脚本是 **UTF-8 无 BOM**、正文含中文；**PowerShell 5.1 在没有 BOM 时按 ANSI(GBK) 解码**，中文字节吃掉了字符串字面量的闭合引号 ⇒ **解析失败**。

| 脚本 | 按字节读（5.1 默认） | 按 UTF-8 文本读 | 结论 |
|---|---|---|---|
| `check-circular-deps.ps1` | **4 个语法错误** | **0** | **不可执行**（实跑已证：`exit -1`，ParserError） |
| `report-skill-portfolio.ps1` | **191** | **0** | **不可执行** |
| `test-platform-api.ps1` | **11** | **0** | **不可执行** |
| **`test-pudding-suite-gates.ps1`** | **31** | **0** | **不可执行（套件身份门禁）** |
| `test-operators-architecture-gates.ps1` | 0 | 0 | 可解析 |
| `test-capability-channel-window.ps1`（**全仓唯一带 BOM**） | 0 | 0 | 可执行 |

> **"按 UTF-8 文本读 = 0 错误"是关键判别**：它证明失败**纯粹是编码**，**不存在 PS7 专有语法** ⇒ **修法就是加 BOM**（3 字节），不需要改代码。

**实测复现（只解析、不执行，无副作用）**：
```powershell
powershell -NoProfile -File TestScripts\check-circular-deps.ps1   # => ParserError / exit -1

Get-ChildItem TestScripts\*.ps1 | ForEach-Object {
  $e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$null,[ref]$e)
  '{0,-46} errors={1}' -f $_.Name, @($e).Count }

# 判别根因（编码 vs 语法）：
$t=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8); $e=$null
[void][System.Management.Automation.Language.Parser]::ParseInput($t,[ref]$null,[ref]$e); @($e).Count   # => 0
```

**修法（3 字节/文件；未由本代理执行，留待 owner 决定）**：
```powershell
foreach($n in @('check-circular-deps.ps1','report-skill-portfolio.ps1','test-platform-api.ps1','test-pudding-suite-gates.ps1')){
  $p = Join-Path 'TestScripts' $n; $b = [IO.File]::ReadAllBytes($p)
  if (-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) {
    [IO.File]::WriteAllBytes($p, ([byte[]](0xEF,0xBB,0xBF)) + $b); "fixed $n" } }
```

### `check-circular-deps.ps1` 的另外四个独立缺陷（**修好编码也不可用**）

1. `$cyclesFound` **从不递增** ⇒ 结尾永远打印"未发现循环依赖" ⇒ **假 PASS**（最危险的一条）。
2. `$root` 多剥了一层目录（3× `Split-Path -Parent`）⇒ `$sourceDir` 指向仓库**外**（实测 `Test-Path D:\CodeProject\PuddingAgent\Source` = **False**）。
3. 只扫 `Source\`，**不覆盖 `Tests\`**（仓库级还有 ≥5 个测试工程）。
4. `Split-Path -LeafBase` 是 **PS 6+ 参数**（本机实测报"找不到与参数名称 LeafBase 匹配的参数"）。
5. **无退出码约定** ⇒ 不能作为 CI 判据（无论好坏都返回 0/解析错误）。

### ✅ 替代实现：`check-project-layering.ps1`（新增 2026-10-08）

- **C-1**：全仓库 `*.csproj` 依赖图**无环**（覆盖 `Source` + `Tests`）。
- **C-4**：**生产工程不得引用测试工程**（`*Tests`）。
- `-SelfTest`：**变异自检**——注入 ①合成环 ②合成 `生产 -> 测试` 引用，断言检查器**必须报错**（对应标准 §4"故意反向依赖能被捕获"的验收精神）。
- **退出码（fail-closed）**：`0`=PASS · `1`=FAIL · `3`=INSTRUMENT_FAILURE（枚举到 0 个工程时**拒绝报绿**）· `4`=SELFTEST_FAIL。
- **ASCII-only**：从设计上免疫上述编码缺陷（本文件不含任何非 ASCII 字节）。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File TestScripts\check-project-layering.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File TestScripts\check-project-layering.ps1 -SelfTest
```

**实测输出（2026-10-08，本机 PS 5.1；两次运行均 exit 0）**：
```
PROJECT_COUNT = 81        EDGE_COUNT = 131
EXTERNAL_TREE_PROJECTS = 4 (vendored; still checked by C-1/C-4; governance undecided: D-5)
  ext: github.hyfree.GM / github.hyfree.GM.ConsoleApp / github.hyfree.GMTests / PerformanceTest
PASS C-1: dependency graph is acyclic
PASS C-4: no production project references a test project
INFO: 2 edge(s) into undecided fixture/probe projects (D-6, NOT enforced):
  PuddingDesktop.Tests -> PuddingDesktop.WpfArchive
  PuddingPlatformTests -> Mcp.Cli
RESULT: PASS
```
- 变异自检（`-SelfTest`）：`SELFTEST ok: injected cycle detected -> __ZZ_CycleA -> __ZZ_CycleB -> __ZZ_CycleA` · `SELFTEST ok: injected production->test reference detected` · `SELFTEST: PASS`。
- 本文件自身：**`NONASCII=0` / `PARSE_ERRORS=0`** ⇒ 从设计上免疫上述编码缺陷。
- **新发现（D-5 的证据）**：`github.hyfree.GM` 等 **4 个工程位于仓库 `external\` 目录树内** ⇒ 它们是**内嵌的外部源码树**，不是普通本仓工程；治理口径（是否纳入层级/边界断言）需人工裁决。
