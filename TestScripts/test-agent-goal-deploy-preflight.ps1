<#
.SYNOPSIS
  PA-08B 部署前离线预检：记录源码/工作树身份、隔离构建产物指纹与目标 DataRoot 路径。
  不部署、不重启、不读取 D:\\data 内容、不调用真实模型；供获得授权后的 08B 实际部署引用。

.DESCRIPTION
  输出 .work/pa08/preflight-<RunId>/{preflight.md,preflight.json,manifest.sha256,host-build.log}。
  - git revision/branch/工作树状态摘要与 dirty 文件数
  - dotnet --info 版本
  - 隔离 artifacts-path 构建 PuddingHost 后，对关键程序集计算 SHA-256
  - 目标 DataRoot 仅记录路径字符串（不访问内容）

.PARAMETER RunId
  证据目录标识，默认按时间生成。

.PARAMETER SkipBuild
  跳过 Host 构建（复用现有 .work 产物时使用）；此时不生成程序集指纹。

.EXAMPLE
  pwsh -NoProfile -File TestScripts/test-agent-goal-deploy-preflight.ps1
#>
param(
    [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runRoot = Join-Path $repoRoot ".work/pa08/preflight-$RunId"
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

function Hash-File([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

Push-Location -LiteralPath $repoRoot
try {
    $revision = (git rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $revision = 'unavailable' }
    $branch = (git rev-parse --abbrev-ref HEAD 2>$null)
    $status = @(git status --porcelain 2>$null)
    $statusText = ($status -join "`n")
    $statusHash = if ($statusText.Length -eq 0) { 'clean' } else {
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($statusText))).ToLowerInvariant()
    }
    $dotnetVersion = (dotnet --version 2>$null)
    $dataRoot = [Environment]::GetEnvironmentVariable('PUDDING_DATA_ROOT')
    if ([string]::IsNullOrWhiteSpace($dataRoot)) { $dataRoot = 'D:\data' }

    $assemblies = [ordered]@{}
    $buildStatus = 'skipped'
    if (-not $SkipBuild) {
        $artifacts = Join-Path $runRoot 'host-artifacts'
        dotnet build (Join-Path $repoRoot 'Source/PuddingHost/PuddingHost.csproj') --artifacts-path $artifacts `
            -p:UseVSTest=false -p:CollectCoverage=false --nologo *> (Join-Path $runRoot 'host-build.log')
        if ($LASTEXITCODE -ne 0) {
            throw "HOST BUILD FAILED (exit $LASTEXITCODE); see $(Join-Path $runRoot 'host-build.log')"
        }
        $buildStatus = 'succeeded'
        $binRoot = Join-Path $artifacts 'bin/PuddingHost/debug'
        foreach ($name in @('PuddingHost.dll','PuddingPlatform.dll','PuddingRuntime.dll','PuddingEventing.dll',
                'PuddingReviews.dll','PuddingModels.dll','PuddingExecutionResources.dll')) {
            $assemblies[$name] = Hash-File (Join-Path $binRoot $name)
        }
    }

    $payload = [ordered]@{
        runId = $RunId
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        source = [ordered]@{
            repositoryRoot = $repoRoot
            revision = $revision
            branch = $branch
            dirtyFileCount = $status.Count
            statusHash = $statusHash
        }
        toolchain = [ordered]@{ dotnet = $dotnetVersion; os = [Environment]::OSVersion.VersionString }
        build = [ordered]@{ status = $buildStatus; assemblies = $assemblies }
        target = [ordered]@{ dataRoot = $dataRoot; deployed = $false }
        boundaries = @(
            'no deploy', 'no restart', 'no real model call', 'no credential read',
            'DataRoot path recorded as a string only; contents were not read'
        )
    }
    $json = $payload | ConvertTo-Json -Depth 6
    Set-Content -LiteralPath (Join-Path $runRoot 'preflight.json') -Value $json -Encoding utf8

    $manifest = New-Object System.Text.StringBuilder
    foreach ($pair in $assemblies.GetEnumerator()) {
        if ($pair.Value) { [void]$manifest.AppendLine("$($pair.Value.ToLowerInvariant())  $($pair.Key)") }
    }
    Set-Content -LiteralPath (Join-Path $runRoot 'manifest.sha256') -Value $manifest.ToString() -Encoding utf8

    $md = New-Object System.Text.StringBuilder
    [void]$md.AppendLine("# PA-08B 部署前离线预检（$RunId）")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("- 生成时间（UTC）：$($payload.generatedAtUtc)")
    [void]$md.AppendLine("- 源码 revision：$revision（branch=$branch）")
    [void]$md.AppendLine("- 工作树：$($status.Count) 个未提交条目，statusHash=$statusHash")
    [void]$md.AppendLine("- 工具链：dotnet $dotnetVersion · $([Environment]::OSVersion.VersionString)")
    [void]$md.AppendLine("- Host 构建：$buildStatus（隔离 artifacts-path）")
    [void]$md.AppendLine("- 目标 DataRoot：``$dataRoot``（仅记录路径，未读取内容）")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("## 程序集指纹")
    [void]$md.AppendLine("")
    if ($assemblies.Count -eq 0) {
        [void]$md.AppendLine("（SkipBuild：本次未构建，未生成程序集指纹）")
    } else {
        foreach ($pair in $assemblies.GetEnumerator()) {
            [void]$md.AppendLine("- ``$($pair.Key)``: ``$($pair.Value ?? 'missing')``")
        }
    }
    [void]$md.AppendLine("")
    [void]$md.AppendLine("## 边界")
    foreach ($line in $payload.boundaries) { [void]$md.AppendLine("- $line") }
    [void]$md.AppendLine("")
    [void]$md.AppendLine("> 本预检不是部署证明，也不是 08B 通过；实际部署/加载仍需授权与隔离环境。")
    Set-Content -LiteralPath (Join-Path $runRoot 'preflight.md') -Value $md.ToString() -Encoding utf8

    Write-Output "PASS: PA-08B preflight ($RunId). Evidence: $runRoot"
    Write-Output "revision=$revision dirty=$($status.Count) build=$buildStatus assemblies=$($assemblies.Count)"
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
finally {
    Pop-Location
}
