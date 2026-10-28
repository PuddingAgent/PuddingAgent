# Reclaim the ~5.36 GB stranded in pudding_platform.db, inside a host write-stop window.
#
# WHY THIS SCRIPT EXISTS
#   The live database is auto_vacuum=NONE with 1.37M freelist pages (5358 MB, 43.9%). Those pages
#   can never be returned to the filesystem. Switching to INCREMENTAL cannot be done in place: it
#   needs one full rewrite. And the rewrite cannot run while the host holds the file.
#
#   Measured on this machine (2026-10-28): VACUUM INTO took 100.5 s, the follow-up VACUUM to apply
#   INCREMENTAL took 120.2 s, producing 6910525440 bytes (6.44 GB) from 12826017792 (11.95 GB).
#   So the whole window is roughly 4 minutes of work.
#
# WHY IT REDOES THE VACUUM INSTEAD OF REUSING A PREPARED COPY
#   Any copy prepared earlier is a snapshot of that instant. Swapping it in would silently discard
#   every row written since. Redoing it here costs ~100 s and loses nothing.
#   (A pre-verified copy may be reused with -ReusePreparedCopy ONLY if the host has been stopped
#    the whole time since that copy was made, and you accept the cut-off. Default is to redo.)
#
# SAFETY MODEL
#   1. Refuses to run unless the host process is gone.
#   2. Backs up by RENAME (same volume, instant, no 12 GB copy) -- the original is never deleted.
#   3. Verifies the new file (freelist=0, auto_vacuum=2, journal=wal, integrity_check, table and
#      index counts) BEFORE swapping anything.
#   4. If verification fails, nothing is replaced and the backup is untouched.
#   5. Prints exact rollback instructions at the end.
#
# Exit codes: 0 = swapped and verified, 2 = preconditions not met, 3 = build/verify failed
#             (nothing replaced), 4 = swap done but post-swap verification failed (roll back).
[CmdletBinding()]
param(
    [string]$DataRoot = 'D:\Data',
    [string]$AssemblyDirectory = 'D:\CodeProject\PuddingAgent\PuddingAgent\temp\build\winui3\bin\PuddingDesktop\Debug\net10.0-windows10.0.19041.0\win-x64\core',
    [string]$ProcessName = 'PuddingAgent',
    [switch]$ReusePreparedCopy,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 6) {
    Write-Output 'FATAL: requires PowerShell 7+ (pwsh); the sqlite assemblies are .NET Core builds.'
    exit 2
}

$live = Join-Path $DataRoot 'databases\pudding_platform.db'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $DataRoot ('databases\pudding_platform_compact.' + $stamp + '.db')
$backup = $live + '.pre-vacuum-' + $stamp

Write-Output '=== preconditions ==='
$running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    foreach ($p in $running) { Write-Output ("  host running: pid=" + $p.Id + " started=" + $p.StartTime) }
    if (-not $Force) {
        Write-Output 'REFUSING: the host still holds the database. Stop it first (this also ends the agent session).'
        Write-Output 'Pass -Force only if you are certain the file is not locked; the rename will fail anyway.'
        exit 2
    }
    Write-Output '  -Force given; continuing (rename will fail if the file is truly locked).'
} else {
    Write-Output '  host not running: OK'
}
if (-not (Test-Path -LiteralPath $live)) {
    Write-Output ("FATAL: live database missing -> " + $live)
    exit 2
}

foreach ($dll in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'Microsoft.Data.Sqlite.dll')) {
    $p = Join-Path $AssemblyDirectory $dll
    if (-not (Test-Path -LiteralPath $p)) { Write-Output ("FATAL: missing assembly " + $p); exit 2 }
    Add-Type -Path $p
}
[SQLitePCL.Batteries_V2]::Init()

function Open-Db { param([string]$Path, [string]$Mode = 'ReadWrite')
    $c = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + $Path + ";Mode=" + $Mode)
    $c.Open(); return $c
}
function Get-Scalar { param($Conn, [string]$Sql, [int]$TimeoutSec = 3600)
    $c = $Conn.CreateCommand(); $c.CommandText = $Sql; $c.CommandTimeout = $TimeoutSec
    $v = $c.ExecuteScalar()
    if ($null -eq $v) { return $null }
    if ($v -is [DBNull]) { return $null }
    return $v
}

$liveBytes = (Get-Item -LiteralPath $live).Length
Write-Output ("  live=" + $live + "  bytes=" + $liveBytes + "  (" + [Math]::Round($liveBytes / 1MB, 1) + " MB)")

