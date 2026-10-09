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
    [string[]]$TestPath = @()
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
        foreach ($project in $TestPath) {
            Write-Host "--- $project"
            & dotnet test $project --nologo --no-restore
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "REFUSING TO COMMIT: tests failed for $project (exit $LASTEXITCODE)."
                exit $LASTEXITCODE
            }
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
