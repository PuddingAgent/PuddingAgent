# Watch the host's read IO for the "4 KiB page-read storm" signature and capture context
# the moment it reappears.
#
# Why this exists (2026-10-28): the anomaly (820-900 MB/s, ~200k read ops/s, each exactly
# 4096 bytes, 85% kernel time, write 0, physical disk 0) was measured repeatedly between
# 08:10 and 13:23 -- and by 14:58 it had simply stopped. A 180-second, 1-second-resolution
# sample showed max 76.2 MB/s and 178/180 seconds below 30 MB/s.
#
# Attribution cannot continue against a phenomenon that is not present. The forensic value
# is entirely in what gets recorded AT THE MOMENT it recurs:
#   - exact wall-clock timestamps of the busy seconds
#   - the byte/op ratio (must be ~4096 to confirm the same signature)
#   - kernel vs user split (must be kernel-dominated)
#   - the size/mtime of every candidate data file at that instant
#
# Read-only: Get-Counter only. Writes nothing except the report file under temp/.
#
# Exit codes: 0 = ran to completion, 2 = probe failed, 3 = anomaly captured (see report).
param(
    [int]$Intervals = 120,
    [int]$IntervalSeconds = 2,
    [double]$BusyMBps = 100.0,
    [string]$ProcessName = 'PuddingAgent',
    [string]$ReportPath = 'D:\CodeProject\PuddingAgent\PuddingAgent\temp\read-io-anomaly.log'
)

$ErrorActionPreference = 'Stop'

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $proc) {
    Write-Output ("FATAL: process '" + $ProcessName + "' is not running")
    exit 2
}

$pid0 = $proc.Id
$started = $proc.StartTime
$inst = "\Process(" + $proc.ProcessName + ")\"
$counters = @(
    ($inst + 'IO Read Bytes/sec'),
    ($inst + 'IO Read Operations/sec'),
    ($inst + '% Privileged Time'),
    ($inst + '% User Time'),
    ($inst + 'Handle Count')
)

# Candidate data files. Their size+mtime at the instant of the anomaly is what lets a later
# round say "these bytes were being read" instead of re-guessing from source code.
$watchFiles = @(
    'D:\Data\databases\pudding_platform.db',
    'D:\Data\databases\pudding_platform.db-wal',
    'D:\Data\databases\pudding_platform.db-shm',
    'D:\Data\databases\pudding_memory.db',
    'D:\Data\databases\pudding_memory.db-wal',
    'D:\Data\databases\pudding_memory.db-shm'
)

function Read-CounterSet {
    param([string[]]$Paths, [string]$Instance)
    $snap = Get-Counter -Counter $Paths -ErrorAction Stop
    $values = @{}
    foreach ($set in @($snap)) {
        foreach ($s in @($set.CounterSamples)) {
            if ($null -eq $s) { continue }
            # Under Windows PowerShell 5.1 CounterSample.CounterName is the EMPTY STRING;
            # only .Path carries it, as "\\MACHINE\process(name)\counter". Derive from the
            # last path segment or every field silently reads 0 (verified 2026-10-28).
            $path = $s.Path
            if ([string]::IsNullOrEmpty($path)) { continue }
            $sep = $path.LastIndexOf('\')
            $name = $path
            if ($sep -ge 0) { $name = $path.Substring($sep + 1) }
            if ([string]::IsNullOrEmpty($name)) { continue }
            $name = $name.ToLowerInvariant()
            if ($values.ContainsKey($name)) { continue }
            if ($null -eq $s.InstanceName -or $s.InstanceName -eq $Instance) {
                $values[$name] = [double]$s.CookedValue
            }
        }
    }
    return $values
}

# Warm up (rate counters need two samples).
$null = Read-CounterSet -Paths $counters -Instance $proc.ProcessName

$header = "=== read-io watch " + (Get-Date -Format 'o') + " pid=" + $pid0 + " started=" + $started.ToString('o') + " ==="
Write-Output $header
Add-Content -Path $ReportPath -Value $header -Encoding UTF8

$busyHits = 0
$maxSeen = 0.0

for ($i = 0; $i -lt $Intervals; $i++) {
    Start-Sleep -Seconds $IntervalSeconds
    $v = Read-CounterSet -Paths $counters -Instance $proc.ProcessName

    $bytes = 0.0; $ops = 0.0; $priv = 0.0; $user = 0.0; $handles = 0.0
    if ($v.ContainsKey('io read bytes/sec')) { $bytes = $v['io read bytes/sec'] }
    if ($v.ContainsKey('io read operations/sec')) { $ops = $v['io read operations/sec'] }
    if ($v.ContainsKey('% privileged time')) { $priv = $v['% privileged time'] }
    if ($v.ContainsKey('% user time')) { $user = $v['% user time'] }
    if ($v.ContainsKey('handle count')) { $handles = $v['handle count'] }

    if ($handles -le 0) {
        Write-Output 'PROBE-FAILED: handle count read 0; counter binding broke (a live process cannot have 0 handles)'
        exit 2
    }

    $mbps = [Math]::Round($bytes / 1MB, 1)
    if ($mbps -gt $maxSeen) { $maxSeen = $mbps }

    if ($mbps -ge $BusyMBps) {
        $busyHits++
        $block = 0
        if ($ops -gt 0) { $block = [int][Math]::Round($bytes / $ops) }
        $line = "ANOMALY " + (Get-Date -Format 'o') + "  MBps=" + $mbps + "  ops=" + [Math]::Round($ops, 0) + "  blockBytes=" + $block + "  priv=" + [Math]::Round($priv, 1) + "  user=" + [Math]::Round($user, 1) + "  handles=" + [int]$handles
        Write-Output $line
        Add-Content -Path $ReportPath -Value $line -Encoding UTF8

        foreach ($f in $watchFiles) {
            if (Test-Path -LiteralPath $f) {
                $it = Get-Item -LiteralPath $f
                $fl = "  FILE " + $f + "  bytes=" + $it.Length + "  mtime=" + $it.LastWriteTimeUtc.ToString('o')
            } else {
                $fl = "  FILE " + $f + "  <absent>"
            }
            Add-Content -Path $ReportPath -Value $fl -Encoding UTF8
        }
    }
}

$footer = "=== done " + (Get-Date -Format 'o') + " intervals=" + $Intervals + " busyHits=" + $busyHits + " maxMBps=" + $maxSeen + " threshold=" + $BusyMBps + " ==="
Write-Output $footer
Add-Content -Path $ReportPath -Value $footer -Encoding UTF8

if ($busyHits -gt 0) { exit 3 }
exit 0