Write-Output ''
Write-Output '=== build the compacted copy ==='
if ($ReusePreparedCopy) {
    $prepared = Join-Path $DataRoot 'databases\pudding_platform_compact.db'
    if (-not (Test-Path -LiteralPath $prepared)) {
        Write-Output ("FATAL: -ReusePreparedCopy but " + $prepared + " does not exist")
        exit 2
    }
    Copy-Item -LiteralPath $prepared -Destination $work -Force
    Write-Output ("  reused prepared copy -> " + $work)
    Write-Output '  WARNING: rows written after that copy was made are NOT in it.'
} else {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $c = Open-Db -Path $live -Mode 'ReadOnly'
        $cmd = $c.CreateCommand()
        $cmd.CommandText = "VACUUM INTO '" + ($work -replace "'", "''") + "';"
        $cmd.CommandTimeout = 3600
        $null = $cmd.ExecuteNonQuery()
        $c.Close()
        $sw.Stop()
        Write-Output ("  VACUUM INTO ok in " + [Math]::Round($sw.Elapsed.TotalSeconds, 1) + " s")
    } catch {
        $sw.Stop()
        Write-Output ('  VACUUM INTO FAILED: ' + $_.Exception.Message)
        Write-Output '  nothing was replaced.'
        exit 3
    }
}

Write-Output ''
Write-Output '=== apply INCREMENTAL and restore WAL on the copy ==='
try {
    $c = Open-Db -Path $work
    $c1 = $c.CreateCommand(); $c1.CommandText = 'PRAGMA auto_vacuum = INCREMENTAL;'; $c1.CommandTimeout = 3600
    $null = $c1.ExecuteNonQuery()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $c2 = $c.CreateCommand(); $c2.CommandText = 'VACUUM;'; $c2.CommandTimeout = 3600
    $null = $c2.ExecuteNonQuery()
    $sw.Stop()
    Write-Output ("  VACUUM ok in " + [Math]::Round($sw.Elapsed.TotalSeconds, 1) + " s")
    $c3 = $c.CreateCommand(); $c3.CommandText = 'PRAGMA journal_mode = WAL;'; $c3.CommandTimeout = 3600
    $jm = $c3.ExecuteScalar()
    Write-Output ("  journal_mode=" + $jm)
    $c.Close()
} catch {
    Write-Output ('  post-processing FAILED: ' + $_.Exception.Message)
    Write-Output '  nothing was replaced.'
    exit 3
}

Write-Output ''
Write-Output '=== verify the copy BEFORE touching the live file ==='
$srcConn = Open-Db -Path $live -Mode 'ReadOnly'
$dstConn = Open-Db -Path $work -Mode 'ReadOnly'

$tablesSrc = @()
$c = $srcConn.CreateCommand()
$c.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"
$c.CommandTimeout = 600
$r = $c.ExecuteReader(); while ($r.Read()) { $tablesSrc += $r.GetString(0) }; $r.Close()

$idxSrc = [int](Get-Scalar -Conn $srcConn -Sql "SELECT COUNT(*) FROM sqlite_master WHERE type='index';")
$idxDst = [int](Get-Scalar -Conn $dstConn -Sql "SELECT COUNT(*) FROM sqlite_master WHERE type='index';")

$freelist = [long](Get-Scalar -Conn $dstConn -Sql 'PRAGMA freelist_count;')
$autoVac = [int](Get-Scalar -Conn $dstConn -Sql 'PRAGMA auto_vacuum;')
$journal = [string](Get-Scalar -Conn $dstConn -Sql 'PRAGMA journal_mode;')
$integrity = [string](Get-Scalar -Conn $dstConn -Sql 'PRAGMA integrity_check;')

$rowProblems = 0
foreach ($t in $tablesSrc) {
    $ns = [long](Get-Scalar -Conn $srcConn -Sql ('SELECT COUNT(*) FROM "' + $t + '";'))
    $nd = Get-Scalar -Conn $dstConn -Sql ('SELECT COUNT(*) FROM "' + $t + '";')
    if ($null -eq $nd) { Write-Output ("  MISSING TABLE IN COPY: " + $t); $rowProblems++; continue }
    if ([long]$nd -gt $ns) { Write-Output ("  COPY HAS MORE ROWS THAN SOURCE: " + $t); $rowProblems++ }
}

