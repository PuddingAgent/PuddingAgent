param(
    [Parameter(Mandatory=$true)][string]$DesktopExe,
    [switch]$WaitForTrayExit
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$desktopPath = (Resolve-Path -LiteralPath $DesktopExe).Path
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repo '.work')) + [IO.Path]::DirectorySeparatorChar
if (-not $desktopPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Exit smoke requires a separate package under .work; never pass the installed product.'
}
if (Get-Process -Name PuddingDesktop -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $desktopPath }) {
    throw 'The isolated package is already running.'
}
$testRoot = Join-Path $repo ('.work/guardian-exit/smoke-' + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $testRoot 'data'
$desktopHome = Join-Path $testRoot 'home'
New-Item -ItemType Directory -Path (Join-Path $dataRoot 'config'),$desktopHome -Force | Out-Null
function New-Port {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start(); $number=$listener.LocalEndpoint.Port; $listener.Stop(); return $number
}
$corePort=New-Port
do { $desktopPort=New-Port } while ($desktopPort -eq $corePort)
do { $guardianPort=New-Port } while ($guardianPort -in @($corePort,$desktopPort))
$headers=@{'X-Control-Token'=([Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N'))}
@{dataRoot=$dataRoot;closeBehavior='MinimizeToTray'} | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $desktopHome 'desktop.json')
@{desktop=@{
    core=@{port=$corePort;controlToken=$headers['X-Control-Token'];autoStart=$true;autoRestart=$false;startupTimeoutSeconds=60;shutdownTimeoutSeconds=10}
    bootstrap=@{httpEnabled=$true;httpPort=$desktopPort;enabled=$false;autoYolo=$false}
    guardian=@{enabled=$true;httpPort=$guardianPort;repositoryRoot=$repo;configuration='Debug';startupTimeoutSeconds=60;stableSeconds=2;pollSeconds=1;failureThreshold=2;buildTimeoutSeconds=120}
}} | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $dataRoot 'config/system.json')
$baseUrl="http://127.0.0.1:$guardianPort"
function Get-Status { Invoke-RestMethod "$baseUrl/guardian/status" -Headers $headers -NoProxy -TimeoutSec 4 }
function Get-Product { Invoke-RestMethod "http://127.0.0.1:$desktopPort/desktop/guardian/health" -Headers $headers -NoProxy -TimeoutSec 4 }
function Send-Command([string]$Action,[string]$Target) {
    $id=[Guid]::NewGuid().ToString('N')
    $receipt=Invoke-RestMethod "$baseUrl/guardian/commands" -Method Post -Headers $headers -ContentType application/json -Body (@{requestId=$id;action=$Action;target=$Target}|ConvertTo-Json) -NoProxy -TimeoutSec 8
    if (-not $receipt.accepted) {throw 'Command rejected'}
    return $id
}
function Wait-Phase([string]$Phase,[string]$RequestId='') {
    $deadline=[DateTime]::UtcNow.AddSeconds(150)
    do {
        try {
            $status=Get-Status
            if (-not $status.busy -and ($RequestId -eq '' -or $status.requestId -eq $RequestId)) {
                if ($status.phase -eq $Phase) {return $status}
                if ($status.phase -in @('failed','faulted')) {throw "Lifecycle failed: $($status.error)"}
            }
        } catch [Net.Http.HttpRequestException] {} catch [System.Threading.Tasks.TaskCanceledException] {}
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Phase; evidence=$testRoot"
}
function Start-Desktop {
    $info=[Diagnostics.ProcessStartInfo]::new($desktopPath)
    $info.UseShellExecute=$false
    $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory=Split-Path $desktopPath
    $info.Environment['PUDDING_DESKTOP_HOME']=$desktopHome
    return [Diagnostics.Process]::Start($info)
}
function Get-Tree {
    $status=Wait-Phase healthy
    $product=Get-Product
    $desktop=Get-CimInstance Win32_Process -Filter "ProcessId=$($product.desktopProcessId)"
    $core=Get-CimInstance Win32_Process -Filter "ProcessId=$($product.diagnostics.coreProcessId)"
    if ($desktop.ParentProcessId -ne $status.processId -or $core.ParentProcessId -ne $desktop.ProcessId) {throw 'Expected Guardian -> Desktop -> Core parent identities'}
    return @{guardianPid=[int]$status.processId;desktopPid=[int]$desktop.ProcessId;corePid=[int]$core.ProcessId}
}
function Wait-AllExited([hashtable]$Tree,[int]$Seconds=90) {
    $processes=@{}
    foreach ($name in @('corePid','desktopPid','guardianPid')) {
        $processes[$name]=Get-Process -Id $Tree[$name] -ErrorAction SilentlyContinue
        if ($processes[$name]) {$null=$processes[$name].Handle}
    }
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $live=@($Tree.Values | Where-Object {Get-Process -Id $_ -ErrorAction SilentlyContinue})
        if ($live.Count -eq 0) {break}
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($live.Count) {throw "Processes remained after exit; evidence=$testRoot"}
    $state=Get-Content (Join-Path $desktopHome 'guardian/state.json') -Raw | ConvertFrom-Json
    if ($state.phase -ne 'exited' -or -not $state.exitRequest.completed) {throw 'Guardian did not persist the completed exit'}
    $times=@{}
    foreach ($name in $processes.Keys) {if ($processes[$name]) {$times[$name]=$processes[$name].ExitTime.ToUniversalTime()}}
    if ($times.corePid -and $times.desktopPid -and $times.corePid -gt $times.desktopPid) {throw 'Core exited after Desktop'}
    if ($times.desktopPid -and $times.guardianPid -and $times.desktopPid -gt $times.guardianPid) {throw 'Desktop exited after Guardian'}
    $lease=[IO.File]::Open((Join-Path $dataRoot '.pudding-host.lock'),'Open','ReadWrite','None'); $lease.Dispose()
    return $times
}
$checks=[Collections.Generic.List[string]]::new()
try {
    $bootstrap=Start-Desktop
    $tree=Get-Tree
    $checks.Add('fresh launch hands off to Guardian -> Desktop -> Core')
    $stop=Send-Command stop core
    [void](Wait-Phase stopped $stop)
    Start-Sleep -Seconds 3
    if ((Get-Status).monitoring -or (Get-Process -Id $tree.corePid -ErrorAction SilentlyContinue)) {throw 'Manual Core stop was resurrected'}
    $checks.Add('Core stop retains Desktop/Guardian without resurrection')
    $start=Send-Command start core
    [void](Wait-Phase healthy $start)
    $tree=Get-Tree
    $ready=@{testRoot=$testRoot;tree=$tree;desktopExe=$desktopPath;waitForTrayExit=[bool]$WaitForTrayExit}
    $ready|ConvertTo-Json -Depth 5|Set-Content -Encoding utf8 (Join-Path $testRoot 'ready.json')
    Write-Output ($ready|ConvertTo-Json -Depth 5 -Compress)
    if (-not $WaitForTrayExit) {[void](Send-Command exit desktop)}
    $exitTimes=Wait-AllExited $tree $(if($WaitForTrayExit){300}else{90})
    $checks.Add('whole application exit releases all three processes and DataRoot lease in order')
    $bootstrap=Start-Desktop
    $second=Get-Tree
    $checks.Add('explicit relaunch clears completed exit and establishes a fresh process tree')
    [void](Send-Command exit desktop)
    [void](Wait-AllExited $second)
    $report=@{success=$true;checks=@($checks);firstTree=$tree;secondTree=$second;exitTimes=$exitTimes;testRoot=$testRoot}
    $report|ConvertTo-Json -Depth 6|Set-Content -Encoding utf8 (Join-Path $testRoot 'acceptance.json')
    Write-Output ($report|ConvertTo-Json -Depth 6)
} finally {
    # Use the isolated authenticated endpoint; never terminate the installed Guardian or a process tree.
    try {if (-not (Get-Status).exiting) {[void](Send-Command exit desktop)}} catch {}
}
