# Read-only readiness check before reclaiming free pages from the SQLite databases.
#
# Why this exists (2026-10-28): the platform database is 11.95 GB on disk while its live
# data is only ~6.9 GB -- 43.9% of its pages are on the freelist and will NEVER be returned
# to the filesystem, because it was created with auto_vacuum=NONE.
#
#   platform    auto_vacuum=0 (NONE)         freelist=1,373,929  free=5366.9 MB
#   memory      auto_vacuum=0 (NONE)         freelist=313        free=1.2 MB
#   code-index  auto_vacuum=2 (INCREMENTAL)  freelist=1          free=0 MB
#
# A NONE database cannot switch mode in place: it needs one full VACUUM, which requires
# (a) roughly live-size free disk next to the file and (b) a write-stop window. This script
# answers both questions plus "is a writer active right now" BEFORE anyone schedules that
# window. It writes nothing.
#
# Exit codes:
#   0 = READY      (headroom OK and no writer observed)
#   2 = BLOCKED    (insufficient headroom, or a writer was observed)
#   3 = UNKNOWN    (a database or pragma could not be read; do NOT treat as ready)
[CmdletBinding()]
param(
    [string]$DataRoot = 'D:\Data',
    [int]$WriterProbeSeconds = 0,
    [string]$AssemblyDirectory = 'D:\CodeProject\PuddingAgent\PuddingAgent\temp\build\winui3\bin\PuddingDesktop\Debug\net10.0-windows10.0.19041.0\win-x64\core'
)

$ErrorActionPreference = 'Stop'

# The SQLite assemblies this script loads are .NET Core builds; Windows PowerShell 5.1 cannot
# Add-Type them and fails with an opaque ReflectionTypeLoadException. Say so plainly instead.
# (Verified 2026-10-28: powershell.exe 5.1 -> ReflectionTypeLoadException; pwsh 7.x -> fine.)
if ($PSVersionTable.PSVersion.Major -lt 6) {
    Write-Output 'FATAL: this script requires PowerShell 7+ (pwsh), not Windows PowerShell 5.1.'
    Write-Output '       The sqlite assemblies under the host build output are .NET Core builds.'
    exit 3
}

foreach ($dll in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'Microsoft.Data.Sqlite.dll')) {
    $p = Join-Path $AssemblyDirectory $dll
    if (-not (Test-Path -LiteralPath $p)) {
        Write-Output ("FATAL: missing assembly " + $p)
        exit 3
    }
    Add-Type -Path $p
}
[SQLitePCL.Batteries_V2]::Init()

function Get-Scalar {
    param($Connection, [string]$Sql)
    $c = $Connection.CreateCommand()
    $c.CommandText = $Sql
    $c.CommandTimeout = 120
    $v = $c.ExecuteScalar()
    if ($null -eq $v) { return $null }
    if ($v -is [DBNull]) { return $null }
    return $v
}

$databases = @(
    @{ Label = 'platform';   Path = (Join-Path $DataRoot 'databases\pudding_platform.db') },
    @{ Label = 'memory';     Path = (Join-Path $DataRoot 'databases\pudding_memory.db') },
    @{ Label = 'code-index'; Path = (Join-Path $DataRoot 'databases\code-index\code_index.db') }
)

# auto_vacuum: 0 = NONE (freelist never returned to the filesystem), 1 = FULL, 2 = INCREMENTAL
$modeName = @{ 0 = 'NONE'; 1 = 'FULL'; 2 = 'INCREMENTAL' }
$unknown = $false
$reclaimable = 0.0

