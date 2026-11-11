<#
.SYNOPSIS
    Pre-commit gate: run the tests that actually reference the C# types you staged, and block the commit
    when a NEW test fails.

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
    [switch]$ListOnly
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

try {
    function Get-ChangedCsharpFiles {
        param([bool]$All)

        $lines = if ($All) {
            & git diff --name-only --diff-filter=ACMR HEAD
        }
        else {
            & git diff --cached --name-only --diff-filter=ACMR
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

        if ($Names.Count -eq 0) { return @() }
        $directory = Split-Path -Parent $ProjectPath
        $sources = Get-ChildItem -Path $directory -Recurse -Filter "*.cs" -File -ErrorAction SilentlyContinue
        $hits = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($source in $sources) {
            $text = Get-Content $source.FullName -Raw -Encoding UTF8
            foreach ($name in $Names) {
                # Word-boundary match: "Foo" must not match "FooBar".
                if ([regex]::IsMatch($text, ('\b' + [regex]::Escape($name) + '\b'))) { [void]$hits.Add($name) }
            }
        }
        @($hits)
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
        param([string]$ProjectPath, [string]$Filter, [string]$ResultsDirectory)

        $assembly = Get-BuiltTestAssembly -ProjectPath $ProjectPath
        if ($null -eq $assembly) {
            return [pscustomobject]@{ Skipped = $true; Failed = @(); Reason = 'no built test assembly found'; Runner = 'mtp' }
        }

        $before = @(Get-ChildItem $ResultsDirectory -Filter *.trx -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
        $output = & dotnet $assembly.FullName --filter $Filter --report-trx --results-directory $ResultsDirectory 2>&1
        $exitCode = $LASTEXITCODE
        $fresh = @(Get-ChildItem $ResultsDirectory -Filter *.trx -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName } | Where-Object { $before -notcontains $_ })

        if ($fresh.Count -eq 0) {
            Write-Host ($output | Select-Object -Last 20 | Out-String)
            return [pscustomobject]@{ Skipped = $true; Failed = @(); Reason = "mtp runner exited $exitCode without a trx"; Runner = 'mtp' }
        }

        [xml]$trx = Get-Content $fresh[0] -Raw -Encoding UTF8
        $failed = @($trx.TestRun.Results.UnitTestResult |
            Where-Object { $_.outcome -eq "Failed" } |
            ForEach-Object { [string]$_.testName })

        [pscustomobject]@{
            Skipped  = $false
            ExitCode = $exitCode
            Failed   = $failed
            Trx      = $fresh[0]
            Runner   = 'mtp'
        }
    }

    function Invoke-TestProject {
        param([string]$ProjectPath, [string]$Filter, [string]$ResultsDirectory)

        $trxName = [guid]::NewGuid().ToString("N")
        $output = & dotnet test $ProjectPath --nologo -v q --filter $Filter `
            --logger "trx;LogFileName=$trxName.trx" --results-directory $ResultsDirectory 2>&1
        $exitCode = $LASTEXITCODE

        $trxPath = Join-Path $ResultsDirectory "$trxName.trx"
        if (-not (Test-Path $trxPath)) {
            $joined = ($output | Out-String)
            if ($joined -match 'VSTest target is no longer supported') {
                return Invoke-MtpTestProject -ProjectPath $ProjectPath -Filter $Filter -ResultsDirectory $ResultsDirectory
            }
            # A filter that matches zero tests makes dotnet test exit 1 WITHOUT a TRX, which is not a
            # build failure. Distinguish the two: a real build failure always prints 'error CSxxxx' /
            # 'error MSBxxxx'. Language-independent on purpose (ASCII-only script, zh/en host locales).
            if ($joined -notmatch 'error\s+(CS|MSB|NU)\d+') {
                return [pscustomobject]@{ Skipped = $true; Failed = @(); Reason = 'no test matched the filter'; Runner = 'vstest' }
            }

            Write-Host ($output | Select-Object -Last 30 | Out-String)
            throw "dotnet test for $ProjectPath exited $exitCode and produced no TRX log; fix the build first"
        }

        [xml]$trx = Get-Content $trxPath -Raw -Encoding UTF8
        $failed = @($trx.TestRun.Results.UnitTestResult |
            Where-Object { $_.outcome -eq "Failed" } |
            ForEach-Object { [string]$_.testName })

        [pscustomobject]@{
            Skipped  = $false
            ExitCode = $exitCode
            Failed   = $failed
            Trx      = $trxPath
            Runner   = 'vstest'
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

    $testProjects = Get-TestProjects
    if ($testProjects.Count -eq 0) {
        Write-Warning "no *Tests.csproj found under Tests/; the scan root may be wrong"
        exit 1
    }

    $selection = @{}
    $uncovered = @()

    foreach ($file in $changed) {
        $names = Get-DeclaredTypeNames -RelativePath $file
        if ($names.Count -eq 0) { continue }

        $matchedAny = $false
        foreach ($project in $testProjects) {
            $hits = Test-ProjectMentionsAny -ProjectPath $project.FullName -Names $names
            if ($hits.Count -eq 0) { continue }
            $matchedAny = $true
            if (-not $selection.ContainsKey($project.FullName)) {
                $selection[$project.FullName] = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
            }
            foreach ($hit in $hits) { [void]$selection[$project.FullName].Add($hit) }
        }

        if (-not $matchedAny) { $uncovered += $file }
    }

    if ($selection.Count -eq 0) {
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

    # --- run ------------------------------------------------------------------------------------
    $resultsDirectory = Join-Path $repositoryRoot "temp\gate-trx"
    if (-not (Test-Path $resultsDirectory)) { New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null }

    $newFailures = @()
    $ranProjects = 0
    $skippedProjects = @()

    foreach ($project in ($selection.Keys | Sort-Object)) {
        $types = @($selection[$project] | Sort-Object)
        $filter = ($types | ForEach-Object { "FullyQualifiedName~$_" }) -join "|"
        Write-Host ("--- {0}  filter~{1}" -f (Resolve-Path -Relative $project), ($types -join "|"))

        $result = Invoke-TestProject -ProjectPath $project -Filter $filter -ResultsDirectory $resultsDirectory
        if ($null -eq $result) { continue }
        if ($result.Skipped) {
            Write-Host ("    SKIPPED ({0}): {1}" -f $result.Runner, $result.Reason)
            $skippedProjects += [pscustomobject]@{ Project = $project; Reason = $result.Reason; Runner = $result.Runner }
            continue
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
    if ($skippedProjects.Count -gt 0) {
        # A skipped project is not evidence of anything: surface it separately so a green verdict
        # is never read as "these tests passed".
        Write-Host ("=== NOT VERIFIED: {0} test project(s) skipped" -f $skippedProjects.Count)
        foreach ($skipped in $skippedProjects) {
            Write-Host ("    {0} [{1}]: {2}" -f (Resolve-Path -Relative $skipped.Project), $skipped.Runner, $skipped.Reason)
        }
    }

    if ($newFailures.Count -gt 0) {
        Write-Warning ("BLOCKED: {0} new test failure(s)" -f $newFailures.Count)
        foreach ($name in $newFailures) { Write-Warning ("    {0}" -f $name) }
        Write-Warning "If a failure is genuinely pre-existing and unrelated, add it to $BaselinePath with a dated reason."
        exit 1
    }

    if ($skippedProjects.Count -gt 0) {
        Write-Host "=== OK among projects that ran (skipped projects were not verified)"
        exit 0
    }

    Write-Host "=== OK: no new test failures"
    exit 0
}
finally {
    Pop-Location
}
