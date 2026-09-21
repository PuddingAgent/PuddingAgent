<#
RSI-G2 read-only skill usage leaderboard.

Consumes skill-usage-YYYYMMDD.jsonl written by JsonlSkillUsageTelemetrySink
(one JSON object per line; fields: skillId, agentInstanceId, matchedKeywords,
injected, contentBytes, failureReason, occurredAtUtc, outcome).

Read-only by construction: this script opens telemetry files for reading only
and never writes outside -OutFile. It performs no state mutation.

WHY THE SCRIPT BODY IS PURE ASCII:
Windows PowerShell 5.1 decodes a BOM-less UTF-8 script as CP936, which turns
non-ASCII source text into either a parse error or mojibake. All non-ASCII
content handled here is DATA (JSONL values), read/written with an explicit
UTF8Encoding(no BOM) instance.

WHY SYNTHETIC FIXTURES MATTER:
The emitter only activates once the host restarts with the DI registration in
place, so the directory may legitimately be empty. An empty directory must not
be confused with a broken instrument: validate this script against a fixture
before trusting (or blaming) it.
#>
[CmdletBinding()]
param(
    [string]$TelemetryDir = 'D:\data\skill-usage',
    [string]$SkillsRoot   = '',
    [int]   $TopN         = 40,
    [string]$OutFile      = ''
)

$ErrorActionPreference = 'Stop'
$enc = New-Object System.Text.UTF8Encoding($false)
$out = New-Object System.Collections.Generic.List[string]

function Add-Line([string]$s) { [void]$out.Add($s) }
function Add-TableRow([string[]]$cells) { Add-Line ('| ' + ($cells -join ' | ') + ' |') }

$generatedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

# ---------------------------------------------------------------- discovery
$dirExists = Test-Path -LiteralPath $TelemetryDir
$files = @()
if ($dirExists) {
    $files = @(Get-ChildItem -LiteralPath $TelemetryDir -Filter 'skill-usage-*.jsonl' -File -ErrorAction SilentlyContinue | Sort-Object Name)
}

$status = 'OK'
if (-not $dirExists) { $status = 'NO_TELEMETRY_DIR' }
elseif ($files.Count -eq 0) { $status = 'NO_TELEMETRY_FILES' }

# ---------------------------------------------------------------- parse
$lineCount = 0
$malformed = 0
$blankLines = 0
$skills = @{}
$keywordHits = @{}

if ($status -eq 'OK') {
    foreach ($f in $files) {
        foreach ($ln in [System.IO.File]::ReadAllLines($f.FullName, $enc)) {
            if ([string]::IsNullOrWhiteSpace($ln)) { $blankLines++; continue }
            $lineCount++
            $rec = $null
            try { $rec = $ln | ConvertFrom-Json } catch { $malformed++; continue }
            $sid = [string]$rec.skillId
            if ([string]::IsNullOrEmpty($sid)) { $malformed++; continue }

            if (-not $skills.ContainsKey($sid)) {
                $skills[$sid] = [pscustomobject]@{
                    SkillId   = $sid
                    Records   = 0
                    Injected  = 0
                    Failed    = 0
                    Bytes     = 0
                    First     = $null
                    Last      = $null
                    Agents    = (New-Object 'System.Collections.Generic.HashSet[string]')
                    Keywords  = (New-Object 'System.Collections.Generic.HashSet[string]')
                    Reasons   = (New-Object 'System.Collections.Generic.HashSet[string]')
                }
            }

            $a = $skills[$sid]
            $a.Records++

            if ([string]$rec.outcome -eq 'injected') { $a.Injected++ } else { $a.Failed++ }
            if ($null -ne $rec.contentBytes) { $a.Bytes += [int]$rec.contentBytes }
            if ($rec.agentInstanceId) { [void]$a.Agents.Add([string]$rec.agentInstanceId) }
            if ($rec.failureReason)   { [void]$a.Reasons.Add([string]$rec.failureReason) }

            if ($rec.matchedKeywords) {
                foreach ($k in $rec.matchedKeywords) {
                    $ks = [string]$k
                    if ([string]::IsNullOrEmpty($ks)) { continue }
                    [void]$a.Keywords.Add($ks)
                    if (-not $keywordHits.ContainsKey($ks)) { $keywordHits[$ks] = 0 }
                    $keywordHits[$ks]++
                }
            }

            if ($null -ne $rec.occurredAtUtc) {
                try {
                    # IMPORTANT (fixture-caught twice): ConvertFrom-Json in Windows
                    # PowerShell 5.1 already turns ISO-8601 strings into [datetime]
                    # with Kind=Local BEFORE this code runs, so an explicit offset in
                    # the payload is resolved into the instant and the original text is
                    # gone. Re-parsing the re-rendered local string as UTC relabels
                    # local wall-clock as Z (10:00:00+00:00 came out as 18:00:00Z on a
                    # UTC+8 host), silently shifting every first/last-seen timestamp.
                    # So: trust the object we were actually handed; only parse when the
                    # value is genuinely still a string.
                    $raw = $rec.occurredAtUtc
                    if ($raw -is [System.DateTime]) {
                        $t = ([System.DateTime]$raw).ToUniversalTime()
                    } elseif ($raw -is [System.DateTimeOffset]) {
                        $t = ([System.DateTimeOffset]$raw).UtcDateTime
                    } else {
                        $t = [System.DateTimeOffset]::Parse(
                            [string]$raw,
                            [System.Globalization.CultureInfo]::InvariantCulture,
                            [System.Globalization.DateTimeStyles]::AssumeUniversal).UtcDateTime
                    }
                    if ($null -eq $a.First -or $t -lt $a.First) { $a.First = $t }
                    if ($null -eq $a.Last  -or $t -gt $a.Last)  { $a.Last  = $t }
                } catch { }
            }
        }
    }
}

