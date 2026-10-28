# Read-only probe: what SHAPE does the host's sustained read IO have?
#
# Why this exists (2026-10-28): the host process reads 830~900 MB/s continuously while
# writing ~0 and touching physical disk ~0. Every earlier attempt to name the reader failed
# for lack of a per-file attribution tool (procmon / xperf absent; wpr needs admin).
# Eliminations so far, each with its own probe:
#   - not the probe itself      (two different run modes produced the same number)
#   - not agent turns           (240 x 5s window across real idle time: 100% of intervals busy)
#   - not the code index        (code_index.db-wal bytes=0, mtime unchanged)
#   - not the full-text index   (checkpoint.generation frozen for 125s)
#   - not writes                (procWriteMBps 0.0; IO Data Operations == IO Read Operations)
#   - not log flushing          (10s window: 2 files changed, 1623 bytes total)
#   - not file enumeration      (handle count steady 962~973 across 8 intervals)
#   - not mmap                  (no mmap_size / MemoryMappedFile in the component sources)
#
# What is left is a 4 KiB page-read loop on an ALREADY-OPEN handle. This script pins that
# shape down as a number so the next round does not have to re-derive it by hand.
#
# Shape classification (block size = bytes / operations):
#   PAGE-READ  block ~4 KiB     -> SQLite page reads or a 4096-byte FileStream loop
#   BULK-READ  block >= 64 KiB  -> large sequential reads (file copy / full-file load)
#   MIXED      in between
# Handle volatility separates "reopening many files" (FILE-CHURN) from "same handle".
#
# Two instrument traps this script now guards against, both found the hard way:
#   (1) CounterSamples .Path is "\\MACHINE\process(name)\counter", machine name included and
#       lower-cased. Matching a locally built path silently yields 0 for EVERY field, which
#       looks exactly like "the host is quiet". => match on CounterName + InstanceName.
#   (2) Get-Counter -MaxSamples returns a set collection whose shape differs by engine.
#       => sample one set at a time and never index into a possibly-null array.
#
# Strictly read-only: Get-Counter only, never touches the host process or its files.
# Exit codes: 0 = shape captured, 2 = probe failed, 3 = host quiet (nothing to classify).
[CmdletBinding()]
param(
    [string]$ProcessName = 'PuddingAgent',
    [int]$Intervals = 12,
    [int]$IntervalSeconds = 5,
    [double]$QuietThresholdMBps = 5.0
)

$ErrorActionPreference = 'Stop'

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $proc) {
    Write-Output ("FATAL: process '" + $ProcessName + "' is not running")
    exit 2
}

$inst = "\Process(" + $proc.ProcessName + ")\"
$counterPaths = @(
    ($inst + 'IO Read Operations/sec'),
    ($inst + 'IO Read Bytes/sec'),
    ($inst + 'IO Data Operations/sec'),
    ($inst + 'Handle Count'),
    ($inst + '% Processor Time')
)

Write-Output ("target   = " + $proc.ProcessName + " pid=" + $proc.Id)
Write-Output ("sampling = " + $Intervals + " x " + $IntervalSeconds + "s")

