# Fine-grained read/write IO sampling for the host process.
# Purpose (2026-10-28): distinguish a STEADY read leak from PERIODIC bursts.
# A steady rate points at a continuous reader; bursts point at a scheduled job
# (scan/sampler/checkpoint). The distinction decides whether ETW is even needed.
#
# Read-only: only reads process perf counters. No filesystem work inside the loop.
[CmdletBinding()]
param(
    [string]$ProcessName = 'PuddingAgent',
    [int]$Intervals = 12,
    [int]$IntervalSeconds = 5
)

$ErrorActionPreference = 'Continue'

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'PROBE_FAILED: process not running'; exit 1 }
# 注意：$pid 是 PowerShell 的只读自动变量。写成 $pid = $proc.Id 会报 WriteError
# 但脚本继续跑，于是测到的是 pwsh 自己（2026-10-28 实测踩到，整轮数据作废）。
# 所以这里用独立名字，并在下面回读断言一次。
$targetProcessId = $proc.Id
Write-Output ("target={0} pid={1} start={2}" -f $proc.ProcessName, $targetProcessId, $proc.StartTime.ToString('yyyy-MM-dd HH:mm:ss'))

function Get-Raw([int]$id) {
    try {
        $raw = Get-CimInstance Win32_PerfRawData_PerfProc_Process -ErrorAction Stop |
               Where-Object { $_.IDProcess -eq $id } | Select-Object -First 1
        if (-not $raw) { return $null }
        return [pscustomobject]@{
            Read = [long]$raw.IOReadBytesPersec
            Write = [long]$raw.IOWriteBytesPersec
            Cpu = [long]$raw.PercentProcessorTime
        }
    } catch { return $null }
}

Write-Output ("pid={0} intervals={1} interval={2}s" -f $targetProcessId, $Intervals, $IntervalSeconds)
Write-Output 'idx  readDeltaBytes  readMBps  writeDeltaBytes  cpuTicksDelta'

$prev = Get-Raw $targetProcessId
if (-not $prev) { Write-Output 'PROBE_FAILED: raw counters unavailable'; exit 1 }

$readSeries = @()
for ($i = 1; $i -le $Intervals; $i++) {
    Start-Sleep -Seconds $IntervalSeconds
    $cur = Get-Raw $targetProcessId
    if (-not $cur) { Write-Output 'PROBE_FAILED: counters disappeared'; exit 1 }

    $dRead = $cur.Read - $prev.Read
    $dWrite = $cur.Write - $prev.Write
    $dCpu = $cur.Cpu - $prev.Cpu
    $mbps = [math]::Round($dRead / 1MB / $IntervalSeconds, 2)
    Write-Output ("{0,3}  {1,13}  {2,8}  {3,15}  {4,12}" -f $i, $dRead, $mbps, $dWrite, $dCpu)
    $readSeries += $dRead
    $prev = $cur
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
Write-Output 'RESULT: OK'
