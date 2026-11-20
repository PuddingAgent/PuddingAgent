<#
.SYNOPSIS
    Pre-commit gate: run the tests that actually reference the C# types you staged, and block the commit
    when a NEW test fails or a selected project does not produce a complete run.

.DESCRIPTION
    Why this exists (2026-10-27): commit 967da97 rewrote the text of
    HeartbeatPromptComposer.AutonomousExecutionContract and was committed with the only evidence being
    `dotnet build` exit 0. The build stayed green because the change compiles fine; an existing xUnit
    test (HeartbeatPromptComposerTests, present since 5199baf) was broken by it, and nobody noticed until
    the next session. Tool-level metrics could not catch it either: file_write / git_commit / build all
    report success, because they measure "did it throw", not "is it still correct".

    The gate is deliberately NOT "run everything and require green". This repository has 5 known failing
    tests in BrowserBridge (measured 2026-10-27, HEAD 51a84e3, 396 tests total). A gate demanding
    all-green would fail forever and get bypassed with --no-verify, which is worse than having no gate.
    So we ratchet: failures listed in the baseline file are tolerated, anything else blocks.

    Selection strategy (fast path): for every staged .cs file, extract declared type names, then pick
    test projects whose *sources* mention those names, and run only matching test classes.
    Limitation, stated on purpose: if a staged change breaks a test that never mentions the changed type
    name, this gate will not catch it. It is a fast first line, not a replacement for a full run.

    Result reading uses the TRX logger rather than scraping console text: a Theory failure prints
    `...Anchors(anchor: "work_summary") [FAIL]`, and a naive `\S+` capture silently mis-parses it.

    ASCII-only on purpose: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.

.PARAMETER BaselinePath
    JSON file listing known-failing fully-qualified test names (relative to the repository root).

.PARAMETER IncludeAllChanged
    Use every changed file (working tree + index) instead of only the staged ones. Useful for CI.

.PARAMETER ListOnly
    Print the selected test projects, filters and baseline, then exit 0 without running anything.

.EXAMPLE
    powershell -File TestScripts/check-staged-tests.ps1
    powershell -File TestScripts/check-staged-tests.ps1 -ListOnly
    powershell -File TestScripts/check-staged-tests.ps1 -IncludeAllChanged
