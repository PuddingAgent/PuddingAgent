param(
    [string]$DesktopExe = 'temp/build/winui3/bin/PuddingDesktop/debug_win-x64/PuddingDesktop.exe',
    [string]$CoreExe
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$desktopPath = (Resolve-Path (Join-Path $repo $DesktopExe)).Path
$configuredCore = if ([string]::IsNullOrWhiteSpace($CoreExe)) { $null } else { (Resolve-Path (Join-Path $repo $CoreExe)).Path }
$corePath = if ($configuredCore) { $configuredCore } else { (Resolve-Path (Join-Path (Split-Path $desktopPath) 'core/PuddingAgent.exe')).Path }
$testRoot = Join-Path $repo ('temp/test-out/launcher-' + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $testRoot 'data'
$desktopHome = Join-Path $testRoot 'desktop'
New-Item -ItemType Directory -Path (Join-Path $dataRoot 'config'),$desktopHome -Force | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
@{ dataRoot=$dataRoot; coreExecutablePath=$configuredCore; closeBehavior='ExitAndStopCore' } |
    ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $desktopHome 'desktop.json')
@{ desktop=@{ core=@{ port=$port; autoStart=$true; autoRestart=$false; startupTimeoutSeconds=120; shutdownTimeoutSeconds=15 }; bootstrap=@{ httpEnabled=$false; enabled=$false } } } |
    ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $dataRoot 'config/system.json')
$reportPath = Join-Path $testRoot 'smoke.json'
$info = [Diagnostics.ProcessStartInfo]::new($desktopPath)
$info.WorkingDirectory = Split-Path $desktopPath
$info.UseShellExecute = $false
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$info.Environment['PUDDING_DESKTOP_HOME'] = $desktopHome
$info.Environment['PUDDING_LAUNCHER_SMOKE_REPORT'] = $reportPath
$process = [Diagnostics.Process]::Start($info)
Write-Output "Isolated launcher PID $($process.Id); evidence: $testRoot"
$deadline = [DateTime]::UtcNow.AddMinutes(7)
while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (-not $process.HasExited) {
    # Only the harness-owned process tree; never locate or stop the user's product by name.
    $process.Kill($true)
    throw "Launcher smoke timed out. Evidence: $testRoot"
}
if (-not (Test-Path -LiteralPath $reportPath)) { throw "Launcher exited without report ($($process.ExitCode)). Evidence: $testRoot" }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (-not $report.success) { throw "Launcher smoke failed: $($report.error). Evidence: $testRoot" }
if (-not [string]::Equals($report.coreExecutablePath,$corePath,[StringComparison]::OrdinalIgnoreCase)) { throw "Launcher selected an unexpected Core: $($report.coreExecutablePath)" }
foreach ($childPid in $report.corePids) {
    if (Get-Process -Id $childPid -ErrorAction SilentlyContinue) { throw "Core $childPid survived Shell shutdown" }
}
$lease = [IO.File]::Open((Join-Path $dataRoot '.pudding-host.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$lease.Dispose()
try { $client = [Net.Sockets.TcpClient]::new(); $client.Connect('127.0.0.1',$port); throw 'Core port still listening' }
catch [Net.Sockets.SocketException] { }
finally { if ($client) { $client.Dispose() } }
$report | Add-Member externalChecks @('Shell exited','all Core PIDs exited','DataRoot lease released','Core port released')
$report | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $testRoot 'external-acceptance.json')
$report | ConvertTo-Json -Depth 5
