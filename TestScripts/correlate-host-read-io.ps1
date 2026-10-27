# Correlate the host process's sustained read IO with per-component activity.
# Read-only: this script only stats files before/after a quiet window.
[CmdletBinding()]
param([int]$WindowSeconds = 20)

$ErrorActionPreference = 'Continue'

function Get-FileState([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $f = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
    if (-not $f) { return $null }
    return [pscustomobject]@{ Bytes = $f.Length; Mtime = $f.LastWriteTimeUtc }
}

function Get-DirState([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $files = Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $bytes) { $bytes = 0 }
    return [pscustomobject]@{ Count = @($files).Count; Bytes = [long]$bytes }
}

# --- watch list -------------------------------------------------------------
$watchedFiles = @(
    'D:\Data\databases\pudding_platform.db',
    'D:\Data\databases\pudding_platform.db-wal',
    'D:\Data\databases\pudding_platform.db-shm',
    'D:\Data\databases\code-index\code_index.db',
    'D:\Data\databases\code-index\code_index.db-wal',
    'D:\Data\databases\pudding_memory.db',
    'D:\Data\databases\pudding_memory.db-wal'
)

$componentLog = 'D:\Data\logs\components'
$systemLog = 'D:\Data\logs\system'

Write-Output ("=== window {0}s === " -f $WindowSeconds)

$before = @{}
foreach ($f in $watchedFiles) { $before[$f] = Get-FileState $f }

$logBefore = @{}
if (Test-Path $componentLog) {
    foreach ($d in Get-ChildItem $componentLog -Directory -ErrorAction SilentlyContinue) {
        $newest = Get-ChildItem $d.FullName -File -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($newest) { $logBefore[$d.Name] = $newest.Length }
    }
}

$sysBefore = $null
$sysNewest = Get-ChildItem $systemLog -File -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($sysNewest) { $sysBefore = $sysNewest.Length }

# quiet window: no filesystem work here beyond the sleeps themselves
Start-Sleep -Seconds $WindowSeconds

Write-Output '--- sqlite files (bytes delta) ---'
foreach ($f in $watchedFiles) {
    $a = $before[$f]
    $b = Get-FileState $f
    if (-not $b) { Write-Output ("  {0} ABSENT" -f (Split-Path $f -Leaf)); continue }
    $delta = if ($a) { $b.Bytes - $a.Bytes } else { 0 }
    $changed = if ($a -and $a.Mtime -ne $b.Mtime) { 'CHANGED' } else { '-' }
    Write-Output ("  {0} bytes={1} delta={2} {3}" -f (Split-Path $f -Leaf), $b.Bytes, $delta, $changed)
}

Write-Output '--- component log growth (bytes delta over window) ---'
$growth = @()
foreach ($name in ($logBefore.Keys | Sort-Object)) {
    $d = Join-Path $componentLog $name
    $newest = Get-ChildItem $d -File -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $newest) { continue }
    $delta = $newest.Length - $logBefore[$name]
    if ($delta -ne 0) { $growth += ("  {0} delta={1}" -f $name, $delta) }
}
if ($growth.Count -eq 0) { Write-Output '  (no component log grew)' } else { $growth | ForEach-Object { Write-Output $_ } }

$sysAfter = Get-ChildItem $systemLog -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($sysNewest -and $sysAfter) {
    Write-Output ("--- system log {0} delta={1}" -f $sysAfter.Name, ($sysAfter.Length - $sysBefore))
}

Write-Output '--- session log dir ---'
$sessDirs = @(
    'D:\Data\agents\default.global_general-assistant.6a8\logs\sessions',
    'D:\Data\agents\default.global_general-assistant.6a8\logs\messages'
)
foreach ($d in $sessDirs) {
    $a = Get-DirState $d
    if ($a) { Write-Output ("  {0} files={1} bytes={2}" -f $d, $a.Count, $a.Bytes) } else { Write-Output ("  {0} MISSING" -f $d) }
}

Write-Output 'RESULT: OK'
