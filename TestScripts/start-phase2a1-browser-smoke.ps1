param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,

    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'

$resolvedPublishRoot = [System.IO.Path]::GetFullPath($PublishRoot)
$desktopExecutable = Join-Path $resolvedPublishRoot 'PuddingDesktop.exe'
$coreExecutable = Join-Path $resolvedPublishRoot 'core\PuddingAgent.exe'
$adminIndex = Join-Path $resolvedPublishRoot 'core\wwwroot\admin\index.html'

foreach ($requiredPath in @($desktopExecutable, $coreExecutable, $adminIndex)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Phase 2A-1 publish artifact is missing: $requiredPath"
    }
}

$otherDesktopProcesses = @(Get-CimInstance Win32_Process -Filter "Name = 'PuddingDesktop.exe'")
if ($otherDesktopProcesses.Count -gt 0) {
    $details = $otherDesktopProcesses |
        Select-Object ProcessId, ExecutablePath, CommandLine |
        ConvertTo-Json -Compress
    throw "Another PuddingDesktop instance is running. Exit it through the tray before smoke: $details"
}

$smokeParent = Join-Path ([System.IO.Path]::GetTempPath()) 'PuddingAgent'
$smokeRoot = Join-Path $smokeParent ("phase2a1-browser-" + [Guid]::NewGuid().ToString('N'))
$desktopHome = Join-Path $smokeRoot 'desktop-home'
$dataRoot = Join-Path $smokeRoot 'data'
$configRoot = Join-Path $dataRoot 'config'
$workbenchUdf = Join-Path $dataRoot 'browser\workbench\user-data'
$agentBrowserUdf = Join-Path $dataRoot 'browser\agent-browser\user-data'
New-Item -ItemType Directory -Path $desktopHome, $configRoot -Force | Out-Null

$desktopConfig = [ordered]@{
    schemaVersion = 1
    dataRoot = $dataRoot
    coreExecutablePath = $null
    closeBehavior = 'ExitAndStopCore'
    window = [ordered]@{
        width = 1360
        height = 840
        isMaximized = $false
    }
}
$desktopConfig | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $desktopHome 'desktop.json') -Encoding utf8

$systemConfig = [ordered]@{
    environment = 'development'
    desktop = [ordered]@{
        core = [ordered]@{
            autoStart = $false
            autoRestart = $true
            port = 0
            startupTimeoutSeconds = 120
            shutdownTimeoutSeconds = 15
            controlToken = $null
        }
    }
}
$systemConfig | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $configRoot 'system.json') -Encoding utf8

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $desktopExecutable
$startInfo.WorkingDirectory = $resolvedPublishRoot
$startInfo.UseShellExecute = $false
$startInfo.Environment['PUDDING_DESKTOP_HOME'] = $desktopHome
$desktopProcess = [System.Diagnostics.Process]::Start($startInfo)
if ($null -eq $desktopProcess) {
    throw 'Failed to start PuddingDesktop.exe.'
}

$createdProcessId = $desktopProcess.Id
$normalExitObserved = $false

try {
    $windowDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $desktopProcess.Refresh()
    } while ((-not $desktopProcess.HasExited) -and
        ($desktopProcess.MainWindowHandle -eq 0) -and
        ([DateTimeOffset]::UtcNow -lt $windowDeadline))

    if ($desktopProcess.HasExited) {
        throw "PuddingDesktop exited during smoke startup with code $($desktopProcess.ExitCode)."
    }
    if ($desktopProcess.MainWindowHandle -eq 0) {
        throw 'PuddingDesktop did not expose a main window within 30 seconds.'
    }

    [ordered]@{
        event = 'desktop-ready'
        desktopPid = $desktopProcess.Id
        desktopMainWindowHandle = $desktopProcess.MainWindowHandle.ToInt64()
        smokeRoot = $smokeRoot
        desktopHome = $desktopHome
        dataRoot = $dataRoot
        workbenchUdf = $workbenchUdf
        agentBrowserUdf = $agentBrowserUdf
        desktopExecutable = $desktopExecutable
        coreExecutable = $coreExecutable
        coreAutoStart = $false
    } | ConvertTo-Json -Compress

    $reportedCorePid = $null
    while (-not $desktopProcess.HasExited) {
        Start-Sleep -Milliseconds 500
        $desktopProcess.Refresh()

        $coreProcess = Get-CimInstance Win32_Process |
            Where-Object {
                $_.Name -eq 'PuddingAgent.exe' -and
                $_.ParentProcessId -eq $desktopProcess.Id
            } |
            Select-Object -First 1

        if ($null -ne $coreProcess -and $reportedCorePid -ne $coreProcess.ProcessId) {
            $reportedCorePid = $coreProcess.ProcessId
            $listenAddresses = @(Get-NetTCPConnection -State Listen -OwningProcess $reportedCorePid -ErrorAction SilentlyContinue |
                Where-Object { $_.LocalAddress -in @('127.0.0.1', '::1') } |
                ForEach-Object { "http://127.0.0.1:$($_.LocalPort)" } |
                Sort-Object -Unique)

            [ordered]@{
                event = 'core-observed'
                desktopPid = $desktopProcess.Id
                corePid = $reportedCorePid
                loopbackAddresses = $listenAddresses
                commandLine = $coreProcess.CommandLine
            } | ConvertTo-Json -Compress
        }
    }

    $normalExitObserved = $true
    $desktopProcess.WaitForExit()

    $childDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        $remainingChildren = @(Get-CimInstance Win32_Process |
            Where-Object { $_.ParentProcessId -eq $createdProcessId })
        if ($remainingChildren.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $childDeadline)

    [ordered]@{
        event = 'desktop-exited'
        desktopPid = $createdProcessId
        exitCode = $desktopProcess.ExitCode
        remainingChildProcessIds = @($remainingChildren | ForEach-Object ProcessId)
        smokeRoot = $smokeRoot
    } | ConvertTo-Json -Compress
}
finally {
    if (-not $normalExitObserved) {
        try {
            $desktopProcess.Refresh()
            if (-not $desktopProcess.HasExited) {
                # This process tree was created by this smoke script under an
                # isolated DataRoot. Cleanup is explicit and scoped to it.
                $desktopProcess.Kill($true)
                $desktopProcess.WaitForExit(10000)
            }
        }
        catch { }
    }

    if (-not $KeepArtifacts) {
        $resolvedSmokeRoot = [System.IO.Path]::GetFullPath($smokeRoot)
        $resolvedSmokeParent = [System.IO.Path]::GetFullPath($smokeParent)
        if (-not $resolvedSmokeRoot.StartsWith(
            $resolvedSmokeParent + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove smoke directory outside expected parent: $resolvedSmokeRoot"
        }
        if (Test-Path -LiteralPath $resolvedSmokeRoot) {
            Remove-Item -LiteralPath $resolvedSmokeRoot -Recurse -Force
        }
    }
}
