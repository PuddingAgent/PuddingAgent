#Requires -Version 5.1
<#
.SYNOPSIS
    Read-only house inspection for the Pudding platform.

.DESCRIPTION
    One pass over the things that fail silently: database growth and reclaimable
    pages, delivery-queue backlog, repeated error-log signatures, and process
    freshness. Strictly read-only: every database is opened with Mode=ReadOnly
    and nothing on disk is modified.

    Rationale: on 2026-11-17 this platform had 8 messages queued since July, a
    recurring 3.2 KB MCP stack trace on every restart, and a database at 12.2 GB
    with 8 GB of reclaimable free pages — all of it invisible because nothing
    ever looked. Run this daily (or after every restart) to make that visible.

.PARAMETER ErrorFiles
    How many recent non-empty error logs to scan for repeated signatures.

.PARAMETER DataRoot
    Data root of the instance to inspect.

.PARAMETER SummaryOnly
    Report repeated error counts without printing their log signatures.

.PARAMETER Json
    Emit one JSON object instead of a human report.

.EXAMPLE
    inspect-house.ps1
    inspect-house.ps1 -Json

.NOTES
    ASCII-only on purpose: Windows PowerShell 5.1 mis-decodes UTF-8 without BOM.
#>
[CmdletBinding()]
param(
    [int]$ErrorFiles = 20,
    [int]$StaleQueuedDays = 7,
    [int]$AggregateLagDays = 2,
    [switch]$Json,
    [ValidateNotNullOrEmpty()][string]$DataRoot = 'D:\Data',
    [switch]$SummaryOnly
)

$ErrorActionPreference = 'Stop'

$DbPath = Join-Path $DataRoot 'databases\pudding_platform.db'
$LogRoot = Join-Path $DataRoot 'logs'
$ErrorRoot = Join-Path $LogRoot 'error'
$SystemRoot = Join-Path $LogRoot 'system'

function Resolve-SqliteAssembly {
    $bundled = Join-Path (Split-Path -Parent $PSScriptRoot) 'Microsoft.Data.Sqlite.dll'
    if (Test-Path -LiteralPath $bundled) { return $bundled }
    $live = $null
    try {
        $proc = Get-Process PuddingAgent -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc -and $proc.Path) {
            $candidate = Join-Path (Split-Path $proc.Path -Parent) 'Microsoft.Data.Sqlite.dll'
            if (Test-Path $candidate) { $live = $candidate }
        }
    }
    catch { $live = $null }
    if ($live) { return $live }
    $fallback = 'D:\CodeProject\PuddingAgent\PuddingAgent\temp\build\winui3\bin\PuddingDesktop\Debug\net10.0-windows10.0.19041.0\win-x64\core\Microsoft.Data.Sqlite.dll'
    if (Test-Path $fallback) { return $fallback }
    throw 'Microsoft.Data.Sqlite.dll not found.'
}

$report = [ordered]@{
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    processes      = @()
    disk           = @()
    database       = [ordered]@{}
    queue          = [ordered]@{}
    errorPatterns  = @()
    startup        = [ordered]@{}
}

# ---- processes ----------------------------------------------------------
foreach ($n in @('PuddingAgent', 'PuddingDesktop', 'PuddingDesktop.Guardian')) {
    $p = Get-Process $n -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) {
        $report.processes += [ordered]@{
            name      = $n
            pid       = $p.Id
            startedAt = $p.StartTime.ToString('yyyy-MM-dd HH:mm:ss')
            workingSetMB = [Math]::Round($p.WorkingSet64 / 1MB, 0)
        }
    }
    else {
        $report.processes += [ordered]@{ name = $n; pid = $null; startedAt = $null; workingSetMB = $null }
    }
}

# ---- disk ---------------------------------------------------------------
foreach ($d in Get-PSDrive -PSProvider FileSystem) {
    if ($d.Name -match '^[CD]$') {
        $total = $d.Used + $d.Free
        $report.disk += [ordered]@{
            drive    = $d.Name
            freeGB   = [Math]::Round($d.Free / 1GB, 1)
            freePct  = if ($total -gt 0) { [Math]::Round(100 * $d.Free / $total, 1) } else { 0 }
        }
    }
}