# ------------------------------------------------- installed vs observed
$installedSkills = @()
$skillsRootUsed = $false
if (-not [string]::IsNullOrWhiteSpace($SkillsRoot) -and (Test-Path -LiteralPath $SkillsRoot)) {
    $skillsRootUsed = $true
    $installedSkills = @(
        Get-ChildItem -LiteralPath $SkillsRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Name }
    )
}
$zeroHit = @()
if ($skillsRootUsed) {
    $zeroHit = @($installedSkills | Where-Object { -not $skills.ContainsKey($_) } | Sort-Object)
}

# ---------------------------------------------------------------- summary
$rows = @($skills.Values | Sort-Object -Property @{Expression = 'Injected'; Descending = $true}, @{Expression = 'Records'; Descending = $true})
$totalInjected = 0
$totalFailed = 0
foreach ($r in $rows) { $totalInjected += $r.Injected; $totalFailed += $r.Failed }

Add-Line '# Skill usage leaderboard (RSI-G2)'
Add-Line ''
Add-Line ('- Generated (UTC): ' + $generatedUtc)
Add-Line ('- Telemetry dir: ' + $TelemetryDir)
Add-Line ('- Files scanned: ' + $files.Count)
Add-Line ('- Skills inventory root: ' + $(if ($skillsRootUsed) { $SkillsRoot } else { '(not supplied)' }))
Add-Line ''
Add-Line '## Summary'
Add-Line ''
Add-Line '```'
Add-Line ('STATUS=' + $status)
Add-Line ('FILES=' + $files.Count)
Add-Line ('LINES=' + $lineCount)
Add-Line ('BLANK_LINES=' + $blankLines)
Add-Line ('MALFORMED=' + $malformed)
Add-Line ('SKILLS_OBSERVED=' + $rows.Count)
Add-Line ('INJECTIONS=' + $totalInjected)
Add-Line ('READ_FAILURES=' + $totalFailed)
Add-Line ('DISTINCT_KEYWORDS_MATCHED=' + $keywordHits.Count)
if ($skillsRootUsed) {
    Add-Line ('INSTALLED_SKILLS=' + $installedSkills.Count)
    Add-Line ('ZERO_HIT_SKILLS=' + $zeroHit.Count)
}
Add-Line '```'
Add-Line ''

if ($status -ne 'OK') {
    Add-Line '## No telemetry yet'
    Add-Line ''
    Add-Line 'No records were available for aggregation. This is EXPECTED until the'
    Add-Line 'running host is restarted with the telemetry DI registration loaded:'
    Add-Line 'the emitter lives in the host process, so an already-running process'
    Add-Line 'that predates that registration writes nothing.'
    Add-Line ''
    Add-Line 'Do NOT read this as "no skill was ever used". Absence of records is'
    Add-Line 'currently indistinguishable from absence of instrumentation.'
    Add-Line ''
    Add-Line 'Validate the aggregation with a synthetic fixture instead of assuming'
    Add-Line 'the instrument is broken or healthy.'
}

