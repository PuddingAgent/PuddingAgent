# Which file is being read? Answer it with NTFS last-access time.
#
# Why this exists (2026-10-28): handle enumeration can only answer "what is open", not "what is
# being read". PDH gives no per-file counters, and there is no ETW/procmon available here. On a
# host configured with DisableLastAccess=2 the filesystem still bumps LastAccessTime on reads,
# which makes this the ONE cheap way to name the file being hammered.
#
# This is how the read-storm attribution was closed: over a 30 s window pudding_platform.db's
# access time advanced exactly 30 s while pudding_memory.db and code_index.db did not move at
# all. If memory.db had really been read 200k times/s its access time could not sit still.
#
# HARD LIMITS -- read before trusting any output:
#   * Only valid when `fsutil behavior query disablelastaccess` reports 2 (system-managed
#     updates enabled). Values 1 and 3 disable them entirely: then a "no movement" result
#     proves NOTHING and the script exits 2 instead of guessing.
#   * NTFS DEFERS last-access writes. On this machine the update is observable within seconds;
#     on another machine it may lag up to an hour. A window with no movement is therefore
#     INCONCLUSIVE (exit 1), never "nothing is being read".
#   * -wal / -shm sidecars are polluted by WRITES. Only judge by the main .db file itself.
#
# Exit codes:
#   0 = at least one access time advanced  (those files are read candidates)
#   1 = no access movement in the window   (INCONCLUSIVE - do not read as "not being read")
#   2 = environment does not support this probe (DisableLastAccess is not 2), or bad input
[CmdletBinding()]
param(
    [string[]]$Path = @(
        'D:\Data\databases\pudding_platform.db',
        'D:\Data\databases\pudding_memory.db',
        'D:\Data\databases\code-index\code_index.db'
    ),
    [int]$WaitSeconds = 30
)

$ErrorActionPreference = 'Stop'

if ($WaitSeconds -lt 1) {
    Write-Output ("FATAL: -WaitSeconds must be >= 1 (got " + $WaitSeconds + ")")
    exit 2
}
if ($null -eq $Path -or $Path.Count -eq 0) {
    Write-Output 'FATAL: -Path is empty'
    exit 2
}

# === precondition: the probe is only meaningful when system-managed updates are on ===
$modeText = (& fsutil behavior query disablelastaccess) 2>&1 | Out-String
Write-Output ("fsutil: " + $modeText.Trim())

$mode = $null
if ($modeText -match 'DisableLastAccess\s*=\s*(\d)') { $mode = [int]$Matches[1] }

if ($null -eq $mode) {
    Write-Output 'FATAL: could not parse DisableLastAccess; refusing to guess'
    exit 2
}
if ($mode -ne 2) {
    Write-Output ("REFUSING: DisableLastAccess=" + $mode + ". Only 2 keeps system-managed access updates.")
    Write-Output '  With 1 or 3 the timestamps will never move, so this probe cannot answer anything.'
    Write-Output '  Do not interpret a future "no movement" result as "nothing is being read".'
    exit 2
}
Write-Output 'precondition: DisableLastAccess=2 -> probe is meaningful'
Write-Output ''

function Get-Snapshot {
    param([string[]]$Targets)
    $out = @{}
    foreach ($t in $Targets) {
        if (Test-Path -LiteralPath $t) {
            $out[$t] = [pscustomobject]@{
                Access = [IO.File]::GetLastAccessTimeUtc($t)
                Write  = [IO.File]::GetLastWriteTimeUtc($t)
                Bytes  = (Get-Item -LiteralPath $t).Length
            }
        } else {
            $out[$t] = $null
        }
    }
    return $out
}

Write-Output '=== T0 ==='
$before = Get-Snapshot -Targets $Path
foreach ($t in $Path) {
    if ($null -eq $before[$t]) { Write-Output ("  <absent> " + $t); continue }
    Write-Output ("  " + $t)
    Write-Output ("      access=" + $before[$t].Access.ToString('o') + "  write=" + $before[$t].Write.ToString('o') + "  bytes=" + $before[$t].Bytes)
}

Write-Output ("... waiting " + $WaitSeconds + "s ...")
Start-Sleep -Seconds $WaitSeconds

Write-Output '=== T1 ==='
$after = Get-Snapshot -Targets $Path
foreach ($t in $Path) {
    if ($null -eq $after[$t]) { Write-Output ("  <absent> " + $t); continue }
    Write-Output ("  " + $t)
    Write-Output ("      access=" + $after[$t].Access.ToString('o') + "  write=" + $after[$t].Write.ToString('o') + "  bytes=" + $after[$t].Bytes)
}

Write-Output ''
Write-Output '=== verdict (judge ONLY by .db access; sidecars are write-polluted) ==='
$anyAccessMoved = $false
foreach ($t in $Path) {
    if ($null -eq $before[$t] -or $null -eq $after[$t]) { continue }

    $accessMoved = ($before[$t].Access -ne $after[$t].Access)
    $writeMoved = ($before[$t].Write -ne $after[$t].Write)
    if ($accessMoved) { $anyAccessMoved = $true }

    $tag = 'no-change'
    if ($accessMoved) { $tag = 'ACCESS-MOVED' }
    elseif ($writeMoved) { $tag = 'write-only' }
    Write-Output ("  " + $tag.PadRight(14) + $t)
}

Write-Output ''
if ($anyAccessMoved) {
    Write-Output 'RESULT: at least one file was READ in this window (access time advanced).'
    exit 0
}

Write-Output 'RESULT: INCONCLUSIVE - no access-time movement in the window.'
Write-Output '        NTFS defers last-access writes, so this proves nothing either way.'
Write-Output '        Do NOT record this as "no file is being read".'
exit 1
