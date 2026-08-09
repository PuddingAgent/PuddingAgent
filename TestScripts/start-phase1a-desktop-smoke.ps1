param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot
)

$ErrorActionPreference = 'Stop'

function Get-FreeIpv4Port {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

$resolvedPublishRoot = [System.IO.Path]::GetFullPath($PublishRoot)
$desktopExecutable = Join-Path $resolvedPublishRoot 'PuddingDesktop.exe'
$coreExecutable = Join-Path $resolvedPublishRoot 'core\PuddingAgent.exe'
$adminIndex = Join-Path $resolvedPublishRoot 'core\wwwroot\admin\index.html'

foreach ($requiredPath in @($desktopExecutable, $coreExecutable, $adminIndex)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Phase 1A publish artifact is missing: $requiredPath"
    }
}

$smokeParent = Join-Path ([System.IO.Path]::GetTempPath()) 'PuddingAgent'
$smokeRoot = Join-Path $smokeParent ("desktop-smoke-" + [Guid]::NewGuid().ToString('N'))
$desktopHome = Join-Path $smokeRoot 'desktop-home'
$dataRoot = Join-Path $smokeRoot 'data'
$configRoot = Join-Path $dataRoot 'config'
New-Item -ItemType Directory -Path $desktopHome, $configRoot -Force | Out-Null
$corePort = Get-FreeIpv4Port

$desktopConfig = [ordered]@{
    schemaVersion = 1
    dataRoot = $dataRoot
    coreExecutablePath = $null
    window = [ordered]@{
        width = 1280
        height = 800
        isMaximized = $false
    }
}
$desktopConfig | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $desktopHome 'desktop.json') -Encoding utf8

$systemConfig = [ordered]@{
    environment = 'development'
    desktop = [ordered]@{
        core = [ordered]@{
            autoStart = $true
            port = $corePort
            startupTimeoutSeconds = 120
            shutdownTimeoutSeconds = 15
            controlToken = $null
        }
    }
}
$systemConfig | ConvertTo-Json -Depth 5 |
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

[ordered]@{
    desktopPid = $desktopProcess.Id
    desktopMainWindowHandle = $desktopProcess.MainWindowHandle.ToInt64()
    smokeRoot = $smokeRoot
    desktopHome = $desktopHome
    dataRoot = $dataRoot
    desktopExecutable = $desktopExecutable
    coreExecutable = $coreExecutable
    coreListenAddress = "http://0.0.0.0:$corePort"
} | ConvertTo-Json -Compress
