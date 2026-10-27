# Post-deployment acceptance for the retrieval work landed on 2026-10-28.
#
# Why this exists: 11 commits are pending a single restart. Verifying them by hand
# across several tool rounds is slow and easy to half-do. This script turns the
# pending list into an executable gate: one command, one PASS/FAIL table.
#
# It is DESIGNED TO FAIL before the restart. Running it now is the expected-red
# control: if it passed on the current (pre-deploy) binaries, the probes would be
# worthless.
#
# Checks:
#   1. freshness  - running process started AFTER the HEAD commit time
#   2. tokens     - each landed change left a unique string in its assembly
#                  (UTF-8 metadata names and UTF-16 string literals are BOTH probed;
#                   a method name only appears as UTF-8, a log template only as UTF-16)
#   3. config     - FullTextIndex:Maintenance:Enabled is true
#
# SUSPECT rule (added after a real contradiction on 2026-10-28):
#   A token whose introducing commit is NEWER than the assembly's mtime is reported as
#   SUSPECT, not PASS. Observed case: IndexScopeResolver (commit 00:23:39) was found inside
#   PuddingRuntime.dll whose mtime is 23:53:26 of the previous day. Either the deploy tool
#   does not refresh timestamps, or the deploy directory has been partially overwritten
#   without a restart. Both are operational hazards worth surfacing; silently calling that
#   a PASS would make the whole gate untrustworthy.
#
# ASCII-only on purpose: Windows PowerShell 5.1 decodes BOM-less .ps1 as ANSI.
[CmdletBinding()]
param(
    [string]$RepoRoot = 'D:\CodeProject\PuddingAgent\PuddingAgent',
    [string]$ConfigPath = 'D:\Data\config\system.json',
    [string]$ProcessName = 'PuddingAgent'
)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]

function Say([string]$t) { Write-Output $t }
function Ok([string]$t) { Write-Output ("PASS  " + $t) }
function Bad([string]$t) { $script:failures.Add($t); Write-Output ("FAIL  " + $t) }

# ---- probe helper (same controls discipline as verify-deployment.ps1) -------
function Get-Latin1([byte[]]$bytes) { [Text.Encoding]::GetEncoding(28591).GetString($bytes) }

function Count-Occ([string]$hay, [string]$needle) {
    if ([string]::IsNullOrEmpty($needle)) { return 0 }
    $c = 0; $i = 0
    while ($true) {
        $i = $hay.IndexOf($needle, $i, [StringComparison]::Ordinal)
        if ($i -lt 0) { break }
        $c++; $i++
    }
    return $c
}

function Measure-Token([string]$path, [string]$token) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $latin = Get-Latin1 $bytes
    $u8 = Count-Occ $latin (Get-Latin1 ([Text.Encoding]::UTF8.GetBytes($token)))
    $u16 = Count-Occ $latin (Get-Latin1 ([Text.Encoding]::Unicode.GetBytes($token)))
    return ($u8 + $u16)
}

# ---- 0. locate the deployed assemblies -------------------------------------
$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Say 'FATAL: host process not running'; exit 1 }
$baseDir = Split-Path -Parent $proc.Path
Say ("=== host pid={0} started={1}" -f $proc.Id, $proc.StartTime.ToString('yyyy-MM-dd HH:mm:ss'))
Say ("=== assemblies: {0}" -f $baseDir)

# ---- 1. freshness vs HEAD ---------------------------------------------------
Say '=== 1. process vs HEAD ==='
$headIso = (& git -C $RepoRoot log -1 --format='%cI') 2>$null
$headSha = (& git -C $RepoRoot log -1 --format='%h') 2>$null
if ([string]::IsNullOrWhiteSpace($headIso)) {
    Bad 'cannot read HEAD from git'
} else {
    $headTime = [DateTimeOffset]::Parse($headIso).LocalDateTime
    Say ("      HEAD {0} at {1}" -f $headSha, $headTime.ToString('yyyy-MM-dd HH:mm:ss'))
    if ($proc.StartTime -gt $headTime) {
        Ok 'process started after HEAD commit (binaries are newer than the last commit)'
    } else {
        Bad ("process started {0:hh\:mm\:ss} BEFORE HEAD commit -> running stale code" -f ($headTime - $proc.StartTime))
    }
}

