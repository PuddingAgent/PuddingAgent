# Measure full-text index incremental latency (G2: "a file change is searchable within 1s").
#
# Why this exists: G2 cannot be judged from logs (the maintenance loop logs almost nothing
# per file) and it must not be judged from a single happy-path sample. This script creates
# probe files, watches the index directory for a commit, and reports a distribution.
#
# Expected BEFORE the maintenance is enabled: "NO COMMITS OBSERVED" for every sample.
# That is the documented state (MaintenanceOptions.Enabled defaults to false); it is not a
# bug in this script. The pre-deploy run is therefore the expected-red control.
#
# Instrument notes (learned the hard way on 2026-10-28):
#   - the index directory name is derived with the frozen rule
#     (GetFullPath -> TrimEnd separators -> ToUpperInvariant -> SHA256 -> lower hex);
#   - the sampling loop must not recursively sum a 1.8 GB directory (that polluted an
#     earlier IO measurement), so only file count / total bytes / newest mtime are compared;
#   - probe files are always deleted in a finally block, and the script prints how many
#     it created so a leftover is obvious.
#
# ASCII-only on purpose: Windows PowerShell 5.1 decodes BOM-less .ps1 as ANSI.
[CmdletBinding()]
param(
    [string]$CorpusRoot = 'D:\CodeProject\PuddingAgent\PuddingAgent',
    [string]$IndexRootParent = 'D:\Data\fulltext-index',
    [string]$ProbeDirectory = 'Docs',
    [int]$Samples = 3,
    [int]$TimeoutSeconds = 20,
    [int]$ThresholdMs = 1000
)

$ErrorActionPreference = 'Stop'

function Get-IndexDirName([string]$corpusRoot) {
    $full = [IO.Path]::GetFullPath($corpusRoot)
    $trimmed = $full.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $upper = $trimmed.ToUpperInvariant()
    $sha = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($upper))
    return [Convert]::ToHexString($sha).ToLowerInvariant()
}

function Get-Stamp([string]$dir) {
    $files = Get-ChildItem $dir -File -ErrorAction SilentlyContinue
    $count = @($files).Count
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $bytes) { $bytes = 0 }
    $mtime = if ($count -gt 0) {
        ($files | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
    } else { [DateTime]::MinValue }
    return [pscustomobject]@{ Count = $count; Bytes = [long]$bytes; Mtime = $mtime }
}

$name = Get-IndexDirName $CorpusRoot
$indexDir = Join-Path $IndexRootParent $name
Write-Output ("corpus   = {0}" -f $CorpusRoot)
Write-Output ("indexDir = {0}" -f $indexDir)
if (-not (Test-Path $indexDir)) { Write-Output 'FATAL: index directory not found (wrong corpus root or index never built)'; exit 2 }

$probeDir = Join-Path $CorpusRoot $ProbeDirectory
if (-not (Test-Path $probeDir)) { Write-Output ("FATAL: probe directory missing: {0}" -f $probeDir); exit 2 }

$created = New-Object System.Collections.Generic.List[string]
$deltas = New-Object System.Collections.Generic.List[int]
$noCommit = 0

try {
    Write-Output ("samples  = {0}  timeout = {1}s  threshold = {2}ms" -f $Samples, $TimeoutSeconds, $ThresholdMs)
    Write-Output 'idx  deltaMs  outcome'

    for ($i = 1; $i -le $Samples; $i++) {
        $file = Join-Path $probeDir ("__g2_probe_{0}_{1}.md" -f $i, [Guid]::NewGuid().ToString('N').Substring(0, 8))
        $body = "# g2 probe`n`nG2-TOKEN-" + [Guid]::NewGuid().ToString('N') + "`n"

        $before = Get-Stamp $indexDir
        $start = [DateTime]::UtcNow
        [IO.File]::WriteAllText($file, $body, (New-Object System.Text.UTF8Encoding($false)))
        $created.Add($file)

        $deadline = $start.AddSeconds($TimeoutSeconds)
        $committed = $false
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 50
            $now = Get-Stamp $indexDir
            if ($now.Count -ne $before.Count -or $now.Bytes -ne $before.Bytes -or $now.Mtime -ne $before.Mtime) {
                $committed = $true
                break
            }
        }

        if ($committed) {
            $ms = [int]([DateTime]::UtcNow - $start).TotalMilliseconds
            $deltas.Add($ms)
            Write-Output ("{0,3}  {1,7}  commit" -f $i, $ms)
        } else {
            $noCommit++
            Write-Output ("{0,3}  {1,7}  NO COMMIT within timeout" -f $i, -1)
        }

        try { Remove-Item -LiteralPath $file -Force -ErrorAction Stop } catch { Write-Output ("WARN: could not delete {0}" -f $file) }
        Start-Sleep -Milliseconds 250
    }
}
finally {
    foreach ($f in $created) {
        if (Test-Path -LiteralPath $f) {
            try { Remove-Item -LiteralPath $f -Force -ErrorAction Stop } catch { Write-Output ("WARN: leftover probe file {0}" -f $f) }
        }
    }
}

Write-Output '--- summary ---'
Write-Output ("probes_created={0} no_commit={1} committed={2}" -f $created.Count, $noCommit, $deltas.Count)

if ($noCommit -eq $Samples) {
    Write-Output 'RESULT: INCONCLUSIVE - no index commit observed at all.'
    Write-Output '        This is the expected pre-deployment state: FullTextIndex:Maintenance:Enabled is false,'
    Write-Output '        so the incremental watcher never runs. Re-run after the restart.'
    exit 3
}

$sorted = $deltas | Sort-Object
$p50 = $sorted[[int][Math]::Floor($sorted.Count * 0.5)]
$avg = [int](($sorted | Measure-Object -Average).Average)
$max = $sorted[-1]
Write-Output ("p50Ms={0} avgMs={1} maxMs={2}" -f $p50, $avg, $max)

if ($max -le $ThresholdMs) {
    Write-Output ("RESULT: PASS - every observed commit landed within {0}ms" -f $ThresholdMs)
    exit 0
}

Write-Output ("RESULT: FAIL - slowest commit {0}ms exceeds {1}ms" -f $max, $ThresholdMs)
exit 1
