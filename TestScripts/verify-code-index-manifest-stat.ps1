# Read-only verifier: does the source manifest's fingerprint still match the disk?
#
# Why this exists (2026-10-28): a full calibration re-extracted 2540 files while reusing 4229.
# A read-only probe of code_index.db showed EXACT=4234 / MTIME_DIFF=2528 -- i.e. the detector
# was RIGHT and roughly 2528 files' fingerprints really are stale. The open question is why a
# calibration that re-extracts them does not persist their new fingerprint.
#
# This script makes that question answerable without archaeology: run it before and after a
# calibration; MTIME_DIFF must drop towards 0 if fingerprints are being persisted.
#
# Strictly read-only (Mode=ReadOnly). WAL caveat: if <db>-wal is non-zero this read can be a
# stale snapshot -- the script prints the WAL size and refuses to give a verdict when it is big.
[CmdletBinding()]
param(
    [string]$DatabasePath = 'D:\Data\databases\code-index\code_index.db',
    [string]$ScopePrefix = '8a48458b',
    # Mismatch ratio above this is a FAIL (fingerprints are not being persisted).
    [double]$MaxMismatchRatio = 0.10
)

$ErrorActionPreference = 'Stop'

$core = 'D:\CodeProject\PuddingAgent\PuddingAgent\temp\build\winui3\bin\PuddingDesktop\Debug\net10.0-windows10.0.19041.0\win-x64\core'
Add-Type -Path (Join-Path $core 'SQLitePCLRaw.core.dll')
Add-Type -Path (Join-Path $core 'SQLitePCLRaw.batteries_v2.dll')
Add-Type -Path (Join-Path $core 'SQLitePCLRaw.provider.e_sqlite3.dll')
Add-Type -Path (Join-Path $core 'Microsoft.Data.Sqlite.dll')
[SQLitePCL.Batteries_V2]::Init()

if (-not (Test-Path -LiteralPath $DatabasePath)) {
    Write-Output ("FATAL: database not found: " + $DatabasePath)
    exit 1
}

$walPath = $DatabasePath + '-wal'
$walBytes = if (Test-Path -LiteralPath $walPath) { (Get-Item -LiteralPath $walPath).Length } else { 0 }
Write-Output ("db       = " + $DatabasePath)
Write-Output ("walBytes = " + $walBytes)

$conn = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DatabasePath;Mode=ReadOnly;Cache=Shared")
$conn.Open()
try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT ProjectId, FilePath, LastWriteTimeUtc, Length FROM CodeSourceManifest WHERE ProjectId LIKE @prefix"
    # 参数化而不是拼接：SQLite 的字符串字面量是单引号，双引号会被当成标识符
    # （实测报错：no such column: "8a48458b%"）。
    [void]$cmd.Parameters.AddWithValue('@prefix', "$ScopePrefix%")
    $r = $cmd.ExecuteReader()

    $total = 0; $exact = 0; $mtimeDiff = 0; $lenDiff = 0; $missing = 0; $unparsed = 0
    $scopeSeen = @{}
    while ($r.Read()) {
        $scopeId = [string]$r.GetValue(0)
        $scopeSeen[$scopeId] = $true

        $path = [string]$r.GetValue(1)
        $rawLw = $r.GetValue(2)
        $rawLen = $r.GetValue(3)

        if (-not (Test-Path -LiteralPath $path)) { $missing++; $total++; continue }

        $info = [IO.FileInfo]::new($path)

        # The stored value is an ISO-8601 string today; ticks (long) is accepted defensively.
        # Never silently skip a row: an unparsed row would make the ratio look better than it is.
        $storedUtc = $null
        if ($rawLw -is [long] -or $rawLw -is [int]) {
            $storedUtc = [DateTime]::new([long]$rawLw, [DateTimeKind]::Utc)
        } elseif ($rawLw -is [string]) {
            $parsed = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse([string]$rawLw, [ref]$parsed)) { $storedUtc = $parsed.UtcDateTime }
        }

        $total++
        if ($null -eq $storedUtc) { $unparsed++; continue }

        $lenOk = ([long]$rawLen -eq [long]$info.Length)
        $mtimeOk = ($storedUtc -eq $info.LastWriteTimeUtc)
        if (-not $lenOk) { $lenDiff++ }
        if (-not $mtimeOk) { $mtimeDiff++ }
        if ($lenOk -and $mtimeOk) { $exact++ }
    }
    $r.Dispose(); $cmd.Dispose()
} finally {
    $conn.Close(); $conn.Dispose()
}

$stale = $mtimeDiff + $lenDiff + $missing
$ratio = if ($total -gt 0) { [math]::Round($stale / $total, 4) } else { 0 }

Write-Output ("scopes   = " + (($scopeSeen.Keys | Sort-Object) -join ', '))
Write-Output ("TOTAL=$total EXACT=$exact MTIME_DIFF=$mtimeDiff LEN_DIFF=$lenDiff MISSING=$missing UNPARSED=$unparsed")
Write-Output ("staleRatio=$ratio  threshold=$MaxMismatchRatio")

# A read-only reader on a WAL database without write access to -shm can silently see the
# pre-WAL snapshot. If the WAL is substantial, the numbers below are not trustworthy.
if ($walBytes -gt 1048576) {
    Write-Output 'RESULT: INCONCLUSIVE - WAL is large; a read-only connection may be reading a stale snapshot'
    exit 3
}

if ($total -eq 0) {
    Write-Output 'RESULT: INCONCLUSIVE - no manifest rows matched the scope prefix'
    exit 3
}

if ($ratio -le $MaxMismatchRatio) {
    Write-Output 'RESULT: PASS - manifest fingerprints match the disk'
    exit 0
}

Write-Output 'RESULT: FAIL - manifest fingerprints are stale; a calibration did not persist them'
exit 2
