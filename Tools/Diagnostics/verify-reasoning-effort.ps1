# Post-restart verification for the reasoning-effort wiring.
# ASCII-only on purpose: Windows PowerShell mangles non-ASCII console output.
# Exits 1 if any check fails. A missing instrument counts as FAIL, never as PASS.

param(
    [string]$RepoRoot = 'D:\CodeProject\PuddingAgent\PuddingAgent',
    [string]$LogRoot  = 'D:\Data\logs\system',
    [int]$LookbackHours = 3
)

$ErrorActionPreference = 'Stop'
$failures = 0

function Report([string]$name, [bool]$ok, [string]$detail) {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Output ("[{0}] {1} - {2}" -f $tag, $name, $detail)
    if (-not $ok) { $script:failures++ }
}

# 1) Deployed assemblies must be at least as new as the last commit.
Push-Location $RepoRoot
try {
    $headTime = [DateTimeOffset]::Parse((git log -1 --format=%cI).Trim()).LocalDateTime
} finally { Pop-Location }

$coreDll = Join-Path $RepoRoot 'temp\build\winui3\bin\PuddingDesktop\Debug\net10.0-windows10.0.19041.0\win-x64\core\PuddingAgent.dll'
if (-not (Test-Path $coreDll)) {
    Report 'deployed-freshness' $false "core dll not found: $coreDll"
} else {
    $dllTime = (Get-Item $coreDll).LastWriteTime
    Report 'deployed-freshness' ($dllTime -ge $headTime) ("dll={0:HH:mm:ss} head={1:HH:mm:ss}" -f $dllTime, $headTime)
}

# 2) Collect recent log lines.
$since = (Get-Date).AddHours(-$LookbackHours)
$lines = @()
if (Test-Path $LogRoot) {
    foreach ($file in (Get-ChildItem -Path $LogRoot -Filter *.log | Where-Object { $_.LastWriteTime -ge $since })) {
        try {
            $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite')
            $reader = New-Object IO.StreamReader($stream)
            $lines += ($reader.ReadToEnd() -split "`n")
            $reader.Close(); $stream.Close()
        } catch { }
    }
}

$captures = $lines | Where-Object { $_ -match 'LlmRequestCapture' }
Report 'capture-instrument' ($captures.Count -gt 0) ("captureLines=$($captures.Count) (PUDDING_CAPTURE_REQUEST must be on)")

# 3) The body must actually carry reasoning.effort.
#    This is the discriminating check: before the fix no request carried the key at
#    all, so this line count moves 0 -> N and can take red.
$withEffort = $captures | Where-Object { $_ -match 'reasoningEffort=(high|max)' }
Report 'body-carries-effort' ($withEffort.Count -gt 0) ("linesWithEffort=$($withEffort.Count)")

# 4) INFO ONLY - reasoning tokens are NOT an acceptance criterion here.
#    Measured 2026-10-25 against the live DB (TokenUsageEvents): across 128,180 rows
#    and 13 models, ReasoningTokens > 0 has never been recorded (max = 0 for every
#    model). The pre-fix baseline is therefore also 0, so this indicator cannot tell
#    a fixed build from a broken one - it can never take red. Treating it as a gate
#    produced a guaranteed-FAIL that said nothing about the change.
#    The discriminating evidence is check 3 (outbound body carries reasoningEffort)
#    plus the offline provider probe (Tools/Diagnostics/probe-effort-live.ps1:
#    none -> 0 reasoning tokens, high -> 40).
#    Kept as INFO because it becomes meaningful if a model ever reports non-zero.
$usageLines = @($lines | Where-Object { $_ -match 'reasoningTokens=(\d+)' })
$positive   = @($usageLines | Where-Object { $_ -match 'reasoningTokens=([1-9]\d*)' })
Write-Output ("[INFO] reasoning-tokens - usageLines={0} positive={1} (not an acceptance criterion; see comment)" -f $usageLines.Count, $positive.Count)

Write-Output ("failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
exit 0