#>
[CmdletBinding()]
param(
    [string]$BaselinePath = "TestScripts/staged-test-baseline.json",
    [switch]$IncludeAllChanged,
    [switch]$ListOnly,
    [switch]$AllowUnverifiedDeletions
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

try {
    function Get-ChangedCsharpFiles {
        param([bool]$All)

        $lines = if ($All) {
            & git diff --name-only --diff-filter=ACMRD HEAD
        }
        else {
            & git diff --cached --name-only --diff-filter=ACMRD
        }
        if ($LASTEXITCODE -ne 0) { throw "git diff failed with exit code $LASTEXITCODE" }

        @($lines | Where-Object { $_ -and $_.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase) })
    }

    # Deletions carry no type declarations, so they can never be mapped to a test class.
    # They are tracked separately to stop the gate from claiming a clean verdict over them.
    function Get-DeletedCsharpFiles {
        param([bool]$All)

        $lines = if ($All) {
            & git diff --name-only --diff-filter=D HEAD
        }
        else {
            & git diff --cached --name-only --diff-filter=D
        }
        if ($LASTEXITCODE -ne 0) { throw "git diff failed with exit code $LASTEXITCODE" }

        @($lines | Where-Object { $_ -and $_.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase) })
    }

    function Get-DeclaredTypeNames {
        param([string]$RelativePath)

        if (-not (Test-Path $RelativePath)) { return @() }
        $text = Get-Content $RelativePath -Raw -Encoding UTF8

        # String/comment blind spot (found 2026-10-30): the raw-text regex below also matched type
        # declarations living *inside string literals*. A characterization test that writes
        # "public class Alpha { }" as fixture source contributed phantom names Alpha/Beta, which then
        # selected 10 test projects; one of them had zero matching tests and dotnet test exits 1 without
        # a TRX, so the gate aborted on a filter that could never match. Strip literals and comments
        # before scanning declarations.
        $text = [regex]::Replace($text, '(?s)"{3,}.*?"{3,}', '""')                       # C# 11 raw strings
        $text = [regex]::Replace($text, '(?s)@"(?:[^"]|"")*"', '""')                   # verbatim strings
        $text = [regex]::Replace($text, '(?s)\$(?:@)?"(?:\\.|[^"\\])*"', '""')       # interpolated
        $text = [regex]::Replace($text, '(?s)"(?:\\.|[^"\\])*"', '""')               # regular
        $text = [regex]::Replace($text, '(?s)/\*.*?\*/', ' ')                              # block comments
        $text = [regex]::Replace($text, '(?m)//.*$', ' ')                                   # line comments

        $pattern = '\b(?:class|record|interface|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)'
        $names = @([regex]::Matches($text, $pattern) | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)

        # 单字母类型名是噪声（如容器文件里的占位类 A）：`\bA\b` 几乎和每个源文件都匹配，
        # 会把 28 个测试工程全部拉进选择面。一律排除。
        $names = @($names | Where-Object { $_.Length -gt 1 })

        # 选择面上限（2026-10-27）：一个文件声明很多类型时，把全部名字 OR 起来去匹配测试源
        # 会让过滤面爆炸。实测 FilePatchTool.cs（声明 ~10 个类型）令门禁跑了 30 分钟未完，
        # 提交因此被卡住。容器型文件优先用「文件名对应的主类型」，其余同名覆盖由该主类型
        # 的测试兜住；主类型不在声明列表里时只取前 $MaxSelectionTypeNames 个。
        $maxSelectionTypeNames = 3
        if ($names.Count -gt $maxSelectionTypeNames) {
            $primary = [IO.Path]::GetFileNameWithoutExtension($RelativePath)
            if ($names -contains $primary) { return @($primary) }
            return @($names | Select-Object -First $maxSelectionTypeNames)
        }

        return $names
    }

    function Get-TestProjects {
        # 测试工程分布在两个根：`Tests/`（宿主级集成测试）与 `Source/*Tests`（各组件单体测试）。
        # 只扫 `Tests/` 会漏掉 Source/PuddingRuntimeTests 这类工程 —— 2026-10-27 实测踩过：
        # 新增 Source/PuddingRuntimeTests/Tools/... 时本门禁选不到任何测试工程，静默放行。
        $roots = @("Tests", "Source") | Where-Object { Test-Path $_ }
        @($roots | ForEach-Object {
                Get-ChildItem -Path $_ -Recurse -Filter "*Tests.csproj" -File -ErrorAction SilentlyContinue
            } | Sort-Object FullName -Unique)
    }

    function Test-ProjectMentionsAny {
        param([string]$ProjectPath, [string[]]$Names)

        if ($Names.Count -eq 0) { return [pscustomobject]@{ Hits = @(); Types = @() } }
        $directory = Split-Path -Parent $ProjectPath
        $sources = Get-ChildItem -Path $directory -Recurse -Filter "*.cs" -File -ErrorAction SilentlyContinue
        $hits = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        $types = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($source in $sources) {
            $text = Get-Content $source.FullName -Raw -Encoding UTF8
            $mentioned = $false
            foreach ($name in $Names) {
                # Word-boundary match: "Foo" must not match "FooBar".
                if ([regex]::IsMatch($text, ('\b' + [regex]::Escape($name) + '\b'))) { [void]$hits.Add($name); $mentioned = $true }
            }
            # A test class that *uses* a changed type need not be named after it:
            # SlackGatewayTests constructs MessageGatewayIngress, and a filter of only
            # `FullyQualifiedName~MessageGatewayIngress` matched zero test classes -- the project was
            # selected but nothing ran (measured 2026-11-11). Collect the classes declared in the
            # mentioning files as well.
            if (-not $mentioned) { continue }
            # Only the class the [TestClass] attribute is attached to. Helper types declared inside
            # the same file (RecordingMessageSystem, TestContext, ...) are not test classes and would
            # otherwise pollute the filter with names that may match unrelated tests by substring.
            foreach ($m in [regex]::Matches($text, '\[TestClass\][\s\S]{0,300}?\b(?:class|record)\s+([A-Za-z_][A-Za-z0-9_]*)')) {
                if ($m.Groups[1].Value.Length -gt 1) { [void]$types.Add($m.Groups[1].Value) }
            }
        }
        [pscustomobject]@{ Hits = @($hits); Types = @($types) }
    }

    function Get-KnownFailures {
        param([string]$Path)

        if (-not (Test-Path $Path)) { return @() }
        $data = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        @($data.knownFailures)
    }

    function Get-BuiltTestAssembly {
        param([string]$ProjectPath)

        $directory = Split-Path -Parent $ProjectPath
        $bin = Join-Path $directory "bin"
        if (-not (Test-Path $bin)) { return $null }
        $expected = [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
        $candidates = @(Get-ChildItem $bin -Recurse -Filter "*.dll" -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\ref\\' })
        # Prefer the project's own assembly name; fall back to the newest runnable output that owns a deps.json.
        $named = @($candidates | Where-Object { $_.BaseName -eq $expected })
        if ($named.Count -gt 0) { return ($named | Sort-Object LastWriteTime -Descending | Select-Object -First 1) }
        $runnable = @($candidates | Where-Object {
                $deps = [IO.Path]::ChangeExtension($_.FullName, '.deps.json')
                (Test-Path $deps)
            })
        if ($runnable.Count -eq 0) { return $null }
        $runnable | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }

    # Microsoft.Testing.Platform projects reject the VSTest target on .NET 10 SDK+. Running the built
    # assembly directly keeps them gated instead of silently skipped, and --report-trx yields the same
    # TestRun/Results shape the caller already parses.
    function Invoke-MtpTestProject {
        param([string]$ProjectPath, [string]$Filter, [string]$ResultsDirectory, [switch]$AllowZero)

        $assembly = Get-BuiltTestAssembly -ProjectPath $ProjectPath
        if ($null -eq $assembly) {
            throw "MTP found no built test assembly for $ProjectPath"
        }

        $filterArgs = if ([string]::IsNullOrWhiteSpace($Filter)) { @() } else { @('--filter', $Filter) }
        $output = & dotnet $assembly.FullName @filterArgs --report-trx --results-directory $ResultsDirectory 2>&1
        $exitCode = $LASTEXITCODE
        $reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter *.trx -File)

        if ($reports.Count -ne 1) {
            Write-Host ($output | Select-Object -Last 20 | Out-String)
            throw "MTP for $ProjectPath exited $exitCode and produced $($reports.Count) TRX reports; expected one"
        }

        Read-TestRun -TrxPath $reports[0].FullName -ExitCode $exitCode -FailureExitCode 2 -AllowZero:$AllowZero
    }

    function Invoke-TestProjectOnce {
        param([string]$ProjectPath, [string]$Filter, [string]$ResultsDirectory, [switch]$AllowZero)

        # Opt-in: build into an isolated tree via PUDDING_GATE_ARTIFACTS. Without it the
        # default output directory is used, which is correct but unusable while the desktop
        # app is running: it locks temp/build/winui3 and every build fails with MSB3027, so
        # the gate cannot execute at all. Measured 2026-11-18, three separate times.
        $gateArtifacts = $env:PUDDING_GATE_ARTIFACTS
        $artifactsArgs = @()
        if (-not [string]::IsNullOrWhiteSpace($gateArtifacts)) {
            $artifactsArgs = @("-p:ArtifactsPath=$gateArtifacts")
        }

        $trxName = [guid]::NewGuid().ToString("N")
        $filterArgs = if ([string]::IsNullOrWhiteSpace($Filter)) { @() } else { @("--filter", $Filter) }
        $output = & dotnet test $ProjectPath --nologo -v q @filterArgs `
            --logger "trx;LogFileName=$trxName.trx" --results-directory $ResultsDirectory @artifactsArgs 2>&1
        $exitCode = $LASTEXITCODE

        $trxPath = Join-Path $ResultsDirectory "$trxName.trx"
        if (-not (Test-Path $trxPath)) {
            $joined = ($output | Out-String)
            if ($joined -match 'VSTest target is no longer supported') {
                return Invoke-MtpTestProject -ProjectPath $ProjectPath -Filter $Filter -ResultsDirectory $ResultsDirectory -AllowZero:$AllowZero
            }
            Write-Host ($output | Select-Object -Last 30 | Out-String)
            throw "dotnet test for $ProjectPath exited $exitCode and produced no TRX report"
        }

        Read-TestRun -TrxPath $trxPath -ExitCode $exitCode -FailureExitCode 1 -AllowZero:$AllowZero
    }

    # A filter derived from a changed file's type name frequently matches no test class at all (the
    # type is merely used, not named by the test). Hard-blocking on that made a legitimate change
    # uncommittable, and the obvious workaround -- deleting the check -- would reopen the silent
    # zero-execution hole. So: retry the whole project, which is strictly stronger than the empty
    # filtered run, and still block if the unfiltered run also executes nothing.
    function Invoke-TestProject {
        param([string]$ProjectPath, [string]$Filter, [string]$ResultsDirectory)

        $filtered = Invoke-TestProjectOnce -ProjectPath $ProjectPath -Filter $Filter -ResultsDirectory $ResultsDirectory -AllowZero
        if ($filtered.Executed -gt 0) { return $filtered }
        # Every test in the project is environment-gated, so the unfiltered rerun would be gated too.
        # Returning here keeps the report honest without a second useless run.
        if ($filtered.Gated) { return $filtered }

        Write-Host "    no test matched the derived filter; running the whole project instead"
        $unfilteredDirectory = Join-Path $ResultsDirectory "unfiltered"
        New-Item -ItemType Directory -Path $unfilteredDirectory -Force | Out-Null
        try {
            return Invoke-TestProjectOnce -ProjectPath $ProjectPath -Filter $null -ResultsDirectory $unfilteredDirectory
        }
        catch {
            # The fallback is best-effort evidence, not a selection: no test named this file, so its
            # failures were never in scope for this commit. Blocking here made pre-existing failures in
            # an unrelated suite uncommittable (measured 2026-11-18: one such failure in 419 tests).
            # A truly empty project still blocks, because that is the silent-zero case this gate exists
            # to catch.
            if ($_.Exception.Message -match 'zero tests executed') { throw }
            Write-Warning ("    unfiltered run reported problems (not blocking, filter never selected these): {0}" -f $_.Exception.Message)
            return [pscustomobject]@{ Executed = 0; Failed = @() }
        }
    }

    function Read-TestRun {
        param([string]$TrxPath, [int]$ExitCode, [int]$FailureExitCode, [switch]$AllowZero)

        [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw -Encoding UTF8
        $summary = $trx.TestRun.ResultSummary
        $counters = $summary.Counters
        $executed = 0
        $passed = 0
        $failedCount = 0
        if (-not $counters -or
            -not [int]::TryParse($counters.executed, [ref]$executed) -or
            -not [int]::TryParse($counters.passed, [ref]$passed) -or
            -not [int]::TryParse($counters.failed, [ref]$failedCount) -or
            $passed -lt 0 -or $failedCount -lt 0 -or
            $passed + $failedCount -ne $executed) {
            throw "TRX $TrxPath has missing or invalid execution counts (runner exit $ExitCode)"
        }

        # Environment gating is not the silent-zero hole (measured 2026-11-20).
        # MSTest 4 records an Assert.Inconclusive test as notExecuted, so a project whose every test
        # is gated by an environment variable yields total>0 / executed=0 / notExecuted=total,
        # ResultSummary outcome="Failed", plus an MTP run-level Error quoting exit 8. The previous
        # single condition folded that into "silent zero execution" and blocked the commit forever on
        # PuddingPluginAgentApi.IntegrationTests (4 tests, all require PUDDING_PLUGIN_WORKER).
        # The two cases must stay distinguishable: total=0 means nothing was ever discovered and still
        # blocks hard (that is the hole this gate exists for), while total>0 with nothing executed is an
        # environment gate and must be reported, not disguised as a failure verdict.
        $total = 0
        $notExecuted = 0
        $gatedRun = ([int]::TryParse($counters.total, [ref]$total)) -and
                    ([int]::TryParse($counters.notExecuted, [ref]$notExecuted)) -and
                    $total -gt 0 -and $executed -eq 0 -and $notExecuted -eq $total
        if ($gatedRun) {
            Write-Warning ("    environment-gated: {0}/{0} test(s) not executed (runner exit {1})" -f $total, $ExitCode)
            return [pscustomobject]@{ Executed = 0; Gated = $true; Total = $total; Failed = @() }
        }

        if ($executed -le 0) {
            if (-not $AllowZero) {
                throw "TRX $TrxPath has missing or invalid execution counts, or zero tests executed (runner exit $ExitCode)"
            }

            # AllowZero = 调用方接受「零匹配」并会回退到跑整个工程（见调用点的
            # "no test matched the derived filter" 分支）。
            # MTP 工程零匹配时退出码是 8（不是 VSTest 的 1），而 ResultSummary.outcome 仍是
            # Failed；只放过 counters 校验会让后面的 outcome 校验把它误判成「运行不完整」，
            # 从而永久挡住提交（实测 2026-11-21：PuddingAgentsTests 的 SubAgentPool 过滤器零匹配）。
            return [pscustomobject]@{ Executed = 0; Gated = $false; Total = 0; Failed = @() }
        }

        $incompleteCounters = @('error', 'timeout', 'aborted', 'passedButRunAborted', 'notRunnable', 'disconnected', 'inProgress', 'pending') |
            Where-Object { $counters.GetAttribute($_) -notin @('', '0') }
        # MTP's TRX reporter records its normal test-failure exit code as a run-level Error.
        # Ignore only that exact notice for exit 2; any other run-level error still blocks.
        $mtpFailureNotice = "Exit code indicates failure: '2'. Please refer to https://aka.ms/testingplatform/exitcodes for more information."
        $runErrors = @($summary.RunInfos.RunInfo | Where-Object {
            $_.outcome -in @('Error', 'Failed', 'Aborted', 'Timeout') -and
            -not ($FailureExitCode -eq 2 -and $ExitCode -eq 2 -and $_.outcome -eq 'Error' -and $_.Text -ceq $mtpFailureNotice)
        })
        $expectedOutcome = if ($failedCount -gt 0) { 'Failed' } else { 'Completed' }
        $expectedExit = if ($failedCount -gt 0) { $FailureExitCode } else { 0 }
        if ($summary.outcome -ne $expectedOutcome -or $ExitCode -ne $expectedExit -or $runErrors.Count -gt 0 -or $incompleteCounters) {
            throw "TRX $TrxPath did not report a complete run (outcome=$($summary.outcome), exit=$ExitCode, run errors=$($runErrors.Count), incomplete counters=[$($incompleteCounters -join ', ')])"
        }

        $results = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_ })
        $passedResults = @($results | Where-Object { $_.outcome -eq 'Passed' })
        $failures = @($results | Where-Object { $_.outcome -eq 'Failed' })
        if ($passedResults.Count -ne $passed -or $failures.Count -ne $failedCount -or
            @($failures | Where-Object { [string]::IsNullOrWhiteSpace($_.testName) }).Count -gt 0) {
            throw "TRX $TrxPath has incomplete test results"
        }

        [pscustomobject]@{
            Executed = $executed
            Failed   = @($failures | ForEach-Object { [string]$_.testName })
        }
    }

    # --- reference-direction config gate --------------------------------------------------------
    # Runs BEFORE the "no changed C# files" early-exit below: a csproj-only commit would otherwise
    # return green without ever reaching this check.
    #
    # Rationale: the PUDDING001 gate in Directory.Build.targets is opt-in -- deleting an
    # AllowedProjectReferences declaration silently disables it and no C# test would notice.
    # test-reference-direction-gate.ps1 pins the declared set against a registry, so a build-config
    # change must not be committable without it passing.
    $configDiffArgs = if ($IncludeAllChanged) {
        @('diff', '--name-only', '--diff-filter=ACMR', 'HEAD')
    }
    else {
        @('diff', '--cached', '--name-only', '--diff-filter=ACMR')
    }
    $configLines = & git @configDiffArgs
    if ($LASTEXITCODE -ne 0) { throw "git diff failed with exit code $LASTEXITCODE" }

    $configFiles = @($configLines | Where-Object {
            if (-not $_) { return $false }
            $normalized = $_.Replace('\', '/')
            $normalized.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase) -or
            $normalized.EndsWith('.props', [StringComparison]::OrdinalIgnoreCase) -or
            $normalized.EndsWith('.targets', [StringComparison]::OrdinalIgnoreCase) -or
            $normalized -eq 'TestScripts/test-reference-direction-gate.ps1'
        })

    if ($configFiles.Count -gt 0) {
        Write-Host ("=== reference-direction gate: {0} build config file(s) staged" -f $configFiles.Count)
        $gateScript = Join-Path $PSScriptRoot 'test-reference-direction-gate.ps1'
        if (-not (Test-Path $gateScript)) {
            Write-Warning "BLOCKED: build config staged but test-reference-direction-gate.ps1 is missing"
            exit 1
        }
        & $gateScript
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "BLOCKED: reference-direction gate failed (AllowedProjectReferences registry or contract guards)"
            exit 1
        }
    }

    # --- select ---------------------------------------------------------------------------------
    $changed = Get-ChangedCsharpFiles -All:$IncludeAllChanged
    if ($changed.Count -eq 0) {
        Write-Host "=== commit gate: no changed C# files, nothing to test"
        exit 0
    }
    Write-Host ("=== commit gate: {0} changed C# file(s)" -f $changed.Count)
    foreach ($file in $changed) { Write-Host ("    {0}" -f $file) }

    $deleted = Get-DeletedCsharpFiles -All:$IncludeAllChanged
    if ($deleted.Count -gt 0) {
        Write-Host ("=== deleted C# file(s) in this change: {0}" -f $deleted.Count)
        foreach ($file in $deleted) { Write-Host ("    - {0}" -f $file) }
    }

    $testProjects = Get-TestProjects
    if ($testProjects.Count -eq 0) {
        Write-Warning "no *Tests.csproj found under Tests/; the scan root may be wrong"
        exit 1
    }

    # Cap on extra "mentioning" test classes per project (see Test-ProjectMentionsAny).
    $MaxMentioningTypes = 40

    $selection = @{}
    $uncovered = @()

    foreach ($file in $changed) {
        $names = Get-DeclaredTypeNames -RelativePath $file
        if ($names.Count -eq 0) { continue }

        $matchedAny = $false
        foreach ($project in $testProjects) {
            $mentions = Test-ProjectMentionsAny -ProjectPath $project.FullName -Names $names
            if ($mentions.Hits.Count -eq 0) { continue }
            $matchedAny = $true
            if (-not $selection.ContainsKey($project.FullName)) {
                $selection[$project.FullName] = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
            }
            foreach ($hit in $mentions.Hits) { [void]$selection[$project.FullName].Add($hit) }
            # Bounded: a very common type makes many test classes mention it, and an unbounded OR
            # filter was already measured to blow up runtime (the FilePatchTool case).
            $extraTypes = @($mentions.Types | Sort-Object | Select-Object -First $MaxMentioningTypes)
            foreach ($type in $extraTypes) { [void]$selection[$project.FullName].Add($type) }
            if ($mentions.Types.Count -gt $extraTypes.Count) {
                Write-Host ("    (mentioning classes capped at {0} of {1})" -f $extraTypes.Count, $mentions.Types.Count)
            }
        }

        if (-not $matchedAny) { $uncovered += $file }
    }

    if ($selection.Count -eq 0) {
        if ($deleted.Count -gt 0 -and -not $AllowUnverifiedDeletions) {
            Write-Warning ("BLOCKED: {0} C# file(s) deleted and no test project maps to this change" -f $deleted.Count)
            Write-Warning "Deleting source can break tests that referenced it; this gate cannot map deletions to test classes."
            Write-Warning "Run the affected suite yourself, then re-run with -AllowUnverifiedDeletions."
            if ($uncovered.Count -gt 0) { Write-Warning ("uncovered changed file(s): {0}" -f ($uncovered -join ", ")) }
            exit 1
        }
        if ($deleted.Count -gt 0) {
            Write-Warning ("deletions NOT verified by tests: {0} file(s) (allowed explicitly)" -f $deleted.Count)
        }
        Write-Host "=== commit gate: no test source references the changed types; nothing to run"
        if ($uncovered.Count -gt 0) { Write-Warning ("uncovered changed file(s): {0}" -f ($uncovered -join ", ")) }
        exit 0
    }

    $knownFailures = Get-KnownFailures -Path $BaselinePath
    Write-Host ("=== known-failure baseline: {0} entr(ies) from {1}" -f $knownFailures.Count, $BaselinePath)
    if ($uncovered.Count -gt 0) { Write-Warning ("uncovered changed file(s): {0}" -f ($uncovered -join ", ")) }

    if ($ListOnly) {
        foreach ($project in ($selection.Keys | Sort-Object)) {
            Write-Host ("    RUN  {0}  filter~{1}" -f (Resolve-Path -Relative $project), (($selection[$project] | Sort-Object) -join "|"))
        }
        Write-Host "=== selection only (-ListOnly)"
        exit 0
    }

    if ($deleted.Count -gt 0) {
        Write-Warning ("deletions NOT covered by the selected tests: {0} file(s)" -f $deleted.Count)
    }

    # --- run ------------------------------------------------------------------------------------
    $runDirectory = Join-Path $repositoryRoot ('.work/staged-test-gate/' + [guid]::NewGuid().ToString('N'))

    $newFailures = @()
    $ranProjects = 0

    foreach ($project in ($selection.Keys | Sort-Object)) {
        $types = @($selection[$project] | Sort-Object)
        $filter = ($types | ForEach-Object { "FullyQualifiedName~$_" }) -join "|"
        Write-Host ("--- {0}  filter~{1}" -f (Resolve-Path -Relative $project), ($types -join "|"))

        $resultsDirectory = Join-Path $runDirectory ([string]($ranProjects + 1))
        New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
        try {
            $result = Invoke-TestProject -ProjectPath $project -Filter $filter -ResultsDirectory $resultsDirectory
        }
        catch {
            Write-Warning ("BLOCKED: {0}: {1}" -f (Resolve-Path -Relative $project), $_.Exception.Message)
            exit 1
        }
        if ($result.Gated) {
            Write-Host ("    environment-gated: {0} test(s) not executed" -f $result.Total)
        }
        else {
            Write-Host ("    executed {0} test(s)" -f $result.Executed)
        }
        $ranProjects++

        foreach ($name in $result.Failed) {
            if ($knownFailures -contains $name) {
                Write-Host ("    tolerated (baseline): {0}" -f $name)
            }
            else {
                Write-Host ("    NEW FAILURE: {0}" -f $name)
                $newFailures += $name
            }
        }
    }

    # --- verdict --------------------------------------------------------------------------------
    Write-Host ("=== ran {0} test project(s)" -f $ranProjects)
    if ($newFailures.Count -gt 0) {
        Write-Warning ("BLOCKED: {0} new test failure(s)" -f $newFailures.Count)
        foreach ($name in $newFailures) { Write-Warning ("    {0}" -f $name) }
        Write-Warning "If a failure is genuinely pre-existing and unrelated, add it to $BaselinePath with a dated reason."
        exit 1
    }

    Write-Host "=== OK: no new test failures"
    exit 0
}
finally {
    Pop-Location
}
