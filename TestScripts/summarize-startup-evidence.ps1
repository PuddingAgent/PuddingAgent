#Requires -Version 7
<#
.SYNOPSIS
  Summarises Desktop startup evidence (D1) written to startup-evidence.jsonl.

.DESCRIPTION
  Reads one JSON object per startup attempt and prints, per attempt, the milestones and the slowest
  phases, then aggregates every phase name across attempts (count / min / mean / max).

  Small samples are reported as max and spread on purpose: the design forbids dressing five cold runs
  up as a reliable p95.

.PARAMETER Path
  Path to startup-evidence.jsonl. Defaults to the Desktop StateRoot location.

.PARAMETER Top
  How many slowest phases to list per attempt (default 12).

.EXAMPLE
  pwsh TestScripts/summarize-startup-evidence.ps1 -Path "$env:LOCALAPPDATA\Pudding\WinUiSkeleton\startup\startup-evidence.jsonl"
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path $env:LOCALAPPDATA 'Pudding\WinUiSkeleton\startup\startup-evidence.jsonl'),
    [int]$Top = 12
)

if (-not (Test-Path -LiteralPath $Path)) { throw "找不到启动证据文件：$Path" }

$attempts = Get-Content -LiteralPath $Path |
    Where-Object { $_.Trim() } |
    ForEach-Object { $_ | ConvertFrom-Json }

if (-not $attempts) { throw "启动证据文件为空：$Path" }

$milestoneOrder = @('ShellVisible', 'DirectoryReadable', 'ConversationReadable', 'ExecutionReady')

foreach ($attempt in $attempts) {
    $phases = @($attempt.phases)
    $slowest = ($phases | Sort-Object -Property durationMs -Descending | Select-Object -First $Top)
    Write-Host ''
    Write-Host ("=== attempt {0}  pid={1}  build={2}" -f $attempt.attemptId.Substring(0, 8), $attempt.processId, $attempt.buildVersion) -ForegroundColor Cyan
    Write-Host ("    outcome={0}{1}  total={2:N0} ms  attempt started {3:N0} ms after process start" -f `
        $attempt.outcome, $(if ($attempt.failureType) { " ($($attempt.failureType))" } else { '' }), `
        $attempt.totalMs, $attempt.sinceProcessStartMs)
    Write-Host  "    dataRoot=$($attempt.dataRoot)"
    Write-Host  "    milestones:"
    foreach ($name in $milestoneOrder) {
        $milestone = @($attempt.milestones) | Where-Object { $_.milestone -eq $name } | Select-Object -First 1
        if ($milestone) {
            $processMs = $attempt.sinceProcessStartMs + $milestone.atMs
            Write-Host ("      {0,-22} +{1,9:N0} ms since process start{2}" -f $name, $processMs, $(if ($milestone.detail) { "  ($($milestone.detail))" } else { '' }))
        } else {
            Write-Host ("      {0,-22} 未达成" -f $name) -ForegroundColor DarkGray
        }
    }
    foreach ($metric in @($attempt.metrics)) { Write-Host ("    metric {0} = {1}" -f $metric.name, $metric.value) }
    if (@($attempt.violations).Count -gt 0) {
        Write-Host "    violations:" -ForegroundColor Yellow
        $attempt.violations | ForEach-Object { Write-Host "      - $_" -ForegroundColor Yellow }
    }
    Write-Host ("    slowest {0} phases:" -f @($slowest).Count)
    $slowest | ForEach-Object {
        Write-Host ("      {0,9:N1} ms  at +{1,8:N0}  {2,-52} {3}" -f $_.durationMs, $_.atMs, $_.name, $_.outcome)
    }
}

Write-Host ''
Write-Host '=== per-phase spread across attempts (count / min / mean / max, ms) ===' -ForegroundColor Cyan
$attempts.phases | Group-Object -Property name | ForEach-Object {
    $values = @($_.Group | ForEach-Object { [double]$_.durationMs })
    [pscustomobject]@{
        Phase = $_.Name
        Count = $values.Count
        Min   = [math]::Round(($values | Measure-Object -Minimum).Minimum, 1)
        Mean  = [math]::Round(($values | Measure-Object -Average).Average, 1)
        Max   = [math]::Round(($values | Measure-Object -Maximum).Maximum, 1)
    }
} | Sort-Object -Property Max -Descending | Format-Table -AutoSize
