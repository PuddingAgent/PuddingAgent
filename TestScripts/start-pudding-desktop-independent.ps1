#requires -Version 5.1
<#
.SYNOPSIS
Starts Desktop through the existing Explorer process, outside a terminal's job tree.
.DESCRIPTION
For development recovery when the invoking agent/terminal owns a kill-on-close job.
Core remains a child supervised by Desktop. This does not change product settings.
Open a File Explorer window before running. Existing Desktop instances are refused
because a second launch would only activate the old, potentially job-owned process.
#>
[CmdletBinding()]
param(
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $PSScriptRoot '..\Source\PuddingDesktop\bin\Debug\net10.0-windows10.0.17763.0\PuddingDesktop.exe'
}
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([IO.Path]::GetFileName($executable) -ne 'PuddingDesktop.exe') {
    throw 'Expected PuddingDesktop.exe.'
}
if (Get-Process PuddingDesktop -ErrorAction SilentlyContinue) {
    throw 'Desktop is already running. Exit it before changing its launch ownership.'
}

# A newly created Shell.Application may execute inside the caller. Obtain the
# application object from a real Explorer window so Explorer performs the launch.
$shell = New-Object -ComObject Shell.Application
$windows = $shell.Windows()
$explorerWindow = $null
foreach ($window in $windows) {
    if ([IO.Path]::GetFileName([string]$window.FullName) -ieq 'explorer.exe') {
        $explorerWindow = $window
        break
    }
}
if ($null -eq $explorerWindow) {
    throw 'Open a File Explorer window, then run this script again.'
}
$explorerShell = $explorerWindow.Document.Application
$explorerShell.ShellExecute($executable, '', [IO.Path]::GetDirectoryName($executable), 'open', 0)

$deadline = [DateTime]::UtcNow.AddSeconds(15)
$desktopProcess = $null
do {
    Start-Sleep -Milliseconds 250
    $desktopProcess = Get-CimInstance Win32_Process -Filter "Name='PuddingDesktop.exe'" |
        Where-Object { $_.ExecutablePath -ieq $executable } | Select-Object -First 1
} while ($null -eq $desktopProcess -and [DateTime]::UtcNow -lt $deadline)
if ($null -eq $desktopProcess) {
    throw 'Explorer did not start the requested Desktop executable.'
}
$parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($desktopProcess.ParentProcessId)"
if ($parent.Name -ine 'explorer.exe') {
    throw 'Desktop started, but Explorer launch ownership could not be verified.'
}
[pscustomobject]@{
    DesktopPid = $desktopProcess.ProcessId
    ParentPid = $desktopProcess.ParentProcessId
    ParentName = $parent.Name
    ExecutablePath = $desktopProcess.ExecutablePath
} | ConvertTo-Json
