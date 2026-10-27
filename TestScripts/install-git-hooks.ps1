<#
.SYNOPSIS
    Install the repository git hooks (currently: pre-commit -> TestScripts/check-staged-tests.ps1).

.DESCRIPTION
    Hooks live in .git/hooks and are therefore NOT versioned, so every clone has to install them once.
    This script is idempotent and writes the hook body itself, which keeps the committed source of truth
    in TestScripts/ instead of in .git.

    The hook runs the staged-test gate and blocks the commit on a NEW test failure. Bypass with
    `git commit --no-verify` only when you accept that the gate did not run.

    ASCII-only on purpose: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.

.EXAMPLE
    powershell -File TestScripts/install-git-hooks.ps1
    powershell -File TestScripts/install-git-hooks.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$Remove
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

try {
    $hooksDirectory = & git rev-parse --git-path hooks
    if ($LASTEXITCODE -ne 0) { throw "not a git repository" }
    if (-not [System.IO.Path]::IsPathRooted($hooksDirectory)) {
        $hooksDirectory = Join-Path $repositoryRoot $hooksDirectory
    }
    if (-not (Test-Path $hooksDirectory)) {
        New-Item -ItemType Directory -Path $hooksDirectory -Force | Out-Null
    }

    $hookPath = Join-Path $hooksDirectory "pre-commit"

    if ($Remove) {
        if (Test-Path $hookPath) {
            Remove-Item $hookPath -Force
            Write-Host "removed $hookPath"
        }
        else {
            Write-Host "nothing to remove"
        }
        exit 0
    }

    # LF only: this file is executed by git's sh, not by PowerShell.
    $hookBody = @(
        '#!/bin/sh'
        '# Installed by TestScripts/install-git-hooks.ps1 -- do not edit here, edit TestScripts/.'
        'ROOT="$(git rev-parse --show-toplevel)"'
        'if command -v pwsh >/dev/null 2>&1; then'
        '  exec pwsh -NoProfile -File "$ROOT/TestScripts/check-staged-tests.ps1"'
        'elif command -v powershell >/dev/null 2>&1; then'
        '  exec powershell -NoProfile -ExecutionPolicy Bypass -File "$ROOT/TestScripts/check-staged-tests.ps1"'
        'else'
        '  echo "commit gate: neither pwsh nor powershell is on PATH; refusing to skip silently" >&2'
        '  exit 1'
        'fi'
        ''
    ) -join "`n"

    [System.IO.File]::WriteAllText($hookPath, $hookBody, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "installed $hookPath"
    Write-Host "verify with: git commit --dry-run (hook runs on real commits only)"
}
finally {
    Pop-Location
}
