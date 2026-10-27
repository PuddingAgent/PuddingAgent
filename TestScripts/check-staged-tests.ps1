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
        $pattern = '\b(?:class|record|interface|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)'
        @([regex]::Matches($text, $pattern) | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    }

    function Get-TestProjects {
        @(Get-ChildItem -Path Tests -Recurse -Filter "*Tests.csproj" -File -ErrorAction SilentlyContinue |
            Sort-Object FullName)
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

    function Invoke-TestProject {
        param([string]$ProjectPath, [string]$Filter, [string]$ResultsDirectory)

        $trxName = [guid]::NewGuid().ToString("N")
        $output = & dotnet test $ProjectPath --nologo -v q --filter $Filter `
            --logger "trx;LogFileName=$trxName.trx" --results-directory $ResultsDirectory 2>&1
        $exitCode = $LASTEXITCODE

        $trxPath = Join-Path $ResultsDirectory "$trxName.trx"
        if (-not (Test-Path $trxPath)) {
            Write-Host ($output | Select-Object -Last 30 | Out-String)
            throw "dotnet test for $ProjectPath exited $exitCode and produced no TRX log; fix the build first"
        }

        [xml]$trx = Get-Content $trxPath -Raw -Encoding UTF8
        $failed = @($trx.TestRun.Results.UnitTestResult |
            Where-Object { $_.outcome -eq "Failed" } |
            ForEach-Object { [string]$_.testName })

        [pscustomobject]@{
            ExitCode = $exitCode
            Failed   = $failed
            Trx      = $trxPath
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

    foreach ($project in ($selection.Keys | Sort-Object)) {
        $types = @($selection[$project] | Sort-Object)
        $filter = ($types | ForEach-Object { "FullyQualifiedName~$_" }) -join "|"
        Write-Host ("--- {0}  filter~{1}" -f (Resolve-Path -Relative $project), ($types -join "|"))

        $result = Invoke-TestProject -ProjectPath $project -Filter $filter -ResultsDirectory $resultsDirectory
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