# ---- 2. token table --------------------------------------------------------
Say '=== 2. landed changes present in binaries ==='
$expectations = @(
    @{ Token = 'IndexScopeResolver';                  Asm = 'PuddingRuntime.dll';  What = 'W1 index scope ascension'; Since = 'ed2bfcb' },
    @{ Token = 'DropRetriesForMissingFiles';          Asm = 'PuddingCodeIndex.dll'; What = 'F3 prune stale retries'; Since = '3029082' },
    @{ Token = 'dropped {Dropped} pending retries';   Asm = 'PuddingCodeIndex.dll'; What = 'F3 prune log line'; Since = '3029082' },
    @{ Token = 'cache_size=-32768';                   Asm = 'PuddingPlatform.dll'; What = 'G4 platform page cache'; Since = '4c2b6b2' },
    @{ Token = 'StorageAutomaticCleanupLoop';         Asm = 'PuddingPlatform.dll'; What = 'automatic cleanup loop'; Since = 'de73598' },
    @{ Token = 'half-open suspected';                 Asm = 'HarnessAgent.Core.dll'; What = 'Feishu half-open self-heal'; Since = 'b003205' },
    @{ Token = 'Channel skipped: must be referenced'; Asm = 'PuddingHost.dll';     What = 'Feishu orphan channel warning'; Since = 'c045bbc' },
    @{ Token = 'StorageAutomaticCleanupDryRun';       Asm = 'PuddingPlatform.dll'; What = 'dry-run extraction'; Since = '325e49c' }
)

$probeAsm = 'PuddingPlatform.dll'
$probePath = Join-Path $baseDir $probeAsm
if (-not (Test-Path $probePath)) { Bad ("probe assembly missing: " + $probePath) }

if (Test-Path $probePath) {
    # controls: without them a zero hit proves nothing
    $pos = Measure-Token $probePath 'StorageMaintenance'
    $neg = Measure-Token $probePath 'NoSuchToken_XYZ123_Absent'
    if ($pos -gt 0) { Ok ("positive control found ({0} hits)" -f $pos) } else { Bad 'positive control NOT found -> scanner broken, ignore all results below' }
    if ($neg -eq 0) { Ok 'negative control absent' } else { Bad 'negative control found -> scanner matches everything' }
}

foreach ($e in $expectations) {
    $path = Join-Path $baseDir $e.Asm
    if (-not (Test-Path $path)) { Bad ("{0}: assembly missing ({1})" -f $e.What, $e.Asm); continue }
    $hits = Measure-Token $path $e.Token
    if ($hits -le 0) {
        Bad ("{0}  token '{1}' NOT in {2}" -f $e.What, $e.Token, $e.Asm)
        continue
    }

    # SUSPECT：token 命中但程序集比引入它的提交还旧 —— 不使用 PASS，避免把
    # 「部署工具没刷新时间戳」或「部署目录被部分覆盖但未重启」误报成已完成。
    $asmTime = (Get-Item $path).LastWriteTime
    $sinceIso = (& git -C $RepoRoot log -1 --format='%cI' $e.Since) 2>$null
    if (-not [string]::IsNullOrWhiteSpace($sinceIso)) {
        $sinceTime = [DateTimeOffset]::Parse($sinceIso).LocalDateTime
        if ($asmTime -lt $sinceTime) {
            $failures.Add(("{0}: SUSPECT (token present, but assembly mtime {1} predates commit {2} at {3})" -f $e.What, $asmTime.ToString('yyyy-MM-dd HH:mm:ss'), $e.Since, $sinceTime.ToString('yyyy-MM-dd HH:mm:ss')))
            Write-Output ("SUSPECT  {0}  [assembly {1} older than {2}]" -f $e.What, $asmTime.ToString('HH:mm:ss'), $e.Since)
            continue
        }
    }

    Ok ("{0}  [{1} x{2}]" -f $e.What, $e.Asm, $hits)
}

# ---- 3. maintenance config --------------------------------------------------
Say '=== 3. full-text maintenance config ==='
if (-not (Test-Path $ConfigPath)) {
    Bad ("config missing: " + $ConfigPath)
} else {
    try {
        $json = Get-Content $ConfigPath -Raw | ConvertFrom-Json
        $maint = $json.FullTextIndex.Maintenance
        if ($null -eq $maint) {
            Bad 'FullTextIndex.Maintenance section absent -> maintenance defaults to disabled'
        } elseif ($maint.Enabled -ne $true) {
            Bad ("FullTextIndex.Maintenance.Enabled = " + $maint.Enabled)
        } else {
            Ok ("Maintenance.Enabled=true  MaxCoalesceWait=" + $maint.MaxCoalesceWait)
        }
    } catch {
        Bad ('config parse failed: ' + $_.Exception.Message)
    }
}

# ---- summary ---------------------------------------------------------------
Say '=== summary ==='
if ($failures.Count -eq 0) {
    Say 'RESULT: PASS - all pending changes are live'
    Say 'Next: run TestScripts/sample-host-io-shape.ps1 (expect read IO to drop ~10x if the page-cache hypothesis holds).'
    Say '      Then verify G2 latency with temp/g2-latency.ps1 (expect deltaMs in 500-1000).'
    exit 0
}

Say ("RESULT: FAIL ({0} problem(s))" -f $failures.Count)
foreach ($f in $failures) { Say ('  - ' + $f) }
Say 'If every token check failed and freshness failed too, the host simply has not been redeployed yet.'
exit 1
