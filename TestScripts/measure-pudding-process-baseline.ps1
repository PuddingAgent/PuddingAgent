#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$DesktopProcessId,
    [Parameter(Mandatory)][int]$CoreProcessId,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(10,600)][int]$DurationSeconds = 120,
    [ValidateRange(1,10)][int]$IntervalSeconds = 2,
    [string]$Label = 'idle'
)
$ErrorActionPreference = 'Stop'
$logicalCpus = [Environment]::ProcessorCount
$inventory = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name)
$desktopEntry = @($inventory | Where-Object { $_.ProcessId -eq $DesktopProcessId -and $_.Name -eq 'PuddingDesktop.exe' })
$coreEntry = @($inventory | Where-Object { $_.ProcessId -eq $CoreProcessId -and $_.Name -eq 'PuddingAgent.exe' -and $_.ParentProcessId -eq $DesktopProcessId })
if ($desktopEntry.Count -ne 1 -or $coreEntry.Count -ne 1) {
    throw 'Desktop/Core PID identity or parent-child relationship changed; refresh before sampling.'
}
$processIds = [Collections.Generic.HashSet[int]]::new()
$null = $processIds.Add($DesktopProcessId)
$null = $processIds.Add($CoreProcessId)
do {
    $changed = $false
    foreach ($entry in $inventory) {
        if ($processIds.Contains([int]$entry.ParentProcessId)) {
            $changed = $processIds.Add([int]$entry.ProcessId) -or $changed
        }
    }
} while ($changed)
$tracked = @($inventory | Where-Object { $processIds.Contains([int]$_.ProcessId) })
$previous = @{}
$samples = [Collections.Generic.List[object]]::new()
$started = [DateTimeOffset]::UtcNow
$timer = [Diagnostics.Stopwatch]::StartNew()
while ($timer.Elapsed.TotalSeconds -le $DurationSeconds) {
    foreach ($entry in $tracked) {
        $process = Get-Process -Id $entry.ProcessId -ErrorAction SilentlyContinue
        if (!$process) { continue }
        $cpu = $process.TotalProcessorTime.TotalSeconds
        $elapsed = $timer.Elapsed.TotalSeconds
        if ($previous.ContainsKey($process.Id)) {
            $prior = $previous[$process.Id]
            $delta = $elapsed - $prior.Elapsed
            $samples.Add([pscustomobject]@{
                ElapsedSeconds = [Math]::Round($elapsed,3)
                ProcessId = $process.Id
                Name = $process.ProcessName
                CpuPercentMachine = [Math]::Round(100 * ($cpu - $prior.Cpu) / $delta / $logicalCpus,3)
                PrivateMiB = [Math]::Round($process.PrivateMemorySize64 / 1MB,2)
                WorkingSetMiB = [Math]::Round($process.WorkingSet64 / 1MB,2)
                Handles = $process.HandleCount
            })
        }
        $previous[$process.Id] = @{ Cpu=$cpu;Elapsed=$elapsed }
        $process.Dispose()
    }
    Start-Sleep -Seconds $IntervalSeconds
}
$summary = @($samples | Group-Object ProcessId | ForEach-Object {
    $rows = @($_.Group)
    [pscustomobject]@{
        ProcessId = $rows[0].ProcessId
        Name = $rows[0].Name
        Samples = $rows.Count
        MeanCpuPercentMachine = [Math]::Round(($rows.CpuPercentMachine | Measure-Object -Average).Average,3)
        MaxCpuPercentMachine = ($rows.CpuPercentMachine | Measure-Object -Maximum).Maximum
        MeanPrivateMiB = [Math]::Round(($rows.PrivateMiB | Measure-Object -Average).Average,2)
        LastPrivateMiB = $rows[-1].PrivateMiB
        LastWorkingSetMiB = $rows[-1].WorkingSetMiB
    }
})
$result = [pscustomobject]@{
    Label=$Label;StartedAtUtc=$started;FinishedAtUtc=[DateTimeOffset]::UtcNow
    LogicalProcessors=$logicalCpus;DurationSeconds=$timer.Elapsed.TotalSeconds
    Notes='PID tree frozen at start; exited/new processes are not equivalent samples. CPU normalized to whole machine. Not a GC or UI latency measurement.'
    Summary=$summary;Samples=$samples
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($resolvedOutput)) -Force
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resolvedOutput -Encoding utf8
$summary | ConvertTo-Json -Depth 4
