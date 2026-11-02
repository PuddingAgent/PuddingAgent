<#
.SYNOPSIS
    Verify that the goal-continuation wiring is visible to the RUNNING host process.

.DESCRIPTION
    The gate reads process-level environment variables. A User-level setx only
    reaches the host after the whole launch chain restarts
    (Guardian -> Desktop -> Agent each inherit a startup snapshot).
    This script answers, in one shot:
      1. which process/assembly is actually running, and whether the DLL is newer
         than the process (i.e. a restart is still pending);
      2. what the User-level PUDDING_GOAL_* values currently are;
      3. whether the live log already contains [GoalLoop] decisions.

    ASCII-only on purpose: Windows PowerShell 5.1 decodes BOM-less UTF-8 as ANSI,
    so non-ASCII text in this file would print as mojibake.

.EXAMPLE
    powershell -NoProfile -File TestScripts\verify-goal-continuation.ps1
#>
[CmdletBinding()]
param(
    [string]$LogDirectory = 'D:\Data\logs\system',
    [string]$ProcessName  = 'PuddingAgent'
)

$ErrorActionPreference = 'Stop'
$exit = 0

Write-Output '=== 1. running process / assembly ==='
$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    Write-Output "FAIL: process '$ProcessName' is not running."
    exit 1
}
Write-Output ("PID          : {0}" -f $proc.Id)
Write-Output ("StartedAt    : {0}" -f $proc.StartTime)

$coreDir = Split-Path $proc.Path -Parent
$runtimeDll = Join-Path $coreDir 'PuddingRuntime.dll'
if (Test-Path $runtimeDll) {
    $dll = Get-Item $runtimeDll
    Write-Output ("RuntimeDll   : {0}" -f $dll.LastWriteTime)
    if ($dll.LastWriteTime -gt $proc.StartTime) {
        Write-Output 'WARN         : DLL is newer than the running process - a restart is still pending.'
        $exit = 2
    }
} else {
    Write-Output "WARN         : PuddingRuntime.dll not found next to the process."
}

Write-Output ''
Write-Output '=== 2. User-level environment (what setx wrote) ==='
$names = @(
    'PUDDING_GOAL_OBJECTIVE',
    'PUDDING_GOAL_MAX_ROUNDS',
    'PUDDING_GOAL_CONTINUE',
    'PUDDING_GOAL_DECIDER'
)
foreach ($name in $names) {
    $value = [Environment]::GetEnvironmentVariable($name, 'User')
    if ($null -eq $value) { $value = '<unset>' }
    Write-Output ("{0,-26}: {1}" -f $name, $value)
}

Write-Output ''
Write-Output '=== 3. [GoalLoop] lines in the newest live log ==='
if (-not (Test-Path $LogDirectory)) {
    Write-Output "WARN         : log directory not found: $LogDirectory"
    exit $exit
}
$log = Get-ChildItem -Path $LogDirectory -Filter 'pudding-*.log' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $log) {
    Write-Output 'WARN         : no pudding-*.log found.'
    exit $exit
}
Write-Output ("LogFile      : {0} (modified {1})" -f $log.Name, $log.LastWriteTime)

# The host keeps the log open for writing, so a plain ReadAllLines would throw.
$stream = [IO.File]::Open($log.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
try {
    $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
    $text = $reader.ReadToEnd()
} finally {
    if ($reader) { $reader.Close() }
    $stream.Close()
}

$hits = @($text -split "`r?`n" | Where-Object { $_.Contains('[GoalLoop]') })
if ($hits.Count -gt 0) {
    Write-Output ("GoalLoopLines: {0} (wiring is live)" -f $hits.Count)
    $hits | Select-Object -First 3 | ForEach-Object { Write-Output ("  {0}" -f $_) }
} else {
    Write-Output 'GoalLoopLines: 0'
    Write-Output '  Either the env is not visible to the host (restart still needed),'
    Write-Output '  or no agent loop has finished since the process started.'
}

Write-Output ''
if ($exit -eq 0) { Write-Output 'RESULT: OK' } else { Write-Output 'RESULT: PENDING (see WARN above)' }
exit $exit
