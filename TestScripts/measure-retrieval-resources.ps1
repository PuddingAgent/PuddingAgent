# Measure the retrieval stack's resource footprint for the Pudding host process.
#
# Why this exists (2026-10-28): the first attempt produced a CPU/IO baseline that was
# unusable because the probe itself polluted the measurement:
#   1. it recursively summed a 1.8 GB index directory DURING the sampling window
#      -> disk read was attributed to the host process;
#   2. it read thread count from a CIM projection that has no such property and
#      silently reported 0;
#   3. it used instantaneous rate counters, so "bytes over the window" was undefined.
#
# This version fixes all three:
#   - index footprint is measured ONCE, before the sampling window (cached);
#   - thread count comes from System.Diagnostics.Process and 0 is reported as a
#     probe failure instead of a fact;
#   - IO bytes are derived from Win32_PerfRawData cumulative counters (delta over
#     the window); when unavailable the field says UNAVAILABLE rather than 0.
#
# The sampling window itself performs NO filesystem work. Note the honest limit:
# the host process IS the agent runtime, so a window during an active agent turn
# still contains that turn's CPU. Compare like-for-like windows only.
#
# ASCII-only on purpose: Windows PowerShell 5.1 decodes BOM-less .ps1 as ANSI.
[CmdletBinding()]
param(
    [string]$ProcessName = 'PuddingAgent',
    [int]$WindowSeconds = 20,
    [switch]$SkipFootprint
)

$ErrorActionPreference = 'Continue'

function Write-Section([string]$text) { Write-Output $text }

# ---- Phase A: footprint, BEFORE the sampling window -------------------------
if (-not $SkipFootprint) {
    Write-Section '--- index footprint (measured before the quiet window) ---'
    $targets = @(
        @{ Name = 'fulltext-index'; Path = 'D:\Data\fulltext-index' },
        @{ Name = 'code-index-db'; Path = 'D:\Data\databases\code-index' }
    )
    foreach ($t in $targets) {
        if (-not (Test-Path $t.Path)) { Write-Section ("  {0} MISSING {1}" -f $t.Name, $t.Path); continue }
        $files = Get-ChildItem $t.Path -Recurse -File -ErrorAction SilentlyContinue
        $bytes = ($files | Measure-Object -Property Length -Sum).Sum
        if ($null -eq $bytes) { $bytes = 0 }
        Write-Section ("  {0} files={1} bytes={2} MB={3}" -f `
            $t.Name, @($files).Count, [long]$bytes, [math]::Round($bytes / 1MB, 1))
    }
}

# ---- locate the process -----------------------------------------------------
$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Section 'PROBE_FAILED: process not running'; exit 1 }

$cores = [Environment]::ProcessorCount
Write-Section '--- process ---'
Write-Section ("  name={0} pid={1} started={2} logicalCores={3}" -f `
    $proc.ProcessName, $proc.Id, $proc.StartTime.ToString('yyyy-MM-dd HH:mm:ss'), $cores)

function Get-ThreadCount([int]$id) {
    try {
        return (Get-Process -Id $id -ErrorAction Stop).Threads.Count
    } catch {
        return -1
    }
}

function Get-RawIo([int]$id) {
    try {
        $raw = Get-CimInstance Win32_PerfRawData_PerfProc_Process -ErrorAction Stop |
               Where-Object { $_.IDProcess -eq $id } | Select-Object -First 1
        if (-not $raw) { return $null }
        return [pscustomobject]@{
            Read = [long]$raw.IOReadBytesPersec
            Write = [long]$raw.IOWriteBytesPersec
            Ops = [long]$raw.IODataOperationsPersec
        }
    } catch {
        return $null
    }
}

# ---- Phase B: quiet sampling window ----------------------------------------
Start-Sleep -Seconds 3   # let any footprint IO drain before we start counting

$p1 = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
if (-not $p1) { Write-Section 'PROBE_FAILED: process exited before window'; exit 1 }
$cpu1 = $p1.TotalProcessorTime.TotalMilliseconds
$io1 = Get-RawIo $proc.Id

Start-Sleep -Seconds $WindowSeconds

$p2 = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
if (-not $p2) { Write-Section 'PROBE_FAILED: process exited during window'; exit 1 }
$cpu2 = $p2.TotalProcessorTime.TotalMilliseconds
$io2 = Get-RawIo $proc.Id
$threads = Get-ThreadCount $proc.Id

$elapsedMs = $WindowSeconds * 1000.0
$cpuDeltaMs = $cpu2 - $cpu1
$cpuPercentOfAllCores = [math]::Round(($cpuDeltaMs / $elapsedMs) * 100.0 / $cores, 3)
$cpuPercentOfOneCore = [math]::Round(($cpuDeltaMs / $elapsedMs) * 100.0, 3)

Write-Section ("--- quiet window ({0}s, no filesystem work in-window) ---" -f $WindowSeconds)
Write-Section ("  CPU_DELTA_MS={0}" -f [math]::Round($cpuDeltaMs, 1))
Write-Section ("  CPU_PERCENT_OF_ONE_CORE={0}  CPU_PERCENT_OF_ALL_CORES={1}" -f $cpuPercentOfOneCore, $cpuPercentOfAllCores)
Write-Section ("  WORKING_SET_BYTES={0}  PRIVATE_BYTES={1}" -f $p2.WorkingSet64, $p2.PrivateMemorySize64)

if ($io1 -and $io2) {
    $readBytes = $io2.Read - $io1.Read
    $writeBytes = $io2.Write - $io1.Write
    $ops = $io2.Ops - $io1.Ops
    Write-Section ("  IO_READ_BYTES_OVER_WINDOW={0}  ({1} B/s)" -f $readBytes, [math]::Round($readBytes / $WindowSeconds, 1))
    Write-Section ("  IO_WRITE_BYTES_OVER_WINDOW={0}  ({1} B/s)" -f $writeBytes, [math]::Round($writeBytes / $WindowSeconds, 1))
    Write-Section ("  IO_OPS_OVER_WINDOW={0}" -f $ops)
} else {
    Write-Section '  IO=UNAVAILABLE (Win32_PerfRawData_PerfProc_Process returned no instance)'
}

if ($threads -le 0) {
    Write-Section '  THREADS_PROBE_FAILED (Process.Threads unreadable) - not a fact, do not record as 0'
    Write-Section 'RESULT: PARTIAL'
    exit 2
}

Write-Section ("  THREADS={0}  HANDLES={1}" -f $threads, $p2.HandleCount)
Write-Section 'RESULT: OK'
exit 0
