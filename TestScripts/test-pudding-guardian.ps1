param(
    [Parameter(Mandatory=$true)][string]$DesktopExe,
    [switch]$SkipBuilds
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$desktopPath = (Resolve-Path -LiteralPath $DesktopExe).Path
if (-not $desktopPath.StartsWith((Join-Path $repo 'temp\build\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Guardian smoke requires an isolated package under this repository temp/build.'
}
if (Get-Process -Name PuddingDesktop -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $desktopPath }) {
    throw 'The smoke package is already running; publish to a separate isolated directory.'
}
$testRoot = Join-Path $repo ('temp/test-out/guardian-' + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $testRoot 'data'
$desktopHome = Join-Path $testRoot 'desktop'
New-Item -ItemType Directory -Path (Join-Path $dataRoot 'config'),$desktopHome -Force | Out-Null
function New-Port {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start()
    $number = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $number
}
$corePort = New-Port
do { $desktopPort = New-Port } while ($desktopPort -eq $corePort)
do { $guardianPort = New-Port } while ($guardianPort -in @($corePort,$desktopPort))
$token = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$headers = @{ 'X-Control-Token'=$token }
$baseUrl = "http://127.0.0.1:$guardianPort"
@{ dataRoot=$dataRoot; closeBehavior='ExitAndStopCore' } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $desktopHome 'desktop.json')
@{ desktop=@{
    core=@{ port=$corePort; controlToken=$token; autoStart=$true; autoRestart=$false; startupTimeoutSeconds=60; shutdownTimeoutSeconds=10 }
    bootstrap=@{ httpEnabled=$true; httpPort=$desktopPort; enabled=$false; autoYolo=$false }
    guardian=@{ enabled=$true; httpPort=$guardianPort; repositoryRoot=$repo; configuration='Debug'; startupTimeoutSeconds=60; stableSeconds=2; pollSeconds=1; failureThreshold=2; buildTimeoutSeconds=900 }
} } | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $dataRoot 'config/system.json')
$checks = [Collections.Generic.List[string]]::new()
$guardianPid = $null
$desktopPids = [Collections.Generic.HashSet[int]]::new()
$corePids = [Collections.Generic.HashSet[int]]::new()
function Get-Status { Invoke-RestMethod -Uri "$baseUrl/guardian/status" -Headers $headers -TimeoutSec 4 -NoProxy }
function Get-Product {
    $snapshot = Invoke-RestMethod -Uri "http://127.0.0.1:$desktopPort/desktop/guardian/health" -Headers $headers -TimeoutSec 4 -NoProxy
    [void]$desktopPids.Add([int]$snapshot.desktopProcessId)
    if ($snapshot.diagnostics.coreProcessId) { [void]$corePids.Add([int]$snapshot.diagnostics.coreProcessId) }
    return $snapshot
}
function Wait-State([string]$RequestId, [string]$Phase, [int]$Seconds=180) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $last = $null
    do {
        try { $last = Get-Status } catch { Start-Sleep -Milliseconds 300; continue }
        if (($RequestId -eq '' -or $last.requestId -eq $RequestId) -and -not $last.busy) {
            if ($last.phase -eq $Phase) { return $last }
            if ($last.phase -in @('failed','faulted')) { throw "Guardian $($last.phase): $($last.error). Evidence: $testRoot" }
        }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Guardian wait timed out. Expected $Phase; last=$($last.phase). Evidence: $testRoot"
}
function Send-Command([hashtable]$Body) {
    $Body.requestId = [Guid]::NewGuid().ToString('N')
    $receipt = Invoke-RestMethod -Uri "$baseUrl/guardian/commands" -Method Post -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 5) -TimeoutSec 10 -NoProxy
    if (-not $receipt.accepted) { throw 'Guardian rejected command' }
    return $Body.requestId
}
function Assert-Hash([string]$Path,[string]$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) { throw "Rollback hash mismatch: $Path" }
}
$process = $null
try {
    $info = [Diagnostics.ProcessStartInfo]::new($desktopPath)
    $info.UseShellExecute = $false
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory = Split-Path $desktopPath
    $info.ArgumentList.Add('--background')
    $info.Environment['PUDDING_DESKTOP_HOME'] = $desktopHome
    $process = [Diagnostics.Process]::Start($info)
    [void]$desktopPids.Add($process.Id)
    Write-Output "Guardian isolated smoke started; evidence: $testRoot"
    $status = Wait-State '' 'healthy'
    $guardianPid = [int]$status.processId
    $product = Get-Product
    $initialDesktop = $product.desktopProcessId
    $initialCore = $product.diagnostics.coreProcessId
    if (-not $status.hasFallback) { throw 'No initial healthy rollback snapshot' }
    $checks.Add('independent authenticated guardian and Desktop/Core health accepted')
    $coreExe = Join-Path (Split-Path $desktopPath) 'core/PuddingAgent.exe'
    $coreDll = [IO.Path]::ChangeExtension($coreExe,'.dll')
    if (-not $SkipBuilds) {
        $id = Send-Command @{ action='build'; target='core' }
        $built = Wait-State $id 'built' 1000
        $product = Get-Product
        if ($product.desktopProcessId -ne $initialDesktop -or $product.diagnostics.coreProcessId -ne $initialCore) { throw 'Build-only restarted product' }
        $checks.Add('guardian compiled Core without stopping running processes')
        $id = Send-Command @{ action='build-restart'; target='core'; deploymentMode='prebuilt-artifact'; artifactDirectory=$built.preparedDirectory }
        [void](Wait-State $id 'healthy')
        $product = Get-Product
        if ($product.desktopProcessId -ne $initialDesktop -or $product.diagnostics.coreProcessId -eq $initialCore) { throw 'Core deployment did not preserve Shell and replace Core PID' }
        $checks.Add('Core deployment replaces Core PID and preserves Desktop PID')
        $coreHash = (Get-FileHash -LiteralPath $coreDll -Algorithm SHA256).Hash
        [IO.File]::WriteAllText((Join-Path $built.preparedDirectory 'PuddingAgent.dll'),'deliberately invalid assembly for isolated rollback smoke')
        $id = Send-Command @{ action='build-restart'; target='core'; deploymentMode='prebuilt-artifact'; artifactDirectory=$built.preparedDirectory }
        [void](Wait-State $id 'rolled-back')
        Assert-Hash $coreDll $coreHash
        [void](Get-Product)
        $checks.Add('bad Core assembly fails readiness and restores previous assembly hash')
        $id = Send-Command @{ action='build'; target='desktop' }
        $builtDesktop = Wait-State $id 'built' 1000
        $checks.Add('guardian compiled complete Desktop and bundled Core in isolated output')
        $shellHash = (Get-FileHash -LiteralPath $desktopPath -Algorithm SHA256).Hash
        [IO.File]::WriteAllText((Join-Path $builtDesktop.preparedDirectory 'PuddingDesktop.exe'),'deliberately invalid executable for isolated rollback smoke')
        $id = Send-Command @{ action='build-restart'; target='desktop'; deploymentMode='prebuilt-artifact'; artifactDirectory=$builtDesktop.preparedDirectory }
        [void](Wait-State $id 'rolled-back')
        Assert-Hash $desktopPath $shellHash
        Assert-Hash $coreDll $coreHash
        [void](Get-Product)
        if ((Get-Status).processId -ne $guardianPid) { throw 'Guardian was replaced or killed during Desktop rollback' }
        $checks.Add('bad Desktop executable rolls back whole pair while original guardian survives')
    }
    $product = Get-Product
    $crashedCore = [Diagnostics.Process]::GetProcessById([int]$product.diagnostics.coreProcessId)
    if (-not [string]::Equals($crashedCore.MainModule.FileName,$coreExe,[StringComparison]::OrdinalIgnoreCase)) { throw 'Core process identity mismatch' }
    $crashedCore.Kill($true)
    $crashedCore.WaitForExit()
    $deadline = [DateTime]::UtcNow.AddSeconds(150)
    do {
        Start-Sleep -Milliseconds 500
        try { $product = Get-Product; $status = Get-Status } catch { continue }
        if ($product.diagnostics.coreState -eq 'Ready' -and $product.diagnostics.coreProcessId -ne $crashedCore.Id -and -not $status.busy) { break }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($product.diagnostics.coreProcessId -eq $crashedCore.Id -or $product.diagnostics.coreState -ne 'Ready') { throw 'Health monitor failed to recover crashed Core' }
    $checks.Add('unexpected Core death is detected and recovered externally')
    $product = Get-Product
    $crashedDesktop = [Diagnostics.Process]::GetProcessById([int]$product.desktopProcessId)
    if (-not [string]::Equals($crashedDesktop.MainModule.FileName,$desktopPath,[StringComparison]::OrdinalIgnoreCase)) { throw 'Desktop process identity mismatch' }
    $crashedDesktop.Kill()
    $crashedDesktop.WaitForExit()
    $deadline = [DateTime]::UtcNow.AddSeconds(150)
    do {
        Start-Sleep -Milliseconds 500
        try { $product = Get-Product; $status = Get-Status } catch { continue }
        if ($product.desktopProcessId -ne $crashedDesktop.Id -and $product.diagnostics.coreState -eq 'Ready' -and -not $status.busy) { break }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($product.desktopProcessId -eq $crashedDesktop.Id -or $product.diagnostics.coreState -ne 'Ready') { throw 'Health monitor failed to recover crashed Desktop' }
    if ((Get-Status).processId -ne $guardianPid) { throw 'Guardian did not survive Desktop crash' }
    $checks.Add('unexpected Desktop death is recovered while the same guardian survives')
    $id = Send-Command @{ action='stop'; target='desktop' }
    [void](Wait-State $id 'stopped')
    Start-Sleep -Seconds 4
    if ((Get-Status).monitoring) { throw 'Explicit stop unexpectedly re-armed' }
    foreach ($ownedPid in @($desktopPids) + @($corePids)) {
        if (Get-Process -Id $ownedPid -ErrorAction SilentlyContinue) { throw "Owned process survived stop: $ownedPid" }
    }
    $lease = [IO.File]::Open((Join-Path $dataRoot '.pudding-host.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $lease.Dispose()
    $checks.Add('explicit stop releases Desktop/Core, DataRoot lease, and suppresses recovery')
    $report = @{ success=$true; checks=@($checks); desktopPids=@($desktopPids); corePids=@($corePids); guardianPid=$guardianPid
        coreAssemblySha256=(Get-FileHash -LiteralPath $coreDll -Algorithm SHA256).Hash
        desktopAssemblySha256=(Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($desktopPath,'.dll')) -Algorithm SHA256).Hash }
    $report | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $testRoot 'acceptance.json')
    $report | ConvertTo-Json -Depth 5
}
finally {
    # Only processes identified by this isolated endpoint/journal and an exact executable path.
    if (-not $guardianPid) { try { $guardianPid = [int](Get-Status).processId } catch { } }
    if ($guardianPid) {
        $ownedGuardian = Get-Process -Id $guardianPid -ErrorAction SilentlyContinue
        if ($ownedGuardian -and [string]::Equals($ownedGuardian.Path,(Join-Path (Split-Path $desktopPath) 'guardian/PuddingDesktop.Guardian.exe'),[StringComparison]::OrdinalIgnoreCase)) { $ownedGuardian.Kill(); $ownedGuardian.WaitForExit() }
    }
    try { [void](Get-Product) } catch { }
    foreach ($ownedPid in @($desktopPids) + @($corePids)) {
        $owned = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
        if ($owned -and $owned.Path -in @($desktopPath,(Join-Path (Split-Path $desktopPath) 'core/PuddingAgent.exe'))) { $owned.Kill($true); $owned.WaitForExit() }
    }
}
