param(
    [string]$EvidenceRoot,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$')]
    [string]$RunId = [guid]::NewGuid().ToString('N')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $EvidenceRoot) { $EvidenceRoot = Join-Path $repo '.work/product-tools' }
$run = [IO.Path]::GetFullPath((Join-Path $EvidenceRoot $RunId))
if (Test-Path -LiteralPath $run) { throw "Evidence directory already exists: $run" }
New-Item -ItemType Directory -Path $run | Out-Null
$product = Join-Path $run 'product'
$sample = Join-Path $repo 'Samples/ToolPlugins/TextTools'
$sampleOutput = Join-Path $run 'sample'
dotnet publish (Join-Path $repo 'Source/PuddingAgent/PuddingAgent.csproj') -c Release --artifacts-path "$run/build" -o $product -p:SkipAdminDistCheck=true *> "$run/publish.log"
if ($LASTEXITCODE) { throw "Core publish failed. See $run/publish.log" }
dotnet build (Join-Path $sample 'TextTools.csproj') --artifacts-path "$run/sdk-build" -o $sampleOutput "-p:PuddingPluginSdkDirectory=$product/tools/plugin-worker" *> "$run/sample.log"
if ($LASTEXITCODE) { throw "SDK consumer build failed. See $run/sample.log" }
py -3 -X utf8 (Join-Path $PSScriptRoot 'test_product_tool_plugins.py') "$product/PuddingAgent.exe" $sample "$sampleOutput/Sample.TextTools.dll" "$run/smoke" *> "$run/smoke.log"
if ($LASTEXITCODE) { throw "Product smoke failed. See $run/smoke.log" }
Get-Content -LiteralPath "$run/smoke/plugins.json"
