#requires -Version 5.1
<#
Goal.md rotation helper (RSI housekeeping).

WHY THIS EXISTS
  goal.md is an append-only log. goal_read truncates above 16 KB (measured: a file of
  16,692 bytes came back as "tail only"), so an un-rotated goal.md silently degrades
  session-context recovery. Rotation has to be mechanical, or it will not happen.

WHY THE SCRIPT BODY IS PURE ASCII
  Windows PowerShell 5.1 decodes a BOM-less UTF-8 script as CP936, which turns
  non-ASCII source text into a parse error or mojibake. All non-ASCII content here is
  DATA (goal text, pointer text), read/written with an explicit UTF8Encoding(no BOM).

SAFETY MODEL
  - dry-run by default; -Apply is required to touch any file.
  - A single structural check decides everything: slicing must be LOSSLESS
    (preamble + all entries, concatenated, must equal the original byte-for-byte).
    If it does not, the script refuses and writes nothing.
  - Refuses when the byte budget cannot be met while honouring -KeepEntries.
  - -Apply backs up the original first, then verifies the written size on read-back.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GoalPath,
    [string]$ArchiveDir = '',
    [int]$MaxGoalBytes = 14000,
    [int]$KeepEntries = 2,
    [string]$PointerText = '',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$enc = New-Object System.Text.UTF8Encoding($false)

# Entry boundary: a separator line, a blank line, then an ISO-8601 UTC stamp line.
$separator = [regex]'(?m)^---\s*\r?\n\s*\r?\n\*\*\d{4}-\d{2}-\d{2}T'

function Emit([string]$key, $value) { Write-Output ('{0}={1}' -f $key, $value) }
function Fail([string]$status, [string]$detail) {
    Emit 'STATUS' $status
    Emit 'DETAIL' $detail
    Emit 'APPLIED' 'False'
    exit 3
}

if (-not (Test-Path -LiteralPath $GoalPath)) { Fail 'GOAL_NOT_FOUND' $GoalPath }

if ([string]::IsNullOrWhiteSpace($ArchiveDir)) {
    $ArchiveDir = Join-Path (Split-Path -Parent $GoalPath) 'archive'
}
if ([string]::IsNullOrWhiteSpace($PointerText)) {
    # ASCII on purpose: keeps the source file free of non-ASCII literals.
    $PointerText = '> Older entries archived to: goal-archive-<date>.md (see ArchiveDir)'
}

$text = [System.IO.File]::ReadAllText($GoalPath, [System.Text.Encoding]::UTF8)
if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }

$matches = $separator.Matches($text)
Emit 'BYTES_BEFORE' ([System.Text.Encoding]::UTF8.GetByteCount($text))

if ($matches.Count -lt 1) {
    Emit 'STATUS' 'NO_ENTRIES'
    Emit 'ENTRIES_TOTAL' '0'
    Emit 'APPLIED' 'False'
    exit 0
}

$starts = @()
foreach ($m in $matches) { $starts += $m.Index }

$preamble = $text.Substring(0, $starts[0])
$entries = @()
for ($i = 0; $i -lt $starts.Count; $i++) {
    $end = if ($i + 1 -lt $starts.Count) { $starts[$i + 1] } else { $text.Length }
    $entries += $text.Substring($starts[$i], $end - $starts[$i])
}

# ---- the one check that matters: slicing must lose nothing -------------------
$rebuilt = $preamble + ($entries -join '')
$lossless = ($rebuilt -eq $text)
Emit 'REBUILD_OK' $lossless
if (-not $lossless) { Fail 'SLICE_NOT_LOSSLESS' 'preamble + entries != original' }

$total = $entries.Count
if ($KeepEntries -lt 0) { Fail 'BAD_ARGUMENT' 'KeepEntries < 0' }
if ($KeepEntries -gt $total) { $KeepEntries = $total }

function Measure-Slim([int]$keepCount, [string]$pointer) {
    $kept = if ($keepCount -gt 0) { $entries[($entries.Count - $keepCount)..($entries.Count - 1)] } else { @() }
    # Only add the pointer when something actually moves. Otherwise a "rotation" that
    # moves nothing would still GROW the file, and the dry-run report would show
    # BYTES_AFTER > BYTES_BEFORE for a no-op plan (misleading).
    $ptr = if ($keepCount -lt $total) { $pointer + "`r`n`r`n" } else { '' }
    $slim = $preamble + $ptr + ($kept -join '')
    return [pscustomobject]@{
        KeepCount = $keepCount
        Bytes     = [System.Text.Encoding]::UTF8.GetByteCount($slim)
        Slim      = $slim
    }
}

