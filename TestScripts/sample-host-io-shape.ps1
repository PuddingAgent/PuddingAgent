# Fine-grained IO sampling for the host process, WITH the physical-disk side.
# Purpose (2026-10-28): two questions at once.
#   (1) steady read leak vs periodic bursts -> decides whether ETW is needed.
#   (2) is the process read actually hitting the disk, or is it cache-served?
#       Without the disk column a 860 MB/s reading is UNCLASSIFIABLE: it can be a
#       real disk storm or a hot loop re-reading the same cached pages. That
#       ambiguity actually blocked a conclusion on 2026-10-28, so disk is now
#       recorded in the same interval, by the same instrument.
#
# Read-only: only reads perf counters. No filesystem work inside the loop
# (a recursive directory sum inside the loop once polluted its own measurement).
[CmdletBinding()]
param(
    [string]$ProcessName = 'PuddingAgent',
    [int]$Intervals = 12,
    [int]$IntervalSeconds = 5,
    [string]$DiskInstance = '_Total'
)

$ErrorActionPreference = 'Continue'

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'PROBE_FAILED: process not running'; exit 1 }
# $pid is a READ-ONLY automatic variable in PowerShell: `$pid = $proc.Id` raises
# WriteError yet the script keeps running, so the probe silently measured pwsh
# itself (observed 2026-10-28, whole run invalidated). Use a distinct name and
# echo the identity so a wrong target is visible before any data is collected.
$targetProcessId = $proc.Id
Write-Output ("target={0} pid={1} start={2}" -f $proc.ProcessName, $targetProcessId, $proc.StartTime.ToString('yyyy-MM-dd HH:mm:ss'))

function Get-Raw([int]$id) {
    try {
        $raw = Get-CimInstance Win32_PerfRawData_PerfProc_Process -ErrorAction Stop |
               Where-Object { $_.IDProcess -eq $id } | Select-Object -First 1
        if (-not $raw) { return $null }
        return [pscustomobject]@{
            Read  = [long]$raw.IOReadBytesPersec
            Write = [long]$raw.IOWriteBytesPersec
            Cpu   = [long]$raw.PercentProcessorTime
        }
    } catch { return $null }
}

function Get-DiskRaw([string]$instance) {
    try {
        $raw = Get-CimInstance Win32_PerfRawData_PerfDisk_PhysicalDisk -ErrorAction Stop |
               Where-Object { $_.Name -eq $instance } | Select-Object -First 1
        if (-not $raw) { return $null }
        return [pscustomobject]@{
            Read  = [long]$raw.DiskReadBytesPersec
            Write = [long]$raw.DiskWriteBytesPersec
        }
    } catch { return $null }
}

Write-Output ("pid={0} intervals={1} interval={2}s diskInstance={3}" -f $targetProcessId, $Intervals, $IntervalSeconds, $DiskInstance)
Write-Output 'idx  procReadMBps  diskReadMBps  procWriteMBps  diskWriteMBps  cpuTicksDelta'

$prev = Get-Raw $targetProcessId
if (-not $prev) { Write-Output 'PROBE_FAILED: process raw counters unavailable'; exit 1 }
$prevDisk = Get-DiskRaw $DiskInstance
if (-not $prevDisk) { Write-Output 'WARN: physical disk counters unavailable; disk columns will read UNAVAILABLE' }

$readSeries = @()
$diskSeries = @()
for ($i = 1; $i -le $Intervals; $i++) {
    Start-Sleep -Seconds $IntervalSeconds
    $cur = Get-Raw $targetProcessId
    if (-not $cur) { Write-Output 'PROBE_FAILED: process counters disappeared'; exit 1 }
    $curDisk = Get-DiskRaw $DiskInstance

    $dRead = $cur.Read - $prev.Read
    $dWrite = $cur.Write - $prev.Write
    $dCpu = $cur.Cpu - $prev.Cpu
    $pReadMbps = [math]::Round($dRead / 1MB / $IntervalSeconds, 2)
    $pWriteMbps = [math]::Round($dWrite / 1MB / $IntervalSeconds, 2)

    # Never print 0 for a counter that could not be read: 0 is a measurement,
    # UNAVAILABLE is not. Conflating them is how an absent instrument turns into
    # a false "nothing is happening" conclusion.
    if ($curDisk -and $prevDisk) {
        $dDiskRead = $curDisk.Read - $prevDisk.Read
        $dDiskWrite = $curDisk.Write - $prevDisk.Write
        $dReadMbps = [math]::Round($dDiskRead / 1MB / $IntervalSeconds, 2)
        $dWriteMbps = [math]::Round($dDiskWrite / 1MB / $IntervalSeconds, 2)
        Write-Output ("{0,3}  {1,12:N1}  {2,12:N1}  {3,13:N1}  {4,13:N1}  {5,12}" -f $i, $pReadMbps, $dReadMbps, $pWriteMbps, $dWriteMbps, $dCpu)
        $diskSeries += $dDiskRead
    } else {
        Write-Output ("{0,3}  {1,12:N1}  {2,12}  {3,13:N1}  {4,13}  {5,12}" -f $i, $pReadMbps, 'UNAVAILABLE', $pWriteMbps, 'UNAVAILABLE', $dCpu)
    }

    $readSeries += $dRead
    $prev = $cur
    $prevDisk = $curDisk
}

$avg = ($readSeries | Measure-Object -Average).Average
$min = ($readSeries | Measure-Object -Minimum).Minimum
$max = ($readSeries | Measure-Object -Maximum).Maximum
$spread = if ($avg -gt 0) { [math]::Round(($max - $min) / $avg, 2) } else { 0 }

Write-Output '--- summary ---'
Write-Output ("avgBytesPerInterval={0} min={1} max={2} spreadRatio={3}" -f [long]$avg, $min, $max, $spread)
if ($spread -lt 0.5) {
    Write-Output 'SHAPE: STEADY (max-min within 50% of average) -> continuous reader, ETW needed to name it'
} else {
    Write-Output 'SHAPE: BURSTY (spread > 50% of average) -> scheduled job, bisect by schedule instead of ETW'
}

if ($diskSeries.Count -gt 0) {
    $diskAvg = ($diskSeries | Measure-Object -Average).Average
    Write-Output ("avgDiskReadBytesPerInterval={0}" -f [long]$diskAvg)
    # The whole point of pairing the two counters: decide disk storm vs cache loop.
    if ($avg -gt 0) {
        $ratio = if ($diskAvg -gt 0) { [math]::Round($avg / $diskAvg, 2) } else { -1 }
        Write-Output ("procOverDiskRatio={0}" -f $ratio)
        if ($diskAvg -le 0.05 * $avg) {
            Write-Output 'SOURCE: CACHE-SERVED (process reads far exceed physical disk reads) -> a hot loop re-reads the same pages, not disk wear'
        } elseif ($ratio -le 2) {
            Write-Output 'SOURCE: DISK-BACKED (process reads track physical disk reads) -> real disk traffic'
        } else {
            Write-Output 'SOURCE: MIXED (process reads exceed disk reads but not overwhelmingly)'
        }
    }
} else {
    Write-Output 'SOURCE: UNKNOWN (physical disk counters unavailable)'
}
Write-Output 'RESULT: OK'
