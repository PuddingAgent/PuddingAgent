param([string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunId must be a simple directory name.' }
$run = Join-Path $repo ".work/plugins/$RunId"
$artifacts = Join-Path $run 'artifacts'
New-Item -ItemType Directory -Force $run | Out-Null
function Invoke-Checked([string]$Name, [scriptblock]$Action) {
    & $Action > (Join-Path $run "$Name.log") 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Name failed. See $run/$Name.log" }
    Write-Host "$Name passed"
}
Push-Location $repo
$previousRoot = $env:PUDDING_PLUGIN_TEST_ROOT
$previousWorker = $env:PUDDING_PLUGIN_WORKER
$previousFixture = $env:PUDDING_PLUGIN_FIXTURE
try {
    $env:PUDDING_PLUGIN_TEST_ROOT = $run
    Invoke-Checked 'build-runtime-tests' { dotnet build Source/Pudding.ToolPlugins.RuntimeTests/Pudding.ToolPlugins.RuntimeTests.csproj --artifacts-path $artifacts --nologo }
    Invoke-Checked 'runtime-tests' { & "$artifacts/bin/Pudding.ToolPlugins.RuntimeTests/debug/Pudding.ToolPlugins.RuntimeTests.exe" --results-directory "$run/runtime-results" }
    Invoke-Checked 'build-worker' { dotnet build Source/Pudding.ToolPlugins.Worker/Pudding.ToolPlugins.Worker.csproj --artifacts-path $artifacts --nologo }
    Invoke-Checked 'build-fixture' { dotnet build Tests/PuddingToolPlugins.Fixture/PuddingToolPlugins.Fixture.csproj --artifacts-path $artifacts --nologo }
    $env:PUDDING_PLUGIN_WORKER = "$artifacts/bin/Pudding.ToolPlugins.Worker/debug/Pudding.ToolPlugins.Worker.exe"
    $env:PUDDING_PLUGIN_FIXTURE = "$artifacts/bin/PuddingToolPlugins.Fixture/debug/Sample.PluginFixture.dll"
    Invoke-Checked 'build-worker-tests' { dotnet build Tests/PuddingToolPlugins.IntegrationTests/PuddingToolPlugins.IntegrationTests.csproj --artifacts-path $artifacts --nologo }
    Invoke-Checked 'worker-tests' { & "$artifacts/bin/PuddingToolPlugins.IntegrationTests/debug/PuddingToolPlugins.IntegrationTests.exe" --results-directory "$run/worker-results" }
    Invoke-Checked 'build-host-tests' { dotnet build Tests/PuddingToolPlugins.HostTests/PuddingToolPlugins.HostTests.csproj --artifacts-path $artifacts --nologo }
    Invoke-Checked 'host-tests' { & "$artifacts/bin/PuddingToolPlugins.HostTests/debug/PuddingToolPlugins.HostTests.exe" --results-directory "$run/host-results" }
    $sdk = Split-Path $env:PUDDING_PLUGIN_WORKER -Parent
    Invoke-Checked 'build-sdk-example' { dotnet build Samples/ToolPlugins/TextTools/TextTools.csproj --artifacts-path "$run/sample-artifacts" "-p:PuddingPluginSdkDirectory=$sdk" --nologo }
    Write-Host "Plugin acceptance passed. Evidence: $run"
}
finally {
    $env:PUDDING_PLUGIN_TEST_ROOT = $previousRoot
    $env:PUDDING_PLUGIN_WORKER = $previousWorker
    $env:PUDDING_PLUGIN_FIXTURE = $previousFixture
    Pop-Location
}
