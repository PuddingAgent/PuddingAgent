#Requires -Version 7.0
<#
.SYNOPSIS
    Reclaim disk space consumed by per-task build artifacts under .work/ and by
    stale bin/obj directories.

.DESCRIPTION
    Every delivery in this repository copies a full set of build artifacts into
    .work/<task>/ (solution-artifacts/bin, gate-build/bin, ...). Nothing removes
    them afterwards. Measured 2026-11-11: .work had grown to 233 GB across
    ~170 task directories, and bin/obj added another 14 GB outside it.

    This script is DRY-RUN BY DEFAULT. It prints every directory it would delete
    and the total size, and deletes nothing unless -Apply is passed.

    Hard safety rules (never overridable):
      * Anything whose path contains \temp\build\winui3 is skipped: the running
        Core / Desktop / Guardian processes load assemblies from there.
      * D:\Data\databases is never touched (this script only looks at the repo).
      * Paths are matched case-insensitively against the protected list.

.PARAMETER MinAgeDays
    Only directories whose LastWriteTime is older than this many days are
    considered. Default 2. Use 0 to consider everything (still dry-run).

.PARAMETER Apply
    Actually delete. Without this switch the script only reports.

.PARAMETER RepoRoot
    Repository root. Defaults to D:\CodeProject\PuddingAgent\PuddingAgent.

.PARAMETER SkipBinObj
    Only consider .work/<task> directories; leave bin/obj alone.

.EXAMPLE
    pwsh -NoProfile -File TestScripts/clean-work-artifacts.ps1
    # dry run, shows what would be freed

.EXAMPLE
    pwsh -NoProfile -File TestScripts/clean-work-artifacts.ps1 -Apply -MinAgeDays 3
    # actually delete task dirs older than 3 days, plus stale bin/obj
#>
[CmdletBinding()]
param(
    [int]$MinAgeDays = 2,
    [switch]$Apply,
    [string]$RepoRoot = 'D:\CodeProject\PuddingAgent\PuddingAgent',
    [switch]$SkipBinObj
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Paths that must never be deleted by this script, whatever the age.
$ProtectedPatterns = @('\temp\build\winui3\', '\temp\build\winui3')

function Get-DirectorySizeBytes {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return [int64]0 }
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { return [int64]0 }
    $measured = $files | Measure-Object -Property Length -Sum
    if ($null -eq $measured -or $null -eq $measured.Sum) { return [int64]0 }
    return [int64]$measured.Sum
}

function Test-Protected {
    param([string]$Path)
    foreach ($p in $ProtectedPatterns) {
        if ($Path.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
    return $false
}

function Format-Gb {
    param([int64]$Bytes)
    return ('{0,9:N2} GB' -f ($Bytes / 1GB))
}

if (-not (Test-Path -LiteralPath $RepoRoot)) {
    throw "RepoRoot not found: $RepoRoot"
}

$cutoff = (Get-Date).AddDays(-$MinAgeDays)
Write-Output "repo      : $RepoRoot"
Write-Output "cutoff    : $cutoff  (MinAgeDays=$MinAgeDays)"
Write-Output "mode      : $(if ($Apply) { 'APPLY (deleting)' } else { 'DRY RUN (nothing is deleted)' })"
Write-Output ''

$targets = [System.Collections.Generic.List[object]]::new()

# --- 1. .work/<task> directories -------------------------------------------
$workRoot = Join-Path $RepoRoot '.work'
if (Test-Path -LiteralPath $workRoot) {
    foreach ($dir in (Get-ChildItem -LiteralPath $workRoot -Directory -ErrorAction SilentlyContinue)) {
        if (Test-Protected $dir.FullName) { continue }
        if ($dir.LastWriteTime -ge $cutoff) { continue }
        $targets.Add([pscustomobject]@{
            Kind = 'work'; Path = $dir.FullName; Bytes = (Get-DirectorySizeBytes $dir.FullName); MTime = $dir.LastWriteTime
        })
    }
}

# --- 2. bin / obj outside .work and outside temp ---------------------------
if (-not $SkipBinObj) {
    $binObj = Get-ChildItem -LiteralPath $RepoRoot -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.Name -eq 'bin' -or $_.Name -eq 'obj') -and
            $_.FullName -notmatch '\\\.work\\' -and
            $_.FullName -notmatch '\\temp\\'
        }
    foreach ($dir in $binObj) {
        if (Test-Protected $dir.FullName) { continue }
        $targets.Add([pscustomobject]@{
            Kind = 'binobj'; Path = $dir.FullName; Bytes = (Get-DirectorySizeBytes $dir.FullName); MTime = $dir.LastWriteTime
        })
    }
}

if ($targets.Count -eq 0) {
    Write-Output 'Nothing to clean.'
    return
}

$workBytes = [int64]0
$binBytes = [int64]0
$totalBytes = [int64]0
foreach ($t in $targets) {
    $totalBytes += $t.Bytes
    if ($t.Kind -eq 'work') { $workBytes += $t.Bytes } else { $binBytes += $t.Bytes }
}

Write-Output ('{0,-8} {1,-12} {2}' -f 'KIND', 'SIZE', 'PATH')
foreach ($t in ($targets | Sort-Object Bytes -Descending)) {
    Write-Output ('{0,-8} {1}  {2}' -f $t.Kind, (Format-Gb $t.Bytes), $t.Path)
}
Write-Output ''
Write-Output ('total targets : {0}' -f $targets.Count)
Write-Output ('.work subtrees: ' + (Format-Gb $workBytes))
Write-Output ('bin/obj dirs  : ' + (Format-Gb $binBytes))
Write-Output ('TOTAL         : ' + (Format-Gb $totalBytes))
Write-Output ''

if (-not $Apply) {
    Write-Output 'DRY RUN — re-run with -Apply to delete.'
    return
}

$freed = [int64]0
$failed = [System.Collections.Generic.List[string]]::new()
foreach ($t in $targets) {
    # cmd rmdir handles very long paths that Remove-Item refuses.
    & cmd /c "rmdir /s /q `"$($t.Path)`"" 2>$null | Out-Null
    if (Test-Path -LiteralPath $t.Path) {
        $failed.Add($t.Path)
    } else {
        $freed += $t.Bytes
    }
}

Write-Output ('FREED : ' + (Format-Gb $freed))
if ($failed.Count -gt 0) {
    Write-Output ('FAILED: {0} path(s) still present' -f $failed.Count)
    foreach ($f in $failed) { Write-Output ('  ' + $f) }
}

$drive = Get-PSDrive -Name (Split-Path $RepoRoot -Qualifier).TrimEnd(':') -ErrorAction SilentlyContinue
if ($drive) {
    Write-Output ('disk  : used={0:N1} GB free={1:N1} GB' -f ($drive.Used / 1GB), ($drive.Free / 1GB))
}
