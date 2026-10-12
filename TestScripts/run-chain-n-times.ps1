<#
.SYNOPSIS
Runs the delivery chain N times, keeping EVERY manifest and log as evidence (for the 20x flake gate).

.DESCRIPTION
Why this exists (2026-10-08): the external 20x gate needs one manifest + one log per iteration, and it must
stop at the FIRST failure with the evidence intact. Doing that by hand is easy to get wrong (people keep only
the last run). This script refuses to invent results: it deletes the previous manifest before each run, waits
for the chain to produce a NEW one, verifies its buildId matches the iteration, and copies it out.

The chain itself now starts by BUILDING THE WINUI SHELL (delivery-manifest.ps1 step 0/3) and refuses to emit a
manifest when the product shell does not build -- so a green manifest means "shell builds AND all suites pass".
#>
param(
    [int]$Times = 20,
    [string]$BuildIdPrefix = "ext-20x",
    [switch]$KeepOriginalManifest
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$chain = Join-Path $repo 'TestScripts/delivery-manifest.ps1'
$outDir = Join-Path $repo 'temp/test-out/manifests'
$manifest = Join-Path $repo 'temp/test-out/delivery-manifest.json'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$results = @()
for ($i = 1; $i -le $Times; $i++) {
    $buildId = "$BuildIdPrefix-$i"
    Write-Host ""
    Write-Host "=== iteration $i/$Times : buildId=$buildId ==="

    # Never read a stale manifest: remove it first, then require a NEW one.
    if (-not $KeepOriginalManifest) { Remove-Item $manifest -Force -ErrorAction SilentlyContinue }

    & $chain -BuildId $buildId *>&1 | Tee-Object -FilePath (Join-Path $outDir "$buildId.log") | Out-Null

    if (-not (Test-Path $manifest)) {
        Write-Host "FLAKE-STOP: iteration $i produced NO manifest (the chain refused to emit one)."
        Write-Host "Evidence: $(Join-Path $outDir "$buildId.log")"
        exit 1
    }

    $json = Get-Content $manifest -Raw
    $shown = ([regex]::Match($json, '"buildId":\s*"([^"]*)"')).Groups[1].Value
    if ($shown -ne $buildId) {
        Write-Host "FLAKE-STOP: manifest buildId='$shown' but this iteration is '$buildId' (stale or reused manifest)."
        exit 1
    }

    Copy-Item $manifest (Join-Path $outDir "$buildId.json") -Force
    $results += [pscustomobject]@{ Iteration = $i; BuildId = $shown; Stage = ([regex]::Match($json, '"stage":\s*"([^"]*)"')).Groups[1].Value }
}

Write-Host ""
Write-Host "=== $Times/$Times iterations produced a manifest each (evidence kept in temp/test-out/manifests) ==="
$results | Format-Table -AutoSize | Out-String | Write-Host