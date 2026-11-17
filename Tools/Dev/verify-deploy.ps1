# Verify that the running PuddingAgent is actually executing the freshly deployed
# build, rather than a stale one left behind by a rollback.
#
# Why this exists: a Guardian restart can fail and roll back, after which a NEW
# process runs the OLD binaries. Process start time alone therefore proves nothing,
# and a wrong "deployment succeeded" report is easy to produce. This script compares
# the commit time against the DEPLOYED BINARY timestamps resolved from the live
# process path, and probes the deployed assembly for a symbol that must be present.
#
# Exit codes: 0 = all checks passed, 1 = at least one check failed,
#             3 = instrument error (a check could not be evaluated; do NOT read this
#                 as "absent" -- the probe itself is broken).
#
# ASCII-only on purpose: Windows PowerShell decodes BOM-less non-ASCII files as ANSI.
#
# Examples:
#   pwsh -File Tools/Dev/verify-deploy.ps1
#   pwsh -File Tools/Dev/verify-deploy.ps1 -ExpectSymbol FormatGoalRun -ProcessName PuddingAgent
#   pwsh -File Tools/Dev/verify-deploy.ps1 -SkipGuardian

[CmdletBinding()]
param(
    [string]$RepoRoot = 'D:\CodeProject\PuddingAgent\PuddingAgent',
    [string]$ProcessName = 'PuddingAgent',
    [string]$ExpectSymbol = '',
    [string]$GuardianLog = (Join-Path $env:LOCALAPPDATA 'Pudding\guardian\guardian.log'),
    [int]$StartupLogLookbackMinutes = 240,
    [switch]$SkipGuardian
)

$script:failures = 0
$script:instrumentErrors = 0

function Report {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    $tag = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Output ("[{0}] {1,-28} {2}" -f $tag, $Name, $Detail)
    if (-not $Ok) { $script:failures++ }
}

function ReportInstrument {
    param([string]$Name, [string]$Detail)
    Write-Output ("[INSTRUMENT-ERROR] {0,-20} {1}" -f $Name, $Detail)
    $script:instrumentErrors++
}

function Read-SharedText {
    param([string]$Path)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
        try { return $reader.ReadToEnd() } finally { $reader.Close() }
    }
    finally { $stream.Close() }
}

Write-Output '=== commit ==='
$headSha = (& git -C $RepoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
$headShort = (& git -C $RepoRoot rev-parse --short HEAD 2>&1 | Out-String).Trim()
$headIso = (& git -C $RepoRoot show -s --format=%cI HEAD 2>&1 | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($headSha) -or $headSha -notmatch '^[0-9a-f]{40}$') {
    ReportInstrument 'git-head' "cannot read HEAD in $RepoRoot : $headSha"
    Write-Output ''
    Write-Output 'RESULT: instrument error'
    exit 3
}
$headTime = [datetimeoffset]::Parse($headIso).LocalDateTime
Write-Output ("HEAD={0}  commit={1}" -f $headShort, $headTime.ToString('yyyy-MM-dd HH:mm:ss'))

Write-Output ''
Write-Output '=== process ==='
$proc = Get-CimInstance Win32_Process -Filter "Name='$ProcessName.exe'" -ErrorAction SilentlyContinue |
        Select-Object -First 1
if (-not $proc) {
    Report 'process-alive' $false "$ProcessName.exe is not running"
    Write-Output ''
    Write-Output 'RESULT: 1 check(s) failed'
    exit 1
}
# CreationDate is a DateTime under CIM on PS7 and a DMTF string on PS5.1.
$procStartRaw = $proc.CreationDate
$procStart = if ($procStartRaw -is [datetime]) { $procStartRaw }
             else { [Management.ManagementDateTimeConverter]::ToDateTime($procStartRaw) }
Report 'process-alive' $true ("PID=$($proc.ProcessId) start=" + $procStart.ToString('yyyy-MM-dd HH:mm:ss'))
# Necessary condition: a process that started BEFORE the commit cannot be running
# that commit's code. It is not sufficient -- the binary check below is.

$liveDir = Split-Path $proc.ExecutablePath -Parent
if ([string]::IsNullOrWhiteSpace($liveDir) -or -not (Test-Path $liveDir)) {
    ReportInstrument 'live-dir' "cannot resolve deploy dir from $($proc.ExecutablePath)"
    Write-Output ''
    Write-Output 'RESULT: instrument error'
    exit 3
}
Write-Output "LIVE_DIR=$liveDir"

Write-Output ''
Write-Output '=== deployed binaries ==='
$newest = Get-ChildItem $liveDir -Filter 'Pudding*.dll' -File -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $newest) {
    ReportInstrument 'binary-scan' "no Pudding*.dll under $liveDir"
    Write-Output ''
    Write-Output 'RESULT: instrument error'
    exit 3
}
Report 'binaries-newer-than-source-commit' ($newest.LastWriteTime -gt $srcTime) `
       ("newest=" + $newest.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') + " " + $newest.Name)

# Compare against the newest commit that touched the projects that actually end up in the
# live artifact -- not HEAD, and not all of Source. Two false reds taught this:
#   1) a docs-only commit does not require a redeploy;
#   2) neither does a frontend (.ts/.tsx) change, nor a leaf component that is not in the
#      solution yet (PuddingScheduling today). Both made a healthy deployment look stale,
#      and a check that fires on healthy states gets ignored when it finally matters.
$corePaths = @(
    'Source/PuddingHost',
    'Source/PuddingRuntime',
    'Source/PuddingPlatform',
    'Source/PuddingCore'
)
$srcIso = (& git -C $RepoRoot log -1 --format=%cI HEAD -- $corePaths 2>&1 | Out-String).Trim()
$srcTime = if ($srcIso -match '^\d{4}-') { [datetimeoffset]::Parse($srcIso).LocalDateTime } else { $headTime }
Write-Output ("LAST_SOURCE_COMMIT=" + $srcTime.ToString('yyyy-MM-dd HH:mm:ss') + "  (HEAD=" + $headTime.ToString('yyyy-MM-dd HH:mm:ss') + ")")
$runtime = Join-Path $liveDir 'PuddingRuntime.dll'
if (Test-Path $runtime) {
    $rtTime = (Get-Item $runtime).LastWriteTime
    Report 'runtime-dll-newer-than-source-commit' ($rtTime -gt $srcTime) `
           ("PuddingRuntime.dll=" + $rtTime.ToString('yyyy-MM-dd HH:mm:ss'))
}