if ($rows.Count -gt 0) {
    Add-Line ('## Skill leaderboard (top ' + $TopN + ', ranked by injections)')
    Add-Line ''
    Add-TableRow @('#', 'skillId', 'injected', 'readFailed', 'records', 'distinctKw', 'agents', 'bytes', 'firstSeenUtc', 'lastSeenUtc')
    Add-TableRow @('---', '---', '---:', '---:', '---:', '---:', '---:', '---:', '---', '---')
    $i = 0
    foreach ($r in $rows) {
        $i++
        if ($i -gt $TopN) { break }
        Add-TableRow @(
            [string]$i,
            $r.SkillId,
            [string]$r.Injected,
            [string]$r.Failed,
            [string]$r.Records,
            [string]$r.Keywords.Count,
            [string]$r.Agents.Count,
            [string]$r.Bytes,
            $(if ($r.First) { $r.First.ToString('yyyy-MM-ddTHH:mm:ssZ') } else { '-' }),
            $(if ($r.Last)  { $r.Last.ToString('yyyy-MM-ddTHH:mm:ssZ') }  else { '-' })
        )
    }
    Add-Line ''
}

if ($keywordHits.Count -gt 0) {
    Add-Line '## Most matched keywords'
    Add-Line ''
    Add-Line 'Read-only evidence for keyword ownership decisions (share vs retarget).'
    Add-Line 'It does NOT by itself justify deleting a keyword.'
    Add-Line ''
    Add-TableRow @('keyword', 'timesMatched')
    Add-TableRow @('---', '---:')
    $kwTop = @($keywordHits.GetEnumerator() | Sort-Object -Property @{Expression = 'Value'; Descending = $true} | Select-Object -First $TopN)
    foreach ($kv in $kwTop) { Add-TableRow @([string]$kv.Key, [string]$kv.Value) }
    Add-Line ''
}

if ($skillsRootUsed) {
    Add-Line ('## Installed but never observed (' + $zeroHit.Count + ')')
    Add-Line ''
    Add-Line 'Answer to "which of the installed skills have actually been used".'
    Add-Line 'This list is only valid once the instrumentation is live; while'
    Add-Line 'STATUS is not OK it reflects the missing emitter, not disuse.'
    Add-Line ''
    if ($zeroHit.Count -eq 0) {
        Add-Line '(none)'
    } else {
        foreach ($z in $zeroHit) { Add-Line ('- ' + $z) }
    }
    Add-Line ''
}

Add-Line '## Known limitations'
Add-Line ''
Add-Line '- Zero-hit detection requires -SkillsRoot; without it only observed skills appear.'
Add-Line '- Keywords are only recorded for skills that were actually injected, so a'
Add-Line '  keyword that never matched a skill is invisible here.'
Add-Line '- This report is raw counts. It does not judge usefulness: a high injection'
Add-Line '  count does not prove a skill helped, and a low count does not prove harm.'

$text = ($out -join "`r`n") + "`r`n"

if ([string]::IsNullOrWhiteSpace($OutFile)) {
    Write-Output $text
} else {
    $parent = Split-Path -Parent $OutFile
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent)) {
        [void](New-Item -ItemType Directory -Path $parent -Force)
    }
    [System.IO.File]::WriteAllText($OutFile, $text, $enc)
    Write-Output ('OUTFILE=' + $OutFile)
}

# ASCII-only machine-readable tail so callers never depend on console encoding.
Write-Output ('STATUS=' + $status)
Write-Output ('FILES=' + $files.Count)
Write-Output ('LINES=' + $lineCount)
Write-Output ('MALFORMED=' + $malformed)
Write-Output ('SKILLS_OBSERVED=' + $rows.Count)
Write-Output ('INJECTIONS=' + $totalInjected)
Write-Output ('READ_FAILURES=' + $totalFailed)
if ($skillsRootUsed) {
    Write-Output ('INSTALLED_SKILLS=' + $installedSkills.Count)
    Write-Output ('ZERO_HIT_SKILLS=' + $zeroHit.Count)
}
if ($status -ne 'OK') {
    Write-Output 'ACTION=restart host to activate the telemetry sink before treating counters as evidence'
}