$dstBytes = (Get-Item -LiteralPath $work).Length
Write-Output ("  tables=" + $tablesSrc.Count + "  indexes src=" + $idxSrc + " dst=" + $idxDst)
Write-Output ("  freelist=" + $freelist + "  auto_vacuum=" + $autoVac + "  journal=" + $journal)
Write-Output ("  integrity_check=" + $integrity)
Write-Output ("  copy bytes=" + $dstBytes + "  (" + [Math]::Round($dstBytes / 1MB, 1) + " MB)  reclaim=" + [Math]::Round(($liveBytes - $dstBytes) / 1MB, 1) + " MB")

$verified = ($idxSrc -eq $idxDst) -and ($freelist -eq 0) -and ($autoVac -eq 2) -and ($journal -eq 'wal') -and ($integrity -eq 'ok') -and ($rowProblems -eq 0)
$srcConn.Close(); $dstConn.Close()

if (-not $verified) {
    Write-Output 'VERIFY FAILED -- nothing was replaced. The live database is untouched.'
    Write-Output ("  prepared copy left at: " + $work)
    exit 3
}
Write-Output '  VERIFY OK'

Write-Output ''
Write-Output '=== swap (rename, same volume) ==='
$moved = @()
try {
    Move-Item -LiteralPath $live -Destination $backup
    $moved += $backup
    Write-Output ("  live -> " + $backup)
} catch {
    Write-Output ('  backup rename FAILED: ' + $_.Exception.Message)
    Write-Output '  nothing was replaced.'
    exit 3
}

# The old -wal/-shm belong to the old file; move them aside so they can never be paired with the
# new database (a stale WAL applied to a different file would corrupt it).
foreach ($suffix in @('-wal', '-shm')) {
    $side = $live + $suffix
    if (Test-Path -LiteralPath $side) {
        try { Move-Item -LiteralPath $side -Destination ($backup + $suffix); Write-Output ("  moved sidecar " + $suffix) }
        catch { Write-Output ("  WARN: could not move sidecar " + $suffix + ": " + $_.Exception.Message) }
    }
}

try {
    Move-Item -LiteralPath $work -Destination $live
    Write-Output '  compacted copy -> live'
} catch {
    Write-Output ('  swap FAILED: ' + $_.Exception.Message)
    Write-Output ('  ROLLING BACK: rename ' + $backup + ' back to ' + $live)
    try { Move-Item -LiteralPath $backup -Destination $live; Write-Output '  rollback ok' } catch { Write-Output '  ROLLBACK FAILED - restore manually' }
    exit 3
}

Write-Output ''
Write-Output '=== post-swap check ==='
$chk = Open-Db -Path $live -Mode 'ReadOnly'
$f2 = [long](Get-Scalar -Conn $chk -Sql 'PRAGMA freelist_count;')
$a2 = [int](Get-Scalar -Conn $chk -Sql 'PRAGMA auto_vacuum;')
$j2 = [string](Get-Scalar -Conn $chk -Sql 'PRAGMA journal_mode;')
$i2 = [string](Get-Scalar -Conn $chk -Sql 'PRAGMA integrity_check;')
$chk.Close()
$b2 = (Get-Item -LiteralPath $live).Length
Write-Output ("  live bytes=" + $b2 + " (" + [Math]::Round($b2 / 1MB, 1) + " MB)  freelist=" + $f2 + "  auto_vacuum=" + $a2 + "  journal=" + $j2)
Write-Output ("  integrity_check=" + $i2)

if (($f2 -eq 0) -and ($a2 -eq 2) -and ($j2 -eq 'wal') -and ($i2 -eq 'ok')) {
    Write-Output ''
    Write-Output 'RESULT: SWAPPED_AND_VERIFIED'
    Write-Output ("  reclaimed " + [Math]::Round(($liveBytes - $b2) / 1MB, 1) + " MB")
    Write-Output ("  backup kept at " + $backup + " (delete after you are happy)")
    Write-Output '  NOW START THE HOST. Then measure IO Read Bytes/sec:'
    Write-Output '    if it dropped to ~10 MB/s, the freelist-traversal hypothesis was right;'
    Write-Output '    if it did not, the hypothesis is wrong and the read storm has another cause.'
    exit 0
}

Write-Output 'RESULT: SWAPPED_BUT_POST_CHECK_FAILED'
Write-Output '  ROLL BACK: delete the new file and rename the backup back:'
Write-Output ("    Remove-Item -LiteralPath '" + $live + "' -Force")
Write-Output ("    Move-Item -LiteralPath '" + $backup + "' -Destination '" + $live + "'")
exit 4
