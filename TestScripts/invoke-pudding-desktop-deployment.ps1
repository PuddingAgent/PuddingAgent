#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('StopAndBackup','Deploy','Verify')][string]$Action,
    [string]$BackupManifestPath,
    [string]$ManifestPath = 'Docs/Reports/pudding-agent-round2-build-2026-09-05.json',
    [string]$OutputPath = '.tmp-test-out/efficiency-results/round3-deployment.json'
)
function Test-CoreQuiescent {
    param([object]$Snapshot, [bool]$ProcessExists)
    # Diagnostics retains LastProcessId after shutdown; the ID is not a liveness signal.
    return $null -ne $Snapshot -and $Snapshot.coreState -in @('Stopped','Idle') -and !$ProcessExists
}
function Test-DiagnosticCoreQuiescent {
    param([object]$Snapshot)
    $processExists = $Snapshot.coreProcessId -and $null -ne (Get-Process -Id $Snapshot.coreProcessId -ErrorAction SilentlyContinue)
    return Test-CoreQuiescent -Snapshot $Snapshot -ProcessExists ([bool]$processExists)
}
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dataRoot = 'D:\data'
$controlBase = 'http://127.0.0.1:8199/desktop/bootstrap'
$systemConfig = Get-Content -LiteralPath (Join-Path $dataRoot 'config\system.json') -Raw | ConvertFrom-Json
$headers = @{ 'X-Control-Token'=$systemConfig.desktop.core.controlToken }
if ([string]::IsNullOrWhiteSpace($headers['X-Control-Token'])) { throw 'Desktop control credential missing' }
$manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot $ManifestPath) -Raw | ConvertFrom-Json
$bundleRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $manifest.bundlePath))
$artifactRoot = Join-Path $bundleRoot 'core'
foreach ($entry in $manifest.sha256.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath (Join-Path $bundleRoot $entry.Name)).Hash -ne $entry.Value) {
        throw "Prepared artifact hash changed: $($entry.Name)"
    }
}
$diagnostics = (Invoke-RestMethod "$controlBase/diagnostics" -Headers $headers -TimeoutSec 10).diagnostics
if (!$diagnostics -or $diagnostics.dataRoot -ne $dataRoot) { throw 'Desktop DataRoot mismatch' }
if ($diagnostics.bootstrapBusy -or $diagnostics.frontendDeployBusy) { throw 'Desktop deployment is busy' }
$targetRoot = [IO.Path]::GetDirectoryName($diagnostics.coreExecutablePath)
if ($targetRoot -ne (Join-Path $repositoryRoot 'Source\PuddingAgent\bin\Debug\net10.0')) { throw 'Unexpected deployment target' }

