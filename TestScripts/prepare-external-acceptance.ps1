<#
.SYNOPSIS
Prepare everything the OUTSIDE acceptor needs, in the verified order, with one command.

.DESCRIPTION
The internal side can only deliver `ready-for-external-deploy`; startup / restart /
crash-recovery / exit-reclamation and the real-device smoke items must be judged outside
the product. This script performs the preparation steps that ARE verifiable internally,
in the order that is known to work:

  1. build the launcher exe that TestScripts/test-pudding-desktop-launcher.ps1 expects by
     default (temp/build/winui3/bin/PuddingDesktop/debug_win-x64/PuddingDesktop.exe);
  2. produce a delivery manifest (TestScripts/delivery-manifest.ps1);
  3. optionally repeat the whole chain N times (TestScripts/run-chain-n-times.ps1) for the
     20x gate;
  4. write the external facts skeleton (TestScripts/external-acceptance.ps1 -Template).

It does NOT and cannot produce the verdict: after this script, an operator performs the
real-device observations, fills the facts file, and runs

    TestScripts/external-acceptance.ps1 -SkipTemplate -Conclude -Facts <facts>

ASCII-only on purpose: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so non-ASCII
literals here would be a parse error.

.EXAMPLE
powershell -File TestScripts/prepare-external-acceptance.ps1
powershell -File TestScripts/prepare-external-acceptance.ps1 -Times 20 -BuildIdPrefix ext-20x
#>
[CmdletBinding()]
param(
    [string]$BuildId = "ext-" + (Get-Date -Format "yyyyMMdd-HHmmss"),
    [int]$Times = 1,
    [string]$BuildIdPrefix = "ext-20x",
    [string]$Facts = "temp/test-out/external-facts.json",
    [switch]$SkipLauncherBuild,
    [switch]$SkipFactsTemplate
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    $launcher = "temp/build/winui3/bin/PuddingDesktop/debug_win-x64/PuddingDesktop.exe"

    Write-Host "=== step 1/4: launcher exe (needed by test-pudding-desktop-launcher.ps1)"
    if ($SkipLauncherBuild) {
        Write-Host "    skipped by request"
    } elseif (Test-Path $launcher) {
        Write-Host "    already present: $launcher"
    } else {
        & dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --artifacts-path temp/build/winui3 --nologo
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $launcher)) {
            Write-Warning "LAUNCHER-BUILD-FAILED: $launcher was not produced."
            exit 1
        }
        Write-Host "    built: $launcher"
    }

    Write-Host "=== step 2/4: delivery manifest"
    if ($Times -le 1) {
        Remove-Item "temp/test-out/delivery-manifest.json" -Force -ErrorAction SilentlyContinue
        & "$PSScriptRoot/delivery-manifest.ps1" -BuildId $BuildId
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "CHAIN-FAILED: the delivery chain did not produce an acceptable manifest."
            exit 1
        }
    } else {
        Write-Host "    delegated to run-chain-n-times.ps1 -Times $Times (it deletes the stale manifest per run,"
        Write-Host "    verifies buildId, keeps <buildId>.json/.log under temp/test-out/manifests)"
        & "$PSScriptRoot/run-chain-n-times.ps1" -Times $Times -BuildIdPrefix $BuildIdPrefix
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "CHAIN-FAILED: see the FLAKE-STOP evidence above (temp/test-out/manifests)."
            exit 1
        }
    }

    Write-Host "=== step 3/4: manifest checkpoint"
    $manifest = "temp/test-out/delivery-manifest.json"
    if (Test-Path $manifest) {
        $json = Get-Content $manifest -Raw -Encoding UTF8
        $stage = [regex]::Match($json, '"stage":\s*"([^"]*)"').Groups[1].Value
        $commit = [regex]::Match($json, '"commit":\s*"([^"]*)"').Groups[1].Value
        $build = [regex]::Match($json, '"buildId":\s*"([^"]*)"').Groups[1].Value
        Write-Host "    buildId=$build commit=$commit stage=$stage"
        if ($stage -ne "ready-for-external-deploy") {
            Write-Warning "UNEXPECTED-STAGE: '$stage' (expected ready-for-external-deploy)."
            exit 1
        }
    } else {
        Write-Warning "NO-MANIFEST: temp/test-out/delivery-manifest.json is missing."
        exit 1
    }

    Write-Host "=== step 4/4: external facts skeleton"
    if ($SkipFactsTemplate) {
        Write-Host "    skipped by request"
    } else {
        & "$PSScriptRoot/external-acceptance.ps1" -Template -Facts $Facts
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "FACTS-TEMPLATE-FAILED."
            exit 1
        }
    }

    Write-Host ""
    Write-Host "=== READY FOR THE OUTSIDE ACCEPTOR"
    Write-Host "  1. perform each real-device observation in Docs/14_reports/2026-10-09-*.md"
    Write-Host "     (S-01..S-13 plus startup / restart / crash-recovery / exit-reclamation);"
    Write-Host "  2. fill $Facts (outcome must be one of: passed | failed | not-run);"
    Write-Host "  3. conclude:  powershell -File TestScripts/external-acceptance.ps1 -SkipTemplate -Conclude -Facts $Facts"
    Write-Host "     exit 0 = external-lifecycle-verified allowed; exit 2 = refused with concrete reasons."
    Write-Host "  Reminder: 250ms scanning budget is a PENDING candidate - calibrate it on a real"
    Write-Host "  WinUI/WebView2 runtime (see the handoff section 12); unit tests must not claim it."
}
finally {
    Pop-Location
}
