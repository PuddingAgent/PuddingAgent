param([string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunId must be a simple directory name.' }
$run = Join-Path $repo ".work/plugin-events/$RunId"
$artifacts = Join-Path $run 'artifacts'
New-Item -ItemType Directory -Force $run | Out-Null
New-Item -ItemType Directory -Force (Join-Path $run 'temp') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $run 'plugin-root') | Out-Null
function Invoke-Checked([string]$Name, [scriptblock]$Action) {
    & $Action > (Join-Path $run "$Name.log") 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Name failed. See $run/$Name.log" }
    Write-Host "$Name passed"
}
Push-Location $repo
$previousTemp = $env:TEMP; $previousTmp = $env:TMP
$previousWorker = $env:PUDDING_PLUGIN_WORKER; $previousFixture = $env:PUDDING_PLUGIN_FIXTURE
$previousRoot = $env:PUDDING_PLUGIN_TEST_ROOT
try {
    # The plugin acceptance baseline needs a repo-local TEMP: import I/O errors were observed with the system temp.
    $env:TEMP = Join-Path $run 'temp'; $env:TMP = $env:TEMP
    Invoke-Checked 'build-worker' { dotnet build Source/Pudding.ToolPlugins.Worker/Pudding.ToolPlugins.Worker.csproj --artifacts-path $artifacts --nologo }
    Invoke-Checked 'build-fixture' { dotnet build Tests/PuddingToolPlugins.Fixture/PuddingToolPlugins.Fixture.csproj --artifacts-path $artifacts --nologo }
    Invoke-Checked 'build-demo-tests' { dotnet build Tests/PuddingToolPlugins.IntegrationTests/PuddingToolPlugins.IntegrationTests.csproj --artifacts-path $artifacts "--p:UseVSTest=false" --nologo }
    $env:PUDDING_PLUGIN_WORKER = (Resolve-Path "$artifacts/bin/Pudding.ToolPlugins.Worker/debug/Pudding.ToolPlugins.Worker.exe").Path
    $env:PUDDING_PLUGIN_FIXTURE = (Resolve-Path "$artifacts/bin/PuddingToolPlugins.Fixture/debug/Sample.PluginFixture.dll").Path
    $env:PUDDING_PLUGIN_TEST_ROOT = (Resolve-Path (Join-Path $run 'plugin-root')).Path
    Invoke-Checked 'workspace-event-demo' {
        & "$artifacts/bin/PuddingToolPlugins.IntegrationTests/debug/PuddingToolPlugins.IntegrationTests.exe" `
            --results-directory (Join-Path $run 'results') --filter 'PluginWorkspaceEventDemo'
    }
    Write-Host "Workspace event demo passed. Evidence: $run"
}
finally {
    $env:TEMP = $previousTemp; $env:TMP = $previousTmp
    $env:PUDDING_PLUGIN_WORKER = $previousWorker; $env:PUDDING_PLUGIN_FIXTURE = $previousFixture
    $env:PUDDING_PLUGIN_TEST_ROOT = $previousRoot
    Pop-Location
}