function Read-OneSet {
    param([string[]]$Paths, [string]$Instance)

    # Two verified traps (2026-10-28), both of which made every field read 0 and look like
    # "the host is quiet":
    #   (1) Get-Counter returns ONE CounterSampleSet PER COUNTER when given a counter array,
    #       so a single-set assumption yields a member-enumerated array of nulls.
    #   (2) Under Windows PowerShell 5.1, CounterSample.CounterName is the EMPTY STRING; only
    #       .Path carries the name, as "\\MACHINE\process(name)\counter". An IsNullOrEmpty
    #       guard on CounterName therefore discards the only usable field.
    # => derive the counter name from the last path segment, and compare case-insensitively.
    $snap = Get-Counter -Counter $Paths -ErrorAction Stop
    $values = @{}
    foreach ($set in @($snap)) {
        foreach ($s in @($set.CounterSamples)) {
            if ($null -eq $s) { continue }
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

# Warm-up: a rate counter's first sample is always 0, so discard one set.
try {
    $null = Read-OneSet -Paths $counterPaths -Instance $proc.ProcessName
}
catch {
    Write-Output ("FATAL: counters did not resolve for '" + $proc.ProcessName + "': " + $_.Exception.Message)
    exit 2
}

$rows = @()
for ($i = 0; $i -lt $Intervals; $i++) {
    Start-Sleep -Seconds $IntervalSeconds
    $v = Read-OneSet -Paths $counterPaths -Instance $proc.ProcessName

    $ops = 0.0; $bytes = 0.0; $data = 0.0; $handles = 0.0; $cpu = 0.0
    if ($v.ContainsKey('io read operations/sec')) { $ops = $v['io read operations/sec'] }
    if ($v.ContainsKey('io read bytes/sec')) { $bytes = $v['io read bytes/sec'] }
    if ($v.ContainsKey('io data operations/sec')) { $data = $v['io data operations/sec'] }
    if ($v.ContainsKey('handle count')) { $handles = $v['handle count'] }
    if ($v.ContainsKey('% processor time')) { $cpu = $v['% processor time'] }

    $block = 0
    if ($ops -gt 0) { $block = [int][Math]::Round($bytes / $ops) }

    $rows += [pscustomobject]@{
        Ops       = [long]$ops
        MBps      = [Math]::Round($bytes / 1048576.0, 1)
        BlockSize = $block
        Handles   = [int]$handles
        CpuPct    = [Math]::Round($cpu, 1)
    }
}

$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Output

# Instrument self-check BEFORE any verdict. A live host always has a four-digit handle count,
# so an all-zero sample set means the BINDING broke, not that the host went idle. Reporting
# that as "quiet" is precisely the false conclusion this probe exists to prevent.
$handleValues = @($rows | ForEach-Object { $_.Handles })
$handleMin = ($handleValues | Measure-Object -Minimum).Minimum
$handleMax = ($handleValues | Measure-Object -Maximum).Maximum
Write-Output "--- summary ---"
Write-Output ("intervals=" + $rows.Count)

if ($null -eq $handleMax -or $handleMax -le 0) {
    Write-Output "handleMin=0 handleMax=0"
    Write-Output "RESULT: PROBE-FAILED - every field read 0 (a live process cannot have 0 handles); counter binding is broken, this is NOT a quiet host"
    exit 2
}

$handleSpread = 0.0
if ($handleMin -gt 0) { $handleSpread = ($handleMax - $handleMin) / $handleMin }
Write-Output ("handleMin=" + $handleMin + " handleMax=" + $handleMax + " handleSpreadRatio=" + [Math]::Round($handleSpread, 4))

$active = @($rows | Where-Object { $_.MBps -ge $QuietThresholdMBps })
Write-Output ("active=" + $active.Count + " quietThresholdMBps=" + $QuietThresholdMBps)

if ($active.Count -eq 0) {
    Write-Output "RESULT: INCONCLUSIVE - host is quiet; nothing to classify"
    exit 3
}

$avgOps = ($active | Measure-Object -Property Ops -Average).Average
$avgMBps = ($active | Measure-Object -Property MBps -Average).Average
$avgBlock = ($active | Measure-Object -Property BlockSize -Average).Average
$avgCpu = ($active | Measure-Object -Property CpuPct -Average).Average

Write-Output ("avgOpsPerSec=" + [Math]::Round($avgOps, 0) + " avgMBps=" + [Math]::Round($avgMBps, 1) + " avgBlockBytes=" + [Math]::Round($avgBlock, 0) + " avgCpuPct=" + [Math]::Round($avgCpu, 1))

$shape = 'MIXED'
if ($avgBlock -le 8192) { $shape = 'PAGE-READ' }
elseif ($avgBlock -ge 65536) { $shape = 'BULK-READ' }

$churn = 'SAME-HANDLE'
if ($handleSpread -gt 0.20) { $churn = 'FILE-CHURN' }

Write-Output ("SHAPE: " + $shape + " (" + $churn + ")")
Write-Output "  PAGE-READ  -> SQLite page reads or a 4096-byte FileStream loop"
Write-Output "  BULK-READ  -> large sequential reads (file copy / full-file load)"
Write-Output "  FILE-CHURN -> many open/close cycles (directory enumeration), not a single stream"
Write-Output "RESULT: OK - shape captured"
exit 0