if ($Action -eq 'StopAndBackup') {
    $active = python -c 'import sqlite3,json; c=sqlite3.connect("file:D:/data/databases/pudding_platform.db?mode=ro",uri=True,timeout=5); print(json.dumps({"commands":c.execute("SELECT count(*) FROM chat_execution_commands WHERE status NOT IN (?,?,?)",("succeeded","failed","cancelled")).fetchone()[0],"runs":c.execute("SELECT count(*) FROM execution_runs WHERE status NOT IN (?,?,?,?)",("succeeded","failed","cancelled","lease_lost")).fetchone()[0],"reservations":c.execute("SELECT count(*) FROM agent_execution_reservations WHERE released_at_utc IS NULL").fetchone()[0]}))' | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or !$active -or $active.commands -or $active.runs -or $active.reservations) { throw 'Active execution/reservation prevents deployment' }
    $oldCoreId = [int]$diagnostics.coreProcessId
    $stop = Invoke-RestMethod "$controlBase/core/stop" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}' -TimeoutSec 60
    if (!$stop.success -or (Get-Process -Id $oldCoreId -ErrorAction SilentlyContinue)) { throw 'Core did not stop' }
    try {
        $backupRoot = Join-Path 'D:\Keys\PuddingDeploymentBackups' ([DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
        $null = New-Item -ItemType Directory -Path $backupRoot
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true,$false)
        $acl.SetOwner([Security.Principal.WindowsIdentity]::GetCurrent().User)
        foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $backupRoot -AclObject $acl
        foreach ($dir in @('databases','config')) {
            Copy-Item -LiteralPath (Join-Path $dataRoot $dir) -Destination (Join-Path $backupRoot $dir) -Recurse
        }
        Copy-Item -LiteralPath $targetRoot -Destination (Join-Path $backupRoot 'core') -Recurse
        Copy-Item -LiteralPath 'C:\Users\huany\AppData\Local\Pudding\desktop.json' -Destination (Join-Path $backupRoot 'desktop.json')
        $checks = @(Get-ChildItem -LiteralPath (Join-Path $dataRoot 'databases') -File | ForEach-Object {
            $copy = Join-Path $backupRoot ('databases\' + $_.Name)
            $hash = (Get-FileHash -LiteralPath $_.FullName).Hash
            if ($hash -ne (Get-FileHash -LiteralPath $copy).Hash) { throw "Backup mismatch: $($_.Name)" }
            [pscustomobject]@{Name=$_.Name;Bytes=$_.Length;Sha256=$hash}
        })
        $stillStopped = (Invoke-RestMethod "$controlBase/diagnostics" -Headers $headers -TimeoutSec 10).diagnostics
        $quiescent = Test-DiagnosticCoreQuiescent -Snapshot $stillStopped
        $backupResult = [pscustomobject]@{BackupRoot=$backupRoot;OldCoreProcessId=$oldCoreId;BackupCompletedAtUtc=[DateTimeOffset]::UtcNow;QuiescentAtCompletion=$quiescent;Databases=$checks;CoreBackup='core';IncludesConfig=$true;WorkspaceArchivesCopied=$false;Access='Current Windows user and SYSTEM only'}
        $backupResult | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backupRoot 'backup-manifest.json') -Encoding utf8
        $backupResult | ConvertTo-Json -Depth 6
        if (!$quiescent) { throw 'Core is not quiescent after backup; preserve snapshot but reacquire a maintenance window before deploying.' }
    } catch {
        # Backup failure must not leave the user's original service stopped.
        $restoreState = (Invoke-RestMethod "$controlBase/diagnostics" -Headers $headers -TimeoutSec 10).diagnostics
        if (Test-DiagnosticCoreQuiescent -Snapshot $restoreState) {
            [Console]::Error.WriteLine('Backup failed; this controller is restoring the original Core through Desktop.')
            $null = Invoke-RestMethod "$controlBase/core/start" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}' -TimeoutSec 90
        }
        throw
    }
} elseif ($Action -eq 'Deploy') {
    if (!(Test-DiagnosticCoreQuiescent -Snapshot $diagnostics)) { throw 'Stop and verify backup before deploying' }
    if (!$BackupManifestPath -or !(Test-Path -LiteralPath $BackupManifestPath)) { throw 'Verified backup manifest is required' }
    $backup = Get-Content -LiteralPath $BackupManifestPath -Raw | ConvertFrom-Json
    if (!$backup.IncludesConfig -or !$backup.Databases.Count -or !$backup.QuiescentAtCompletion -or !(Test-Path -LiteralPath (Join-Path $backup.BackupRoot 'core/PuddingAgent.dll'))) { throw 'Backup manifest is incomplete or Core restarted during backup' }
    $body = @{ requestedBy='codex-round3';yolo=$false;artifactDirectory=$artifactRoot;artifactAssemblySha256=$manifest.sha256.'core/PuddingAgent.dll' } | ConvertTo-Json -Compress
    $deployment = Invoke-RestMethod "$controlBase/core/deploy-restart" -Method Post -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 660
    $deployment | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $repositoryRoot $OutputPath) -Encoding utf8
    $deployment | Select-Object success,deploymentMode,coreRestarted,preparedAssemblySha256,loadedAssemblySha256,preparedArtifactManifestSha256,loadedArtifactManifestSha256,managedArtifactFileCount,errors | ConvertTo-Json -Depth 5
    if (!$deployment.success) { throw 'Desktop reported deployment failure; inspect receipt before retrying' }
} else {
    $checks = @($manifest.sha256.PSObject.Properties | Where-Object { $_.Name.StartsWith('core/') } | ForEach-Object {
        $deployedPath = Join-Path $targetRoot $_.Name.Substring(5)
        [pscustomobject]@{File=$_.Name;Matches=((Get-FileHash -LiteralPath $deployedPath).Hash -eq $_.Value)}
    })
    $adminSource = Join-Path $repositoryRoot $manifest.adminBuildPath
    $adminTarget = Join-Path $targetRoot 'wwwroot/admin'
    $frontendChecks = @(Get-ChildItem -LiteralPath $adminSource -File -Recurse | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($adminSource, $_.FullName)
        $target = Join-Path $adminTarget $relative
        [pscustomobject]@{File=$relative;Matches=((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $_.FullName).Hash)}
    })
    $ready = Invoke-RestMethod ($diagnostics.coreAddress.TrimEnd('/') + '/health/ready') -TimeoutSec 15
    $verification = [pscustomobject]@{VerifiedAtUtc=[DateTimeOffset]::UtcNow;CoreProcessId=$diagnostics.coreProcessId;CoreState=$diagnostics.coreState;DesktopState=$diagnostics.desktopState;CoreReadyAt=$diagnostics.coreReadyAt;Health=$ready.status;Hashes=$checks;FrontendFiles=$frontendChecks.Count;FrontendMismatches=@($frontendChecks | Where-Object { !$_.Matches })}
    if (!$PSBoundParameters.ContainsKey('OutputPath')) { $OutputPath = '.tmp-test-out/efficiency-results/round3-verification.json' }
    $verification | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $repositoryRoot $OutputPath) -Encoding utf8
    $verification | ConvertTo-Json -Depth 5
    if (@($checks | Where-Object { !$_.Matches }).Count -or !$frontendChecks.Count -or $verification.FrontendMismatches.Count -or $ready.status -ne 'ready') { throw 'Deployed artifact or readiness mismatch' }
}