Write-Output '=== database freelist / auto_vacuum ==='
foreach ($entry in $databases) {
    $path = $entry.Path
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Output ("  " + $entry.Label.PadRight(11) + " <absent>")
        continue
    }
    $conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + $path + ";Mode=ReadOnly")
    try {
        $conn.Open()
        $av = Get-Scalar $conn 'PRAGMA auto_vacuum;'
        $fl = Get-Scalar $conn 'PRAGMA freelist_count;'
        $pc = Get-Scalar $conn 'PRAGMA page_count;'
        $ps = Get-Scalar $conn 'PRAGMA page_size;'
        if ($null -eq $av -or $null -eq $fl -or $null -eq $ps) {
            Write-Output ("  " + $entry.Label.PadRight(11) + " PRAGMA_UNREADABLE")
            $unknown = $true
            continue
        }
        $avInt = [int]$av
        $name = 'UNKNOWN'
        if ($modeName.ContainsKey($avInt)) { $name = $modeName[$avInt] }
        $freeMB = [Math]::Round(([long]$fl * [long]$ps) / 1MB, 1)
        if ($avInt -ne 2) { $reclaimable += $freeMB }
        $flag = ''
        if ($avInt -eq 0 -and $freeMB -ge 1024) { $flag = '  <-- NONE + >=1GB stranded, needs a full VACUUM' }
        Write-Output ("  " + $entry.Label.PadRight(11) + " auto_vacuum=" + $avInt + " (" + $name + ")  freelist=" + $fl + "  free=" + $freeMB + " MB  pages=" + $pc + $flag)
    } catch {
        Write-Output ("  " + $entry.Label.PadRight(11) + " READ_FAILED: " + $_.Exception.Message)
        $unknown = $true
    } finally {
        $conn.Close()
    }
}

Write-Output ''
Write-Output '=== disk headroom ==='
$platformPath = Join-Path $DataRoot 'databases\pudding_platform.db'
$headroomOk = $false
if (Test-Path -LiteralPath $platformPath) {
    $item = Get-Item -LiteralPath $platformPath
    $root = [IO.Path]::GetPathRoot($item.FullName)
    $driveLetter = $root.TrimEnd('\').TrimEnd(':')
    $drive = Get-PSDrive -Name $driveLetter -ErrorAction SilentlyContinue
    if ($null -eq $drive) {
        Write-Output ("  drive " + $driveLetter + " not readable")
        $unknown = $true
    } else {
        # VACUUM writes a fresh copy of LIVE data beside the original; live is smaller than
        # the file, so requiring one full file size of headroom is a safe upper bound.
        $freeGB = [Math]::Round($drive.Free / 1GB, 2)
        $fileGB = [Math]::Round($item.Length / 1GB, 2)
        Write-Output ("  " + $driveLetter + ": free=" + $freeGB + " GB; platform file=" + $fileGB + " GB; need >= " + $fileGB + " GB")
        $headroomOk = ($freeGB -gt $fileGB)
        if ($headroomOk) { Write-Output '  HEADROOM=OK' } else { Write-Output '  HEADROOM=INSUFFICIENT' }
    }
} else {
    Write-Output '  platform db absent; nothing to reclaim'
    $unknown = $true
}

Write-Output ''
Write-Output '=== writer activity ==='
$writerActive = $false
$wal = $platformPath + '-wal'
if ($WriterProbeSeconds -le 0) {
    Write-Output ("  skipped (pass -WriterProbeSeconds N to observe for N seconds)")
} elseif (-not (Test-Path -LiteralPath $wal)) {
    Write-Output '  no -wal file; no writer'
} else {
    $before = Get-Item -LiteralPath $wal
    $bMtime = $before.LastWriteTimeUtc
    $bLen = $before.Length
    Start-Sleep -Seconds $WriterProbeSeconds
    $after = Get-Item -LiteralPath $wal
    $aMtime = $after.LastWriteTimeUtc
    $aLen = $after.Length
    Write-Output ("  before: mtime=" + $bMtime.ToString('o') + " bytes=" + $bLen)
    Write-Output ("  after : mtime=" + $aMtime.ToString('o') + " bytes=" + $aLen)
    if ($bMtime -eq $aMtime -and $bLen -eq $aLen) {
        Write-Output '  WRITER=QUIESCENT'
    } else {
        Write-Output '  WRITER=ACTIVE (a full VACUUM needs a write-stop window)'
        $writerActive = $true
    }
}

Write-Output ''
Write-Output '=== summary ==='
Write-Output ("  reclaimable_on_none_databases=" + [Math]::Round($reclaimable, 1) + " MB")

if ($unknown) {
    Write-Output 'RESULT: UNKNOWN - could not read every database; do NOT treat as ready'
    exit 3
}
if ($writerActive -or -not $headroomOk) {
    Write-Output 'RESULT: BLOCKED - see HEADROOM / WRITER above'
    exit 2
}
Write-Output 'RESULT: READY - headroom sufficient and no writer observed; nothing was modified'
exit 0
