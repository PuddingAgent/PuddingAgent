#Requires -Version 5.1
<#
.SYNOPSIS
    Report and optionally remove stale browser profile directories in a workspace.

.DESCRIPTION
    These directories look like leftover junk, but on 2026-11-18 one of them was being
    written to while being inspected: .edgeprobe had files modified seconds earlier and
    held open handles, because a live Edge instance was still using it as its profile.
    Deleting it would have broken something that was running.

    So this script refuses to treat "old-looking" as "dead". A directory is only a
    removal candidate when BOTH hold:
      1. its newest file is older than -MinIdleHours, and
      2. none of its most recent files can be opened exclusively.

    Dry-run is the default. Pass -Apply to delete candidates.

.PARAMETER Root
    Directory to scan. Defaults to the default workspace.

.PARAMETER Pattern
    Directory name pattern to consider.

.PARAMETER MinIdleHours
    A directory must have been untouched for at least this long.

.PARAMETER Samples
    How many of the newest files to test for an exclusive lock.

.EXAMPLE
    clean-stale-profiles.ps1
    Reports what would be removed.

.EXAMPLE
    clean-stale-profiles.ps1 -Apply
    Removes the candidates.

.NOTES
    ASCII-only on purpose: Windows PowerShell 5.1 mis-decodes UTF-8 without BOM.
#>
[CmdletBinding()]
param(
    [string]$Root = 'D:\Data\workspaces\default',
    [string]$Pattern = '.edgeprobe*',
    [int]$MinIdleHours = 24,
    [int]$Samples = 5,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Root)) { throw "Root not found: $Root" }

function Test-ExclusiveAccess {
    param([string]$Path)
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $stream.Close()
        $stream.Dispose()
        return $true
    }
    catch {
        return $false
    }
}

$mode = if ($Apply) { 'APPLY' } else { 'DRY-RUN' }
Write-Output "=== clean-stale-profiles  mode=$mode  pattern=$Pattern  minIdleHours=$MinIdleHours ==="
Write-Output "ROOT=$Root"
Write-Output ''

$dirs = Get-ChildItem $Root -Directory -Filter $Pattern -ErrorAction SilentlyContinue
if (-not $dirs) {
    Write-Output 'No matching directories.'
    exit 0
}

$candidates = @()
$kept = @()

foreach ($d in $dirs) {
    $files = Get-ChildItem $d.FullName -Recurse -File -ErrorAction SilentlyContinue
    if (-not $files) {
        Write-Output ("[EMPTY] {0}: no files" -f $d.Name)
        continue
    }

    $totalMb = [Math]::Round((($files | Measure-Object -Sum Length).Sum) / 1MB, 1)
    $newest = ($files | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
    $idleHours = [Math]::Round(((Get-Date) - $newest.LastWriteTime).TotalHours, 1)

    $locked = 0
    foreach ($f in ($files | Sort-Object LastWriteTime -Descending | Select-Object -First $Samples)) {
        if (-not (Test-ExclusiveAccess $f.FullName)) { $locked++ }
    }

    $reason = $null
    if ($locked -gt 0) { $reason = "in use ($locked of $Samples recent files locked)" }
    elseif ($idleHours -lt $MinIdleHours) { $reason = "touched $idleHours h ago (< $MinIdleHours)" }

    if ($reason) {
        Write-Output ("[KEEP]  {0}: {1} MB, idle={2}h -- {3}" -f $d.Name, $totalMb, $idleHours, $reason)
        $kept += $d.Name
    }
    else {
        Write-Output ("[CAND]  {0}: {1} MB, idle={2}h, locked=0" -f $d.Name, $totalMb, $idleHours)
        $candidates += $d
    }
}

Write-Output ''
$candMb = 0
foreach ($d in $candidates) {
    $candMb += ((Get-ChildItem $d.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Sum Length).Sum) / 1MB
}
Write-Output ("CANDIDATES={0}  RECLAIMABLE_MB={1}" -f $candidates.Count, [Math]::Round($candMb, 1))
Write-Output ("KEPT={0}" -f $kept.Count)

if (-not $Apply) {
    if ($candidates.Count -gt 0) { Write-Output 'Re-run with -Apply to delete the candidates.' }
    exit 0
}

$failed = 0
foreach ($d in $candidates) {
    try {
        Remove-Item $d.FullName -Recurse -Force -ErrorAction Stop
        Write-Output ("[DELETED] {0}" -f $d.Name)
    }
    catch {
        $failed++
        Write-Output ("[FAILED] {0}: {1}" -f $d.Name, $_.Exception.Message)
    }
}

if ($failed -gt 0) { exit 1 }
exit 0
