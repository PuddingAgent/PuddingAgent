#Requires -Version 7.0
<#
.SYNOPSIS
  DS-A2-01：九个基础设施实现工程的 MSBuild 反向引用边界探针。

.DESCRIPTION
  固定九个实现工程、显式边界目标与预期诊断文本（见任务书 DS-A2-01 表格）。对每个工程：
    1) baseline：dotnet msbuild <csproj> -nologo -t:<目标>，必须 exit 0；
    2) negative：只在本次运行目录生成 MSBuild targets，用 -p:CustomAfterMicrosoftCommonTargets
       注入真实 Source/PuddingRuntime/PuddingRuntime.csproj 的 ProjectReference 后运行同一目标。
       负例必须同时满足：退出非零、输出出现该工程表内精确诊断行、且诊断行来源为该被测工程。
  完整性：九个工程全部通过才返回 0；漏项、意外通过、诊断文本不符、baseline 非零均返回非零。
  证据：.work/ds-a2-01/<RunId>/<Project>/baseline.log、negative.log、injections/*.targets、matrix.json。
  本脚本只读仓库内容；不 restore、不构建、不启动产品、不访问网络、不读写凭据。

.PARAMETER RunId
  证据目录标识，仅允许字母、数字、连字符、下划线；缺省生成时间戳+随机值。
  若 .work/ds-a2-01/<RunId>/ 已存在则拒绝覆盖并以非零退出。

.EXAMPLE
  pwsh -NoProfile -File TestScripts/test-foundation-component-boundaries.ps1 -RunId 20261107-a2-01-run1
#>

[CmdletBinding()]
param(
    [string]$RunId = ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
)

$ErrorActionPreference = 'Stop'

$taskId = 'DS-A2-01'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskRoot = Join-Path $repoRoot '.work/ds-a2-01'
$runtimeProjectRelative = 'Source/PuddingRuntime/PuddingRuntime.csproj'
$runtimeProjectFullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $runtimeProjectRelative))

# 任务书表格：工程、显式执行目标、注入 Runtime 引用后的预期诊断文本。只读仓库，不在脚本内修改这些值。
$boundaries = @(
    [pscustomobject]@{ Project = 'PuddingAgents'; Target = 'EnforceAgentsBoundary'; Diagnostic = 'Agents may only reference its ports and the public API contracts.' }
    [pscustomobject]@{ Project = 'PuddingContext'; Target = 'EnforceContextBoundary'; Diagnostic = 'Context may only reference its ports.' }
    [pscustomobject]@{ Project = 'PuddingTools'; Target = 'EnforceToolFacadeBoundary'; Diagnostic = 'Tool facade may only reference its ports.' }
    [pscustomobject]@{ Project = 'PuddingMcp'; Target = 'EnforceMcpBoundary'; Diagnostic = 'MCP may only reference its ports.' }
    [pscustomobject]@{ Project = 'PuddingWorkspace'; Target = 'EnforceWorkspaceBoundary'; Diagnostic = 'Workspace may only reference its ports.' }
    [pscustomobject]@{ Project = 'PuddingEventing'; Target = 'EnforceEventingBoundary'; Diagnostic = 'Eventing may only reference its ports and the public API contracts.' }
    [pscustomobject]@{ Project = 'PuddingInvocationPipeline'; Target = 'EnforceInvocationPipelineBoundary'; Diagnostic = 'Invocation pipeline may only reference its ports.' }
    [pscustomobject]@{ Project = 'PuddingAuthorization'; Target = 'EnforceAuthorizationBoundary'; Diagnostic = 'Authorization may only reference its ports.' }
    [pscustomobject]@{ Project = 'PuddingModels'; Target = 'EnforceModelsBoundary'; Diagnostic = 'Models may only reference its ports and the public API contracts.' }
)

function Get-RepoRelativePath {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $repoRoot + [IO.Path]::DirectorySeparatorChar
    if ($full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        return ($full.Substring($prefix.Length) -replace '\\', '/')
    }
    return ($full -replace '\\', '/')
}

function ConvertTo-OriginFullPath {
    param([Parameter(Mandatory)][string]$Origin)
    if ([IO.Path]::IsPathRooted($Origin)) { return [IO.Path]::GetFullPath($Origin) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Origin))
}

# RunId 与证据目录：非法标识或目录已存在时拒绝（不覆盖任何已有证据）。
if ($RunId -notmatch '^[A-Za-z0-9_-]+$') {
    [Console]::Error.WriteLine("拒绝：RunId '$RunId' 只能包含字母、数字、连字符、下划线。")
    exit 2
}
$runRoot = Join-Path $taskRoot $RunId
if (Test-Path -LiteralPath $runRoot) {
    [Console]::Error.WriteLine("拒绝覆盖已有证据：.work/ds-a2-01/$RunId 已存在。")
    exit 2
}
try {
    New-Item -ItemType Directory -Path $runRoot -ErrorAction Stop | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $runRoot 'injections') -ErrorAction Stop | Out-Null
} catch {
    [Console]::Error.WriteLine("拒绝覆盖已有证据：.work/ds-a2-01/$RunId 已存在。")
    exit 2
}

$headCommit = ''
try {
    $resolvedHead = ((& git -C $repoRoot rev-parse HEAD 2>$null) | Select-Object -First 1)
    if ($null -ne $resolvedHead) { $headCommit = $resolvedHead.Trim() }
} catch {
    $headCommit = ''
}

$items = [System.Collections.Generic.List[object]]::new()
$failureMessages = [System.Collections.Generic.List[string]]::new()
$fatalMessage = $null

try {
    foreach ($boundary in $boundaries) {
        $projectName = $boundary.Project
        $projectRelative = "Source/$projectName/$projectName.csproj"
        $projectFullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $projectRelative))
        $logDir = Join-Path $runRoot $projectName
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        $baselineLog = Join-Path $logDir 'baseline.log'
        $negativeLog = Join-Path $logDir 'negative.log'
        $injectTargets = Join-Path (Join-Path $runRoot 'injections') "$projectName.runtime-reference.targets"

        $item = [pscustomobject]@{
            project             = $projectName
            projectPath         = $projectRelative
            target              = $boundary.Target
            expectedDiagnostic  = $boundary.Diagnostic
            targetDeclaration   = $null
            baselineExitCode    = $null
            negativeExitCode    = $null
            diagnosticLine      = $null
            baselineLog         = (Get-RepoRelativePath $baselineLog)
            negativeLog         = (Get-RepoRelativePath $negativeLog)
            injectionTargets    = (Get-RepoRelativePath $injectTargets)
            status              = 'fail'
            failureReason       = $null
        }
        $reasons = [System.Collections.Generic.List[string]]::new()

        if (-not (Test-Path -LiteralPath $projectFullPath -PathType Leaf)) {
            $reasons.Add('project-missing')
        }
        else {
            # 前置核对：目标必须真实挂在 BeforeTargets="ResolveProjectReferences"，诊断文本必须仍在工程内。
            $projectText = [IO.File]::ReadAllText($projectFullPath)
            $targetPattern = '<Target\s+Name="' + [regex]::Escape($boundary.Target) + '"[^>]*BeforeTargets="ResolveProjectReferences"'
            if ([regex]::IsMatch($projectText, $targetPattern)) {
                $item.targetDeclaration = 'BeforeTargets=ResolveProjectReferences'
            }
            else {
                $reasons.Add('target-missing-or-not-before-ResolveProjectReferences')
            }
            if ($projectText.IndexOf($boundary.Diagnostic, [StringComparison]::Ordinal) -lt 0) {
                $reasons.Add('expected-diagnostic-text-not-found-in-project')
            }

            # baseline：目标必须正常放行（exit 0）。
            & dotnet msbuild $projectFullPath -nologo -t:$boundary.Target *> $baselineLog
            $item.baselineExitCode = $LASTEXITCODE
            if ($item.baselineExitCode -ne 0) {
                $reasons.Add("baseline-exit-$($item.baselineExitCode)")
            }

            # negative：只在本次运行目录生成注入 targets，给被测工程加入真实 Runtime ProjectReference。
            $escapedRuntime = [System.Security.SecurityElement]::Escape($runtimeProjectFullPath)
            $injectionText = @"
<Project>
  <ItemGroup>
    <ProjectReference Include="$escapedRuntime" />
  </ItemGroup>
</Project>
"@
            [IO.File]::WriteAllText($injectTargets, $injectionText, [Text.UTF8Encoding]::new($false))

            & dotnet msbuild $projectFullPath -nologo -t:$boundary.Target "-p:CustomAfterMicrosoftCommonTargets=$injectTargets" *> $negativeLog
            $item.negativeExitCode = $LASTEXITCODE

            # 命中判定：精确诊断文本 + 该行来源必须是被测工程（排除缺 SDK、项目未找到、别的工程报错等冒充）。
            $matchedLine = $null
            if (Test-Path -LiteralPath $negativeLog -PathType Leaf) {
                foreach ($line in [IO.File]::ReadLines($negativeLog)) {
                    $match = [regex]::Match($line, '^(?<origin>.+?)\(\d+,\d+\)\s*:\s*error\s*:\s*(?<text>.+?)\s*$')
                    if (-not $match.Success) { continue }
                    if ($match.Groups['text'].Value -ne $boundary.Diagnostic) { continue }
                    $originFull = ConvertTo-OriginFullPath $match.Groups['origin'].Value.Trim().Trim('"')
                    if (-not [string]::Equals($originFull, $projectFullPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
                    $matchedLine = $line.Trim()
                    break
                }
            }
            $item.diagnosticLine = $matchedLine

            if ($item.negativeExitCode -eq 0) {
                $reasons.Add('negative-unexpected-pass')
            }
            elseif ($null -eq $matchedLine) {
                $reasons.Add('negative-diagnostic-mismatch')
            }
        }

        if ($reasons.Count -eq 0) {
            $item.status = 'pass'
            Write-Output ("PASS {0} target={1} baseline={2} negative={3}" -f
                $projectName, $boundary.Target, $item.baselineExitCode, $item.negativeExitCode)
        }
        else {
            $item.failureReason = ($reasons -join '; ')
            $failureMessages.Add("${projectName}: $($item.failureReason)")
            Write-Output ("FAIL {0} baseline={1} negative={2} reason={3}" -f
                $projectName, $item.baselineExitCode, $item.negativeExitCode, $item.failureReason)
        }
        $items.Add($item)
    }
}
catch {
    $fatalMessage = $_.Exception.Message
    Write-Output "FATAL: $fatalMessage"
}

$passCount = @($items | Where-Object { $_.status -eq 'pass' }).Count
$complete = ($null -eq $fatalMessage) -and ($items.Count -eq $boundaries.Count) -and ($passCount -eq $boundaries.Count)
$matrix = [pscustomobject]@{
    taskId          = $taskId
    runId           = $RunId
    generatedAt     = (Get-Date -Format o)
    repoRoot        = $repoRoot
    headCommit      = $headCommit
    runtimeProject  = $runtimeProjectRelative
    probeKind       = 'MSBuild-target-rejects-ProjectReference'
    boundaryCount   = $boundaries.Count
    itemCount       = $items.Count
    passCount       = $passCount
    failureCount    = ($items.Count - $passCount)
    complete        = $complete
    fatalError      = $fatalMessage
    failureMessages = @($failureMessages)
    items           = @($items)
}
$matrixPath = Join-Path $runRoot 'matrix.json'
$matrix | ConvertTo-Json -Depth 6 | Out-File -LiteralPath $matrixPath -Encoding utf8
Write-Output "matrix: $(Get-RepoRelativePath $matrixPath)"

if ($complete) {
    Write-Output ("PASS: {0} {1}/{2} boundaries rejected the injected Runtime reference." -f
        $taskId, $passCount, $boundaries.Count)
    exit 0
}

Write-Output ("FAIL: {0} {1}/{2} passed ({3} failed)." -f
    $taskId, $passCount, $boundaries.Count, ($boundaries.Count - $passCount))
if ($null -ne $fatalMessage) { Write-Output "fatal: $fatalMessage" }
exit 1