$plan = $null
for ($k = $total; $k -ge $KeepEntries; $k--) {
    $candidate = Measure-Slim $k $PointerText
    if ($candidate.Bytes -le $MaxGoalBytes) { $plan = $candidate; break }
}

if ($null -eq $plan) {
    $floor = Measure-Slim $KeepEntries $PointerText
    Emit 'ENTRIES_TOTAL' $total
    Emit 'ENTRIES_KEPT' $KeepEntries
    Emit 'FLOOR_BYTES' $floor.Bytes
    Emit 'MAX_GOAL_BYTES' $MaxGoalBytes
    Fail 'BUDGET_INFEASIBLE' 'even KeepEntries cannot fit MaxGoalBytes; lower KeepEntries or raise the budget'
}

$keepCount = $plan.KeepCount
$movedCount = $total - $keepCount
$moved = if ($movedCount -gt 0) { $entries[0..($movedCount - 1)] } else { @() }

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$archiveName = 'goal-archive-' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd') + '.md'
$archivePath = Join-Path $ArchiveDir $archiveName
$backupPath = $GoalPath + '.bak-' + $stamp
$exists = Test-Path -LiteralPath $archivePath

$header = ''
if (-not $exists) {
    $header = "# goal.md archive`r`n`r`nMoved verbatim from goal.md. Nothing is summarised here.`r`n"
}
$section = "`r`n`r`n## Moved out by byte budget (rotation $stamp)`r`n`r`n" + ($moved -join '')
$archiveAdd = $header + $section

# ---- self checks on the outputs we are about to write ------------------------
$checksOk = $true
foreach ($entry in $moved) { if ($archiveAdd.IndexOf($entry) -lt 0) { $checksOk = $false } }
foreach ($entry in $entries[($entries.Count - $keepCount)..([Math]::Max($entries.Count - 1, 0))]) {
    if ($keepCount -gt 0 -and $plan.Slim.IndexOf($entry) -lt 0) { $checksOk = $false }
}
if ($keepCount -gt 0 -and -not $plan.Slim.StartsWith($preamble)) { $checksOk = $false }
if (($movedCount + $keepCount) -ne $total) { $checksOk = $false }
Emit 'CONSERVATION_OK' $checksOk
if (-not $checksOk) { Fail 'CONSERVATION_FAILED' 'moved/kept entries do not reconcile with the original' }

Emit 'ENTRIES_TOTAL' $total
Emit 'ENTRIES_MOVED' $movedCount
Emit 'ENTRIES_KEPT' $keepCount
Emit 'BYTES_AFTER' $plan.Bytes
Emit 'ARCHIVE_PATH' $archivePath
Emit 'MAX_GOAL_BYTES' $MaxGoalBytes

if (-not $Apply) {
    Emit 'STATUS' 'DRY_RUN_OK'
    Emit 'APPLIED' 'False'
    exit 0
}

if ($movedCount -eq 0) {
    Emit 'STATUS' 'NOTHING_TO_DO'
    Emit 'APPLIED' 'False'
    exit 0
}

if (-not (Test-Path -LiteralPath $ArchiveDir)) { [void](New-Item -ItemType Directory -Path $ArchiveDir -Force) }
Copy-Item -LiteralPath $GoalPath -Destination $backupPath -Force
if ($exists) { [System.IO.File]::AppendAllText($archivePath, $archiveAdd, $enc) }
else { [System.IO.File]::WriteAllText($archivePath, $archiveAdd, $enc) }
[System.IO.File]::WriteAllText($GoalPath, $plan.Slim, $enc)

$written = [System.IO.File]::ReadAllText($GoalPath, [System.Text.Encoding]::UTF8)
Emit 'BACKUP_PATH' $backupPath
Emit 'READBACK_BYTES' ([System.Text.Encoding]::UTF8.GetByteCount($written))
Emit 'READBACK_OK' ($written.Length -eq $plan.Slim.Length)
Emit 'STATUS' 'APPLIED'
Emit 'APPLIED' 'True'
exit 0