Write-Output ''
Write-Output '=== artifact string probe ==='
$platform = Join-Path $liveDir 'PuddingPlatform.dll'
$probeTarget = if (Test-Path $runtime) { $runtime } else { $platform }
if (-not (Test-Path $probeTarget)) {
    ReportInstrument 'artifact-probe' "neither PuddingRuntime.dll nor PuddingPlatform.dll in $liveDir"
}
else {
    $bytes = [IO.File]::ReadAllBytes($probeTarget)
    $utf8 = [Text.Encoding]::UTF8.GetString($bytes)
    # POSITIVE CONTROL: this literal is present in every build of the assembly. If it
    # reads as 0 the probe itself is broken, and a 0 for the target symbol would be a
    # false "missing". Never report absence without this control.
    $control = ([regex]::Matches($utf8, 'PuddingRuntime')).Count
    if ($control -eq 0) {
        ReportInstrument 'artifact-probe' ("positive control 'PuddingRuntime' found 0 times in " + (Split-Path $probeTarget -Leaf))
    }
    else {
        Write-Output ("    control: 'PuddingRuntime' x$control in " + (Split-Path $probeTarget -Leaf))
        if (-not [string]::IsNullOrWhiteSpace($ExpectSymbol)) {
            $hits = ([regex]::Matches($utf8, [regex]::Escape($ExpectSymbol))).Count
            Report 'symbol-present' ($hits -gt 0) "'$ExpectSymbol' x$hits"
        }
        else {
            Write-Output '    (no -ExpectSymbol given; skipped)'
        }
    }
}

Write-Output ''
Write-Output '=== startup log ==='
$logRoot = 'D:\Data\logs'
$systemDir = Join-Path $logRoot 'system'
$startupOk = $false
$startupDetail = 'no system log found'
# Log rotation creates a fresh file that does NOT contain this process's startup
# lines, so "newest log file" is the wrong target. Find the file whose
# services-registered line is closest to the process start time instead.
$chosenFile = $null
$chosenText = $null
$bestDelta = [double]::MaxValue
if (Test-Path $systemDir) {
    $candidates = Get-ChildItem $systemDir -Filter '*.log' -File |
                  Where-Object { $_.LastWriteTime -gt $procStart.AddMinutes(-10) }
    foreach ($cand in $candidates) {
        try { $text = Read-SharedText $cand.FullName } catch { continue }
        foreach ($m in [regex]::Matches($text, '(?m)^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})[^\r\n]*services-registered')) {
            $stamp = [datetime]::ParseExact($m.Groups[1].Value, 'yyyy-MM-dd HH:mm:ss', $null)
            $delta = [math]::Abs(($stamp - $procStart).TotalMinutes)
            if ($delta -lt $bestDelta) { $bestDelta = $delta; $chosenFile = $cand; $chosenText = $text }
        }
    }
}
if (-not $chosenFile) {
    ReportInstrument 'startup-log' 'no services-registered line within 10 min of the process start in any log'
}
elseif ($bestDelta -gt 15) {
    ReportInstrument 'startup-log' ("nearest services-registered is " + [math]::Round($bestDelta,1) + " min from process start (" + $chosenFile.Name + ")")
}
else {
    try {
        $kv = $chosenText.Contains('Startup decryption check passed')
        $bad = $chosenText.Contains('Cannot consume scoped service')
        $startupOk = $kv -and (-not $bad)
        $startupDetail = "keyvault-check=$kv di-scope-fault=$bad delta=$([math]::Round($bestDelta,1))min ($($chosenFile.Name))"
        Report 'startup-log-clean' $startupOk $startupDetail
    }
    catch {
        ReportInstrument 'startup-log' "cannot evaluate $($chosenFile.Name): $($_.Exception.Message)"
    }
}

if (-not $SkipGuardian) {
    Write-Output ''
    Write-Output '=== guardian ==='
    if (Test-Path $GuardianLog) {
        $g = Get-Content $GuardianLog -Tail 40 -ErrorAction SilentlyContinue
        $lastPhase = ($g | Where-Object { $_ -match 'phase=(\S+)' } | Select-Object -Last 1)
        if ($lastPhase -match 'phase=(\S+)') {
            $phase = $matches[1]
            Report 'guardian-phase' ($phase -eq 'healthy') "last phase=$phase"
        }
        else {
            ReportInstrument 'guardian-phase' 'no phase= line in the log tail'
        }
    }
    else {
        ReportInstrument 'guardian-phase' "guardian log not found: $GuardianLog"
    }
}

Write-Output ''
if ($script:instrumentErrors -gt 0) {
    Write-Output ("RESULT: instrument error ({0} check(s) could not be evaluated)" -f $script:instrumentErrors)
    exit 3
}
if ($script:failures -gt 0) {
    Write-Output ("RESULT: {0} check(s) failed" -f $script:failures)
    exit 1
}
Write-Output 'RESULT: all checks passed'
exit 0
