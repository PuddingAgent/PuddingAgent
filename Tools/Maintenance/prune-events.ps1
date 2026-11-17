#Requires -Version 5.1
<#
.SYNOPSIS
    Prune expired rows from the platform event tables in bounded batches.

.DESCRIPTION
    Deletes rows older than -CutoffUtc from the known event tables. Deletion is
    done in small batches with a periodic WAL checkpoint so the live host keeps
    serving while the prune runs. Dry-run is the default; pass -Apply to delete.

    The cutoff is compared as a UTC ISO-8601 string. All candidate columns store
    "yyyy-MM-ddTHH:mm:ss.fffffff+00:00", so lexicographic comparison is safe and
    there is no local/UTC ambiguity.

    VACUUM is deliberately NOT performed here: it requires exclusive access and
    therefore a stop/start window. Use -VacuumOnlyProbe to read the reclaimable
    size instead.

.PARAMETER DbPath
    Platform database file. Defaults to D:\Data\databases\pudding_platform.db

.PARAMETER CutoffUtc
    Keep rows with timestamp >= this value. Example: 2026-10-18

.PARAMETER BatchSize
    Rows per DELETE statement. Smaller means shorter locks.

.PARAMETER Apply
    Actually delete. Without it the script only counts.

.EXAMPLE
    prune-events.ps1 -CutoffUtc 2026-10-18
    Counts what would be deleted.

.EXAMPLE
    prune-events.ps1 -CutoffUtc 2026-10-18 -Apply
    Deletes in batches.

.NOTES
    ASCII-only on purpose: Windows PowerShell 5.1 mis-decodes UTF-8 without BOM.
#>
[CmdletBinding()]
param(
    [string]$DbPath = 'D:\Data\databases\pudding_platform.db',
    [string]$CutoffUtc = '2026-10-18',
    [int]$BatchSize = 20000,
    [int]$MaxBatches = 20000,
    [switch]$Apply,
    [switch]$ForceUsagePrune
)

$ErrorActionPreference = 'Stop'

function Resolve-SqliteAssembly {
    $live = $null
    try {
        $proc = Get-Process PuddingAgent -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc -and $proc.Path) { $live = Split-Path $proc.Path -Parent }
    }
    catch {
        $live = $null
    }
    if ($live) {
        $candidate = Join-Path $live 'Microsoft.Data.Sqlite.dll'
        if (Test-Path $candidate) { return $candidate }
    }
    $fallback = 'D:\CodeProject\PuddingAgent\PuddingAgent\temp\build\winui3\bin\PuddingDesktop\Debug\net10.0-windows10.0.19041.0\win-x64\core\Microsoft.Data.Sqlite.dll'
    if (Test-Path $fallback) { return $fallback }
    throw 'Microsoft.Data.Sqlite.dll not found (no PuddingAgent process, no fallback path).'
}

$sqliteDll = Resolve-SqliteAssembly
Add-Type -Path $sqliteDll | Out-Null

if (-not (Test-Path $DbPath)) { throw "Database not found: $DbPath" }

# Table -> timestamp column. Names verified against live schema 2026-11-17.
# runtime_activity uses started_at_utc and is excluded: it is operational
# telemetry that the diagnostics tooling reads, and its volume is lower.
$tables = @(
    @{ Name = 'conversation_events';         Col = 'occurred_at'     },
    @{ Name = 'telemetry_metric_events';     Col = 'occurred_at_utc' },
    @{ Name = 'context_layer_metric_events'; Col = 'occurred_at_utc' },
    @{ Name = 'llm_gateway_usage_events';    Col = 'occurred_at_utc' },
    @{ Name = 'TokenUsageEvents';            Col = 'OccurredAtUtc'   }
)

$mode = if ($Apply) { 'APPLY' } else { 'DRY-RUN' }
Write-Output "=== prune-events  mode=$mode  cutoff=$CutoffUtc  batch=$BatchSize ==="
Write-Output "DB=$DbPath"
$sizeBefore = (Get-Item $DbPath).Length
Write-Output ("SIZE_BEFORE_MB=" + [Math]::Round($sizeBefore / 1MB, 1))

$connStr = "Data Source=$DbPath;Mode=ReadWrite;Cache=Private;Default Timeout=60"
$conn = New-Object Microsoft.Data.Sqlite.SqliteConnection($connStr)
$conn.Open()

function Invoke-ScalarLong($connection, $sql, $cutoff) {
    $cmd = $connection.CreateCommand()
    $cmd.CommandText = $sql
    if ($null -ne $cutoff) { [void]$cmd.Parameters.AddWithValue('$cut', $cutoff) }
    $v = $cmd.ExecuteScalar()
    if ($null -eq $v -or $v -is [System.DBNull]) { return 0 }
    return [int64]$v
}

$grandTotal = 0
$grandDone = 0

