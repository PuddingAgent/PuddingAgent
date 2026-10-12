<#
.SYNOPSIS
    Commit a round ONLY after the gates pass. Exists because I ignored a failing gate twice in a row.

.DESCRIPTION
    Why this exists (2026-10-08): twice in consecutive rounds I ran check-round-gates.ps1, SAW it exit 1
    ("broken targets 35 > baseline 32"), and committed anyway -- both times the regression was mine.
    Knowing the rule was not enough; the order of operations had to change. So this script *is* the
    order: gates first, commit only if they pass.

    Usage:
      powershell -File TestScripts/commit-round.ps1 -Message "..." -Paths path1,path2 [ -TestPath <csproj> ]

    -TestPath is optional and repeated: any project given here is tested (dotnet test) before committing.

.EXAMPLE
    powershell -File TestScripts/commit-round.ps1 -Message "docs: ..." -Paths Docs/00_changelog/...md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Message,
    [Parameter(Mandatory = $true)][string[]]$Paths,
    [string[]]$TestPath = @(),
    # Retry a failing suite ONCE, but only when every failing test is on TestScripts/known-flaky.txt.
    # Unknown instability still refuses: the retry is a classification, not a way to ignore red.
    [ValidateRange(0, 2)][int]$RetryKnownFlakes = 0
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

try {
    Write-Host "=== step 1/3: documentation gates (totals only)"
    & (Join-Path $PSScriptRoot "check-round-gates.ps1") | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "REFUSING TO COMMIT: documentation gates reported a regression (exit $LASTEXITCODE)."
        exit $LASTEXITCODE
    }

    if ($TestPath.Count -gt 0) {
        Write-Host "=== step 2/3: tests"
        # dotnet writes test failures to stderr; with $ErrorActionPreference = "Stop" that becomes a
        # terminating error and the retry/classification code below would never run (found by running it).
        # Gate on exit codes instead (same reason as the git step).
        $ErrorActionPreference = "Continue"
        $registryPath = Join-Path $PSScriptRoot "known-flaky.txt"
        $knownNames = @()
        if (Test-Path $registryPath) {
            $knownNames = Get-Content $registryPath -Encoding UTF8 |
                Where-Object { $_ -and -not $_.TrimStart().StartsWith("#") } |
                ForEach-Object { $_.Trim() }
        }

        foreach ($project in $TestPath) {
            # 先判存在性：路径写错时 dotnet test 会 MSB1009，若直接当"tests failed"会误导（曾把 Source/ 写成 Tests/）。
            if (-not (Test-Path $project)) {
                Write-Warning "REFUSING TO COMMIT: test project not found: $project"
                exit 1
            }
            Write-Host "--- $project"
            $suiteName = [System.IO.Path]::GetFileNameWithoutExtension($project)
            $trxDir = Join-Path $repositoryRoot "temp/test-out/trx"
            New-Item -ItemType Directory -Force -Path $trxDir | Out-Null

            # Write a TRX for the FIRST attempt: classification needs the first failures
            # (a successful retry TRX contains no failing test names).
            & dotnet test $project --nologo --no-restore --logger "trx;LogFileName=$suiteName.first.trx" --results-directory $trxDir
            if ($LASTEXITCODE -eq 0) { continue }

            if ($RetryKnownFlakes -le 0) {
                Write-Warning "REFUSING TO COMMIT: tests failed for $project (exit $LASTEXITCODE)."
                exit $LASTEXITCODE
            }

            $firstTrx = Join-Path $trxDir "$suiteName.first.trx"
            $failedNames = @()
            if (Test-Path $firstTrx) {
                $trx = Get-Content $firstTrx -Raw -Encoding UTF8
                $failedNames = [regex]::Matches($trx, '<UnitTestResult\b[^>]*outcome="Failed"[^>]*testName="([^"]+)"') |
                    ForEach-Object { $_.Groups[1].Value } |
                    Sort-Object -Unique
            }

            if ($failedNames.Count -eq 0) {
                Write-Warning "REFUSING TO COMMIT: $suiteName failed once but its TRX names no failing test (cannot classify)."
                exit 1
            }

            Write-Host "--- retrying $suiteName once (first attempt failed: $($failedNames -join ', '))"
            & dotnet test $project --nologo --no-restore
            $retryExit = $LASTEXITCODE

            if ($retryExit -ne 0) {
                Write-Warning "REFUSING TO COMMIT: $suiteName still fails on retry (exit $retryExit)."
                exit $retryExit
            }

            $unknown = @($failedNames | Where-Object { $knownNames -notcontains $_ })
            if ($unknown.Count -eq 0) {
                Write-Warning ("known flake absorbed by retry ({0}): {1}" -f $failedNames.Count, ($failedNames -join ", "))
                continue
            }

            Write-Warning "REFUSING TO COMMIT: the first attempt failed with NEW INSTABILITY (not on TestScripts/known-flaky.txt):"
            $unknown | ForEach-Object { Write-Warning ("  " + $_) }
            exit 1
        }
    }
    else {
        Write-Host "=== step 2/3: tests skipped (no -TestPath given)"
    }

    Write-Host "=== step 3/3: stage + commit"
    # Native tools write warnings to stderr; with $ErrorActionPreference = "Stop" PowerShell turns that
    # into a terminating error. Gate on exit codes instead (learned the hard way in round 92).
    $ErrorActionPreference = "Continue"

    & git add -- $Paths
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "git add failed"
        exit $LASTEXITCODE
    }

    $staged = & git diff --cached --name-only
    if (-not $staged) {
        Write-Warning "nothing staged; refusing to create an empty commit"
        exit 1
    }
    Write-Host "staged files:"
    $staged | ForEach-Object { Write-Host "  $_" }

    & git commit -m $Message --quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "git commit failed"
        exit $LASTEXITCODE
    }

    Write-Host "=== committed:"
    & git log -1 --format="%h %s"
    exit 0
}
finally {
    Pop-Location
}
