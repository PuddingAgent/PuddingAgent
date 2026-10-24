# Post-restart verification for the token-accounting work.
# Prints PASS/FAIL per check and exits 1 if any check fails.
# ASCII-only on purpose: this file is executed by Windows PowerShell, whose
# console code page mangles non-ASCII output.

param(
    [string]$RepoRoot = 'D:\CodeProject\PuddingAgent\PuddingAgent',
    [string]$LogRoot  = 'D:\Data\logs',
    [int]$LogLookbackHours = 6
)

$ErrorActionPreference = 'Stop'
$failures = 0

function Report([string]$name, [bool]$ok, [string]$detail) {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Output ("[{0}] {1} - {2}" -f $tag, $name, $detail)
    if (-not $ok) { $script:failures++ }
}

# 1) The stashed context_health projection must be applied, not stashed.
Push-Location $RepoRoot
try {
    $stash = (git stash list) -join "`n"
    Report 'stash-empty' ([string]::IsNullOrWhiteSpace($stash)) ($(if ($stash) { 'stash still holds unapplied work' } else { 'no stashed work' }))

    # 2) Source must actually carry the ModelId projection.
    $source = Get-ChildItem -Path (Join-Path $RepoRoot 'Source') -Recurse -Filter 'ContextCompactionService.cs' |
        Select-Object -First 1
    $hasModelId = $false
    if ($source) {
        $hasModelId = (Get-Content -LiteralPath $source.FullName -Raw) -match 'ModelId\s*=\s*measuredMain'
    }
    Report 'modelid-projection' $hasModelId ($(if (-not $source) { 'ContextCompactionService.cs not found' } elseif ($hasModelId) { 'projection present' } else { 'projection missing' }))
}
finally {
    Pop-Location
}

# 3) The running host must be newer than the deployed assemblies it loads.
$proc = Get-Process -Name 'PuddingAgent' -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -Last 1
if (-not $proc) {
    Report 'host-running' $false 'no PuddingAgent process'
}
else {
    $exeDir = Split-Path -Parent $proc.Path
    $newest = Get-ChildItem -Path $exeDir -Filter '*.dll' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $restarted = $false
    $detail = 'no dll found'
    if ($newest) {
        # 单看 proc > dll 会假绿：只要进程比上次部置新就为真，哪怕那次部置早于本次源码修改。
        # 改用文件时间做基线（不依赖 git 输出解析）：产物必须晚于最新的 .cs 源文件。
        $newestSource = Get-ChildItem -Path (Join-Path $RepoRoot 'Source') -Recurse -Filter '*.cs' -File |
            Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $builtAfterCommit = $newestSource -and ($newest.LastWriteTime -gt $newestSource.LastWriteTime)
        $restarted = $builtAfterCommit -and ($proc.StartTime -gt $newest.LastWriteTime)
        $sourceStamp = if ($newestSource) { $newestSource.LastWriteTime } else { $proc.StartTime }
        $detail = "procStart={0:HH:mm:ss} newestDll={1:HH:mm:ss} newestSource={2:HH:mm:ss} builtAfterSource={3}" -f $proc.StartTime, $newest.LastWriteTime, $sourceStamp, $builtAfterCommit
    }
    Report 'restarted-after-build' $restarted $detail
}

# 4) The capture flag must have produced at least one fingerprint line.
$cutoff = (Get-Date).AddHours(-$LogLookbackHours)
$hit = $null
$logDir = Join-Path $LogRoot 'components\llm_gateway'
if (Test-Path $logDir) {
    foreach ($file in Get-ChildItem -Path $logDir -Filter '*.log' -File | Where-Object { $_.LastWriteTime -gt $cutoff }) {
        $content = $null
        try {
            $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
            $content = $reader.ReadToEnd()
            $reader.Close()
            $stream.Close()
        }
        catch { continue }
        if ($content -and $content.Contains('[LlmRequestCapture]')) { $hit = $file.Name; break }
    }
}
Report 'capture-log-line' ([bool]$hit) ($(if ($hit) { "found in $hit" } else { "no [LlmRequestCapture] line in the last $LogLookbackHours h" }))

Write-Output ''
if ($failures -gt 0) {
    Write-Output ("RESULT: {0} check(s) failed" -f $failures)
    exit 1
}
Write-Output 'RESULT: all checks passed'
exit 0