foreach ($t in $tables) {
    $name = $t.Name
    $col = $t.Col

    $total = Invoke-ScalarLong $conn ("SELECT COUNT(*) FROM ""{0}""" -f $name) $null
    $old = Invoke-ScalarLong $conn ("SELECT COUNT(*) FROM ""{0}"" WHERE ""{1}"" < `$cut" -f $name, $col) $CutoffUtc

    # Cost history guard.
    # llm_usage_daily_aggregates is filled LAZILY: a finished UTC day is aggregated
    # only when a report is first queried for it (TokenUsageDailyAggregateService).
    # Deleting usage detail rows for a day that was never aggregated destroys the
    # cost history permanently, because the aggregate is computed FROM those rows.
    if (($name -eq 'llm_gateway_usage_events' -or $name -eq 'TokenUsageEvents') -and $old -gt 0) {
        $uncoveredSql = "SELECT DISTINCT substr(""$col"",1,10) FROM ""$name"" WHERE ""$col"" < `$cut " +
                        "EXCEPT SELECT DISTINCT day_utc FROM llm_usage_daily_aggregates ORDER BY 1"
        $cmdU = $conn.CreateCommand()
        $cmdU.CommandText = $uncoveredSql
        [void]$cmdU.Parameters.AddWithValue('$cut', $CutoffUtc)
        $rdrU = $cmdU.ExecuteReader()
        $uncovered = @()
        while ($rdrU.Read()) { $uncovered += $rdrU.GetString(0) }
        $rdrU.Close()

        if ($uncovered.Count -gt 0) {
            $preview = ($uncovered | Select-Object -First 5) -join ', '
            if ($uncovered.Count -gt 5) { $preview += ', ...' }
            Write-Output ("[GUARD] {0}: {1} day(s) have usage rows but no daily aggregate ({2})" -f $name, $uncovered.Count, $preview)
            if ($Apply -and -not $ForceUsagePrune) {
                Write-Output ("[REFUSE] {0}: deleting these rows would make their cost uncomputable forever. " -f $name +
                              "Open the usage report once so the aggregate is built, or pass -ForceUsagePrune.")
                continue
            }
        }
    }

    $grandTotal += $total

    if (-not $Apply) {
        Write-Output ("[DRY] {0,-30} total={1,9}  would-delete={2,9}  keep={3,9}" -f $name, $total, $old, ($total - $old))
        continue
    }

    if ($old -eq 0) {
        Write-Output ("[SKIP] {0,-29} nothing older than cutoff" -f $name)
        continue
    }

    $deleted = 0
    $batches = 0
    while ($batches -lt $MaxBatches) {
        $sql = "DELETE FROM ""$name"" WHERE rowid IN (SELECT rowid FROM ""$name"" WHERE ""$col"" < `$cut LIMIT $BatchSize)"
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $sql
        [void]$cmd.Parameters.AddWithValue('$cut', $CutoffUtc)
        $n = $cmd.ExecuteNonQuery()
        $deleted += $n
        $batches++
        if ($n -lt $BatchSize) { break }
        if (($batches % 5) -eq 0) {
            $cp = $conn.CreateCommand()
            $cp.CommandText = 'PRAGMA wal_checkpoint(PASSIVE)'
            [void]$cp.ExecuteNonQuery()
            Write-Output ("       ... {0} deleted={1}" -f $name, $deleted)
        }
    }

    $cp = $conn.CreateCommand()
    $cp.CommandText = 'PRAGMA wal_checkpoint(PASSIVE)'
    [void]$cp.ExecuteNonQuery()

    $remaining = Invoke-ScalarLong $conn ("SELECT COUNT(*) FROM ""{0}""" -f $name) $null
    $grandDone += $deleted
    Write-Output ("[DEL]  {0,-29} deleted={1,9}  batches={2,5}  remaining={3,9}" -f $name, $deleted, $batches, $remaining)
}

$free = Invoke-ScalarLong $conn 'PRAGMA freelist_count' $null
$pages = Invoke-ScalarLong $conn 'PRAGMA page_count' $null
$pageSize = Invoke-ScalarLong $conn 'PRAGMA page_size' $null
$conn.Close()

Write-Output ''
Write-Output ("TOTAL_ROWS_SEEN=" + $grandTotal)
if ($Apply) { Write-Output ("TOTAL_DELETED=" + $grandDone) }
Write-Output ("PAGES=" + $pages + " FREE_PAGES=" + $free + " PAGE_SIZE=" + $pageSize)
if ($pages -gt 0) {
    Write-Output ("RECLAIMABLE_MB=" + [Math]::Round(($free * $pageSize) / 1MB, 1) + "  (needs VACUUM, which needs exclusive access)")
}
$sizeAfter = (Get-Item $DbPath).Length
Write-Output ("SIZE_AFTER_MB=" + [Math]::Round($sizeAfter / 1MB, 1) + "  (file stays this size until VACUUM)")

exit 0
