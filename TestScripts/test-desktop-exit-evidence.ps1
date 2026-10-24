param([Parameter(Mandatory=$true)][string]$DesktopExe)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $repo ('temp/test-out/desktop-exit-' + [guid]::NewGuid().ToString('N'))
$desktopHome = Join-Path $testRoot 'desktop'
New-Item -ItemType Directory -Path $desktopHome | Out-Null
@{dataRoot=$null;closeBehavior='ExitAndStopCore'} | ConvertTo-Json | Set-Content (Join-Path $desktopHome 'desktop.json')
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
function Wait-File([string]$Path) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $Path)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Timed out: $Path" }
        Start-Sleep -Milliseconds 100
    }
}
function Start-Isolated {
    $info = [Diagnostics.ProcessStartInfo]::new($DesktopExe)
    $info.UseShellExecute = $false
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory = Split-Path $DesktopExe
    $info.Environment['PUDDING_DESKTOP_HOME'] = $desktopHome
    $p = [Diagnostics.Process]::Start($info)
    $owned.Add($p)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $dir = $null
    while (-not $dir) {
        $dir = Get-ChildItem (Join-Path $desktopHome 'logs/desktop-runs') -Directory -ErrorAction SilentlyContinue |
            Where-Object Name -Match "-$($p.Id)-" | Select-Object -First 1
        if ($p.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Isolated Desktop did not start' }
        if (-not $dir) { Start-Sleep -Milliseconds 100 }
    }
    Wait-File (Join-Path $dir.FullName 'observer.json')
    $observerInfo = Get-Content (Join-Path $dir.FullName 'observer.json') -Raw | ConvertFrom-Json
    if (-not $observerInfo.observerPid) { throw 'Observer did not attach' }
    $observer = Get-Process -Id $observerInfo.observerPid
    $owned.Add($observer)
    return @{process=$p;observer=$observer;directory=$dir.FullName}
}
try {
    $first = Start-Isolated
    Stop-Process -Id $first.process.Id -Force
    Wait-File (Join-Path $first.directory 'incident.json')
    $incident = Get-Content (Join-Path $first.directory 'incident.json') -Raw | ConvertFrom-Json
    if ($incident.source -ne 'observer' -or $incident.cause -ne 'abnormal_exit_code') { throw 'Forced exit evidence was not captured' }
    if ($incident.state.process.processId -ne $first.process.Id) { throw 'Evidence belongs to a different process' }

    $second = Start-Isolated
    Stop-Process -Id $second.observer.Id -Force
    if (-not $second.observer.WaitForExit(5000)) { throw 'Observer did not stop' }
    Stop-Process -Id $second.process.Id -Force
    if (-not $second.process.WaitForExit(5000)) { throw 'Desktop did not stop' }
    $third = Start-Isolated
    Wait-File (Join-Path $second.directory 'recovered-exit.json')
    $recovered = Get-Content (Join-Path $second.directory 'recovered-exit.json') -Raw | ConvertFrom-Json
    if ($recovered.source -ne 'next_startup' -or $recovered.cause -ne 'unobserved_termination') { throw 'Missing-observer recovery was not captured' }
    $result = @{passed=$true;checks=@('forced Desktop exit observed','PID identity retained','Desktop plus observer termination recovered at next startup');evidence=$testRoot}
    $result | ConvertTo-Json | Set-Content (Join-Path $testRoot 'results.json')
    $result | ConvertTo-Json
}
finally {
    foreach ($p in $owned) {
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
        $p.Dispose()
    }
}
