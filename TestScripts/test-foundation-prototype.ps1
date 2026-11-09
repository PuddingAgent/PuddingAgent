param([string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'), [switch]$VerifyCore)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunId must be a simple directory name.' }
$run = Join-Path $repo ".work/b-prototype/$RunId"
$artifacts = Join-Path $run 'artifacts'
New-Item -ItemType Directory $run | Out-Null
New-Item -ItemType Directory -Force (Join-Path $run 'temp') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $run 'plugin-root') | Out-Null
$receipt = Join-Path $run 'receipt.json'
function Invoke-Checked([string]$Name, [scriptblock]$Action) {
    & $Action > (Join-Path $run "$Name.log") 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Name failed. See $run/$Name.log" }
    Write-Host "$Name passed"
}
Push-Location $repo
$oldTemp = $env:TEMP; $oldTmp = $env:TMP
$oldWorker = $env:PUDDING_PLUGIN_WORKER; $oldFixture = $env:PUDDING_PLUGIN_FIXTURE
$oldRoot = $env:PUDDING_PLUGIN_TEST_ROOT; $oldReceipt = $env:PUDDING_PROTOTYPE_RECEIPT
try {
    $env:TEMP = Join-Path $run 'temp'; $env:TMP = $env:TEMP
    Invoke-Checked 'build-worker' { dotnet build Source/Pudding.ToolPlugins.Worker/Pudding.ToolPlugins.Worker.csproj --artifacts-path $artifacts --nologo }
    Invoke-Checked 'build-fixture' { dotnet build Tests/PuddingToolPlugins.Fixture/PuddingToolPlugins.Fixture.csproj --artifacts-path $artifacts --nologo }
    Invoke-Checked 'build-prototype' { dotnet build Tests/PuddingToolPlugins.IntegrationTests/PuddingToolPlugins.IntegrationTests.csproj --artifacts-path $artifacts "--p:UseVSTest=false" --nologo }
    if ($VerifyCore) {
        Invoke-Checked 'build-core' { dotnet build Source/PuddingAgent/PuddingAgent.csproj --artifacts-path $artifacts --nologo }
        Invoke-Checked 'core-smoke' {
            py -3 -X utf8 TestScripts/foundation_core_smoke.py "$artifacts/bin/PuddingAgent/debug/PuddingAgent.exe" (Join-Path $run 'core')
        }
    }
    $env:PUDDING_PLUGIN_WORKER = (Resolve-Path "$artifacts/bin/Pudding.ToolPlugins.Worker/debug/Pudding.ToolPlugins.Worker.exe").Path
    $env:PUDDING_PLUGIN_FIXTURE = (Resolve-Path "$artifacts/bin/PuddingToolPlugins.Fixture/debug/Sample.PluginFixture.dll").Path
    $env:PUDDING_PLUGIN_TEST_ROOT = (Resolve-Path (Join-Path $run 'plugin-root')).Path
    $env:PUDDING_PROTOTYPE_RECEIPT = $receipt
    Invoke-Checked 'plugin-exposure' {
        & "$artifacts/bin/PuddingToolPlugins.IntegrationTests/debug/PuddingToolPlugins.IntegrationTests.exe" `
            --results-directory (Join-Path $run 'exposure-results') --filter 'PluginToolExposure'
    }
    Invoke-Checked 'foundation-prototype' {
        & "$artifacts/bin/PuddingToolPlugins.IntegrationTests/debug/PuddingToolPlugins.IntegrationTests.exe" `
            --results-directory (Join-Path $run 'results') --filter 'FoundationPrototype'
    }
    if (-not (Test-Path $receipt)) { throw "Prototype receipt missing: $receipt" }
    $json = Get-Content $receipt -Raw | ConvertFrom-Json
    if ($json.dispatch.IsSuccess -ne $true -or $json.dispatch.ToolFailureCount -ne 0 -or $json.dispatch.echo -cne '插件真实回执') {
        throw 'Work dispatch, tool failures or the actual plugin echo did not match.'
    }
    $required = 'workId', 'operationId', 'artifactId', 'version', 'eventId', 'archiveId', 'archiveOperationId'
    foreach ($name in $required) {
        if ([string]::IsNullOrWhiteSpace([string]$json.$name)) { throw "Receipt field '$name' is empty." }
    }
    if ($json.workId -ne $json.artifactId) { throw 'Artifact must be addressed by the same WorkId.' }
    if ($json.eventId -ne $json.archiveOperationId) { throw 'Archive OperationId must equal the committed EventId.' }
    if ($json.snapshot -ne 'Completed') { throw "Work snapshot is '$($json.snapshot)', expected Completed." }
    if ($json.delivery.Acknowledged -ne 1) { throw "Observer delivery was not acknowledged (ack=$($json.delivery.Acknowledged))." }
    if ($json.replay.archiveCount -ne 1) { throw "Replay must not duplicate the archive (count=$($json.replay.archiveCount))." }
    if ($json.unload.Delivered -ne 0) { throw "Unloaded observer must not receive new events (delivered=$($json.unload.Delivered))." }
    if ($json.unload.archived -ne 0) { throw "Unloaded observer must not archive new artifacts (archived=$($json.unload.archived))." }
    if ($json.reload.archived -ne 1) { throw "Reloaded observer must archive the next artifact (archived=$($json.reload.archived))." }
    if ($json.cancel.status -ne 'Cancelled') { throw "Cancelled Work snapshot is '$($json.cancel.status)', expected Cancelled." }
    if ($json.cancel.artifact -ne $false) { throw "Cancelled Work must not commit an artifact (artifact=$($json.cancel.artifact))." }
    Write-Host 'Receipt:'
    Write-Host ($json | ConvertTo-Json -Compress)
    Write-Host "Foundation prototype passed. Evidence: $run"
}
finally {
    $env:TEMP = $oldTemp; $env:TMP = $oldTmp
    $env:PUDDING_PLUGIN_WORKER = $oldWorker; $env:PUDDING_PLUGIN_FIXTURE = $oldFixture
    $env:PUDDING_PLUGIN_TEST_ROOT = $oldRoot; $env:PUDDING_PROTOTYPE_RECEIPT = $oldReceipt
    Pop-Location
}