# ---- database -----------------------------------------------------------
if (Test-Path $DbPath) {
    $sqliteDirectory = Split-Path -Parent (Resolve-SqliteAssembly)
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $nativeDirectory = Join-Path $sqliteDirectory "runtimes\win-$arch\native"
    $env:PATH = "$sqliteDirectory;$nativeDirectory;$env:PATH"
    foreach ($dll in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll')) {
        Add-Type -Path (Join-Path $sqliteDirectory $dll) | Out-Null
    }
    [SQLitePCL.Batteries_V2]::Init()
    $connectionString = New-Object Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    $connectionString.DataSource = $DbPath
    $connectionString.Mode = [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadOnly
    $conn = New-Object Microsoft.Data.Sqlite.SqliteConnection($connectionString.ToString())
    $conn.Open()

    function Get-Scalar($connection, $sql) {
        $cmd = $connection.CreateCommand()
        $cmd.CommandText = $sql
        $v = $cmd.ExecuteScalar()
        if ($null -eq $v -or $v -is [System.DBNull]) { return 0 }
        return [int64]$v
    }

    $pages = Get-Scalar $conn 'PRAGMA page_count'
    $free = Get-Scalar $conn 'PRAGMA freelist_count'
    $pageSize = Get-Scalar $conn 'PRAGMA page_size'

    $report.database = [ordered]@{
        fileMB            = [Math]::Round((Get-Item $DbPath).Length / 1MB, 1)
        walMB             = if (Test-Path "$DbPath-wal") { [Math]::Round((Get-Item "$DbPath-wal").Length / 1MB, 1) } else { 0 }
        pageCount         = $pages
        freePageCount     = $free
        reclaimableMB     = [Math]::Round(($free * $pageSize) / 1MB, 1)
        reclaimablePct    = if ($pages -gt 0) { [Math]::Round(100 * $free / $pages, 1) } else { 0 }
        rowsByTable       = [ordered]@{}
    }

    # Only the known event tables: COUNT(*) on arbitrary tables can be slow.
    foreach ($t in @('conversation_events', 'message_deliveries', 'ChatMessages', 'TokenUsageEvents')) {
        $exists = Get-Scalar $conn ("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{0}'" -f $t)
        if ($exists -gt 0) {
            $report.database.rowsByTable[$t] = Get-Scalar $conn ("SELECT COUNT(*) FROM ""{0}""" -f $t)
        }
    }

    # ---- usage aggregate lag ---------------------------------------------
    # llm_usage_daily_aggregates is filled lazily from the detail tables, so the two
    # can drift apart with nothing surfacing it. That matters because pruning the
    # detail for an unaggregated day destroys the cost history permanently.
    $aggExists = Get-Scalar $conn "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='llm_usage_daily_aggregates'"
    if ($aggExists -gt 0) {
        $cmdAgg = $conn.CreateCommand()
        $cmdAgg.CommandText = 'SELECT COUNT(DISTINCT day_utc), MAX(day_utc) FROM llm_usage_daily_aggregates'
        $rdrAgg = $cmdAgg.ExecuteReader()
        $aggDays = 0
        $aggMax = $null
        if ($rdrAgg.Read()) {
            $aggDays = [int]$rdrAgg.GetInt64(0)
            if (-not $rdrAgg.IsDBNull(1)) { $aggMax = $rdrAgg.GetString(1) }
        }
        $rdrAgg.Close()

        $lagDays = 0
        if ($aggMax) {
            $lagDays = [int](([DateTime]::UtcNow.Date - [DateTime]::Parse($aggMax)).TotalDays)
        }
        $report.database['usageAggregateDays'] = $aggDays
        $report.database['usageAggregateLastDay'] = $aggMax
        $report.database['usageAggregateLagDays'] = $lagDays
        $report.database['usageAggregateLagWarn'] = ($lagDays -gt $AggregateLagDays)
    }

    # ---- queue ----------------------------------------------------------
    $queueExists = Get-Scalar $conn "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='message_deliveries'"
    if ($queueExists -gt 0) {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'SELECT status, COUNT(*), MIN(created_at) FROM message_deliveries GROUP BY status'
        $rdr = $cmd.ExecuteReader()
        $byStatus = [ordered]@{}
        $oldestUtc = [ordered]@{}
        $oldestMs = [ordered]@{}
        while ($rdr.Read()) {
            $st = $rdr.GetString(0)
            $byStatus[$st] = $rdr.GetInt64(1)
            if (-not $rdr.IsDBNull(2)) {
                $ms = $rdr.GetInt64(2)
                $oldestMs[$st] = $ms
                $oldestUtc[$st] = [DateTimeOffset]::FromUnixTimeMilliseconds($ms).UtcDateTime.ToString('yyyy-MM-dd')
            }
        }
        $rdr.Close()

        # A queued row that stays queued for days means nothing is draining that
        # target. Eight rows sat here from 2026-07-23 to 2026-11-17 unnoticed.
        $staleDays = 0
        if ($oldestMs.Contains('queued')) {
            $staleDays = [int]((([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) - $oldestMs['queued']) / 86400000)
        }
        $staleWarning = $staleDays -gt $StaleQueuedDays

        $report.queue = [ordered]@{
            byStatus       = $byStatus
            oldestUtc      = $oldestUtc
            staleQueuedDays = $staleDays
            staleThreshold  = $StaleQueuedDays
            staleWarning    = $staleWarning
        }
    }

    $conn.Close()
}

# ---- error-log signatures ----------------------------------------------
if (Test-Path $ErrorRoot) {
    $files = Get-ChildItem $ErrorRoot -File -Filter '*.log' |
        Where-Object { $_.Length -gt 0 } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First $ErrorFiles

    $sigs = @{}
    foreach ($f in $files) {
        $line = Get-Content $f.FullName -TotalCount 40 |
            Where-Object { $_ -match 'Exception|error|failed' } |
            Select-Object -First 1
        if (-not $line) { $line = '(no signature line)' }
        # Collapse volatile parts so the same failure clusters together.
        $sig = $line -replace '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', '<guid>'
        $sig = $sig -replace '\d{2}:\d{2}:\d{2}(\.\d+)?', '<time>'
        $sig = $sig.Trim()
        if ($sig.Length -gt 160) { $sig = $sig.Substring(0, 160) }
        if (-not $sigs.ContainsKey($sig)) { $sigs[$sig] = 0 }
        $sigs[$sig]++
    }
    foreach ($k in ($sigs.Keys | Sort-Object { -$sigs[$_] })) {
        $pattern = [ordered]@{ occurrences = $sigs[$k] }
        if (-not $SummaryOnly) { $pattern.signature = $k }
        $report.errorPatterns += $pattern
    }
}

# ---- startup evidence ---------------------------------------------------
if (Test-Path $SystemRoot) {
    # The newest log file may be freshly rotated and not yet contain this boot's
    # startup lines, so scan a few recent files and use the first one that has them.
    # (Same trap that made verify-deploy.ps1 report a false red on 2026-11-17.)
    $candidates = Get-ChildItem $SystemRoot -File -Filter 'pudding-*.log' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 20

    $latest = $null
    $text = ''
    foreach ($candidate in $candidates) {
        $body = Get-Content $candidate.FullName -Raw
        if ($body -match 'Startup decryption check|services-registered') {
            $latest = $candidate
            $text = $body
            break
        }
    }

    if ($latest) {
        $report.startup = [ordered]@{
            logFile          = $latest.Name
            keyvaultCheck    = [bool]($text -match 'Startup decryption check passed for (\d+) credentials')
            keyvaultCount    = if ($text -match 'Startup decryption check passed for (\d+) credentials') { [int]$matches[1] } else { 0 }
            diScopeFault     = [bool]($text -match 'Cannot consume scoped service')
        }
    }
}

# ---- output -------------------------------------------------------------
if ($Json) {
    $report | ConvertTo-Json -Depth 6
    exit 0
}

Write-Output '=== house inspection  (read-only) ==='
Write-Output ''
Write-Output '[processes]'
foreach ($p in $report.processes) {
    if ($p.pid) {
        Write-Output ("  {0,-26} pid={1,-7} start={2}  ws={3}MB" -f $p.name, $p.pid, $p.startedAt, $p.workingSetMB)
    }
    else {
        Write-Output ("  {0,-26} NOT RUNNING" -f $p.name)
    }
}

Write-Output ''
Write-Output '[disk]'
foreach ($d in $report.disk) {
    Write-Output ("  {0}: free {1} GB ({2}%)" -f $d.drive, $d.freeGB, $d.freePct)
}

Write-Output ''
Write-Output '[database]'
if ($report.database.Count -gt 0) {
    Write-Output ("  file={0} MB  wal={1} MB  pages={2}  free={3}  reclaimable={4} MB ({5}%)" -f `
        $report.database.fileMB, $report.database.walMB, $report.database.pageCount, `
        $report.database.freePageCount, $report.database.reclaimableMB, $report.database.reclaimablePct)
        foreach ($k in $report.database.rowsByTable.Keys) {
        Write-Output ("  {0,-28} {1,10} rows" -f $k, $report.database.rowsByTable[$k])
    }
    if ($report.database.Contains('usageAggregateLagDays')) {
        Write-Output ("  usage aggregates: days={0} lastDay={1} lag={2}d" -f `
            $report.database.usageAggregateDays, $report.database.usageAggregateLastDay, $report.database.usageAggregateLagDays)
        if ($report.database.usageAggregateLagWarn) {
            Write-Output ("  WARN: usage detail is not aggregated past {0}. Pruning those days would lose cost history; " -f $report.database.usageAggregateLastDay +
                "GET /api/stats/tokens/series?yearMonth=<yyyy-MM> once (admin token) to build it.")
        }
    }
}

Write-Output ''
Write-Output '[delivery queue]'
if ($report.queue.Count -gt 0) {
    foreach ($k in $report.queue.byStatus.Keys) {
        $oldest = $report.queue.oldestUtc[$k]
        Write-Output ("  {0,-14} {1,7}   oldest={2}" -f $k, $report.queue.byStatus[$k], $oldest)
    }
    if ($report.queue.staleWarning) {
        Write-Output ("  WARN: oldest queued row is {0} days old (threshold {1}): nothing is draining that target" -f `
            $report.queue.staleQueuedDays, $report.queue.staleThreshold)
    }
}

Write-Output ''
Write-Output ("[error-log signatures  last {0} non-empty files]" -f $ErrorFiles)
if ($report.errorPatterns.Count -eq 0) {
    Write-Output '  (none)'
}
foreach ($e in $report.errorPatterns) {
    if ($SummaryOnly) {
        Write-Output ("  {0,4} log files with the same signature" -f $e.occurrences)
    }
    else {
        Write-Output ("  {0,4} x {1}" -f $e.occurrences, $e.signature)
    }
}

Write-Output ''
Write-Output '[guardian]'
# The guardian is launched with --adopt-pid and keeps that pid across desktop restarts. A stale
# value means the control plane answers on its port while supervising nothing, and nothing on the
# machine reports it. check-guardian-identity.ps1 owns that verdict; this section only relays it so
# the two cannot drift apart into different definitions of "healthy".
$identityProbe = Join-Path $PSScriptRoot 'check-guardian-identity.ps1'
if (-not (Test-Path $identityProbe)) {
    Write-Output '  (check-guardian-identity.ps1 missing; guardian identity not checked)'
}
else {
    $hostShell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
    $rawProbe = & $hostShell -NoProfile -File $identityProbe -Json 2>$null
    $probeExit = $LASTEXITCODE
    $identity = $null
    try { $identity = $rawProbe | ConvertFrom-Json } catch { }
    if ($null -eq $identity) {
        Write-Output ("  UNKNOWN: probe produced no parsable output (exit {0})" -f $probeExit)
    }
    else {
        foreach ($r in @($identity.Results)) {
            Write-Output ("  {0,-15} guardian={1} adopt={2}" -f $r.Verdict, $r.GuardianId, $r.AdoptPid)
        }
        if (@($identity.Results | Where-Object { $_.Verdict -in @('STALE_ADOPTION', 'NO_GUARDIAN') }).Count -gt 0) {
            Write-Output '  WARN: the running guardian does not supervise the running desktop; restart it with Tools/Dev/redeploy-desktop.ps1 -Apply.'
        }
    }
}

Write-Output ''
Write-Output '[startup]'
if ($report.startup.Count -eq 0) {
    Write-Output '  (no startup log found in the recent window; the process may predate the retained files)'
}
else {
    Write-Output ("  log={0}  keyvaultCheck={1} (n={2})  diScopeFault={3}" -f `
        $report.startup.logFile, $report.startup.keyvaultCheck, $report.startup.keyvaultCount, $report.startup.diScopeFault)
}

exit 0
