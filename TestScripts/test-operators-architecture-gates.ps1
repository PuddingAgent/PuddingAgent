#requires -Version 7.0
<#
.SYNOPSIS
  算子抽象层架构门禁（S1a §5.2）：供应商隔离 + 反向依赖。任一违规 ⇒ exit 非零。

.DESCRIPTION
  为什么需要它：契约层的价值全在「不依赖具体实现 / 不依赖具体供应商」这条边界上。
  边界一旦被一行 `using` 或一个注释里的供应商名词悄悄破掉，后面所有「可替换」的承诺都失效，
  而这类破口在编译期完全合法、在测试里也不会红——只有架构门禁能拦住。

  两项检查（作用于 Source/PuddingCore/Operators/** 与 Source/PuddingRuntime/Operators/**）：
  1. **供应商隔离**：匹配 `(?i)jev|openai|anthropic|typesafe` 的命中数必须为 0；
  2. **反向依赖**：匹配 `(?i)\bRsi|GoalService|ToolApproval\b` 的领域标识命中数必须为 0。

  脚本逐条打印命中文件名与行号，便于定位；退出码：全绿 ⇒ 0，任一违规 ⇒ 1。

.EXAMPLE
  pwsh -File TestScripts\test-operators-architecture-gates.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$targets = @(
    (Join-Path $RepoRoot 'Source/PuddingCore/Operators'),
    (Join-Path $RepoRoot 'Source/PuddingRuntime/Operators')
)

$checks = @(
    [pscustomobject]@{ Name = 'vendor-isolation';   Pattern = '(?i)jev|openai|anthropic|typesafe' }
    [pscustomobject]@{ Name = 'reverse-dependency'; Pattern = '(?i)\bRsi|GoalService|ToolApproval\b' }
)

$violations = 0

foreach ($dir in $targets) {
    if (-not (Test-Path -LiteralPath $dir)) {
        Write-Host "SKIP (missing dir): $dir"
        continue
    }

    $files = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter '*.cs')
    Write-Host "SCAN $dir  files=$($files.Count)"

    foreach ($check in $checks) {
        $hits = @()
        if ($files.Count -gt 0) {
            $hits = @($files | Select-String -Pattern $check.Pattern)
        }

        Write-Host ("  [{0}] hits={1}" -f $check.Name, $hits.Count)
        foreach ($hit in $hits) {
            $relative = $hit.Path.Substring($RepoRoot.Length).TrimStart('\', '/')
            Write-Host ("    {0}:{1}: {2}" -f $relative, $hit.LineNumber, $hit.Line.Trim())
        }

        if ($hits.Count -gt 0) {
            $violations += $hits.Count
        }
    }
}

if ($violations -gt 0) {
    Write-Host "GATES: FAIL (violations=$violations)"
    exit 1
}

Write-Host 'GATES: PASS'
exit 0
