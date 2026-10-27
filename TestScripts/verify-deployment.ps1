<#
.SYNOPSIS
    Verify that a running Pudding host is actually executing freshly deployed assemblies,
    and that a specific code token really shipped inside the deployed binary.

.DESCRIPTION
    Why this exists (2026-10-27):
      "restart != deploy" and "file exists != code shipped" are the two mistakes that keep
      costing us false conclusions. This script turns the manual probe into one command with
      controls, so a negative answer can never be produced by a broken instrument.

    Three independent checks:
      1. PROCESS FRESHNESS  - running process start time vs assembly file mtime.
         A process started BEFORE its assembly was written is running stale code.
      2. TOKEN PRESENCE     - scan the deployed DLL for the expected token.
      3. CONTROLS           - a positive control that MUST be found and a negative control
         that MUST NOT be found. Without them, "0 hits" proves nothing.

    Encoding trap this script encodes (learned 2026-10-27):
      .NET assembly metadata (type/method/field NAMES) is stored as UTF-8,
      while string LITERALS are stored as UTF-16LE.
      Probing a method name with UTF-16 returns 0 and looks like "code missing".
      Therefore every -ExpectToken is probed in BOTH encodings; a token is considered
      present if either encoding finds it.

.PARAMETER ProcessName
    Process name without .exe. Default: PuddingAgent

.PARAMETER BaseDir
    Directory containing the assemblies the process should be running from.
    Default: the directory of the running process image.

.PARAMETER Assembly
    Assembly file names to check. Default covers the Pudding stack.

.PARAMETER ExpectToken
    Tokens that MUST be present in -ProbeAssembly. Repeatable.

.PARAMETER ForbidToken
    Tokens that MUST NOT be present. Repeatable. Use the same tokens as -ExpectToken
    against a pre-change build to prove the probe can fail.

.PARAMETER PositiveControl
    Token that MUST be found, proving the scanner works at all.
    Default: StorageMaintenance

.PARAMETER NegativeControl
    Token that MUST NOT be found, proving the scanner does not match everything.
    Default: NoSuchToken_XYZ123_Absent

.PARAMETER ProbeAssembly
    Which assembly to scan for tokens. Default: PuddingPlatform.dll

.EXAMPLE
    pwsh -NoProfile -File TestScripts/verify-deployment.ps1

.EXAMPLE
    # expected-red run: a bogus token must produce exit 1
    pwsh -NoProfile -File TestScripts/verify-deployment.ps1 -ExpectToken NoSuchShippedCode_ZZZ

.NOTES
    ASCII-only on purpose: Windows PowerShell 5.1 decodes BOM-less .ps1 as ANSI and would
    corrupt non-ASCII literals.
#>
[CmdletBinding()]
param(
    [string]$ProcessName = 'PuddingAgent',
    [string]$BaseDir,
    [string[]]$Assembly = @('PuddingAgent.dll', 'PuddingRuntime.dll', 'PuddingPlatform.dll', 'PuddingHost.dll'),
    [string[]]$ExpectToken = @(),
    [string[]]$ForbidToken = @(),
    [string]$PositiveControl = 'StorageMaintenance',
    [string]$NegativeControl = 'NoSuchToken_XYZ123_Absent',
    [string]$ProbeAssembly = 'PuddingPlatform.dll'
)

$ErrorActionPreference = 'Stop'

# With `pwsh -File`, `-ExpectToken a,b` arrives as ONE string element. Split here so the
# command line form and the array form behave the same.
$ExpectToken = @($ExpectToken | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$ForbidToken = @($ForbidToken | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$script:failures = New-Object System.Collections.Generic.List[string]
$script:notes = New-Object System.Collections.Generic.List[string]

function Say([string]$text) {
    Write-Output $text
}

function Fail([string]$text) {
    $script:failures.Add($text)
    Write-Output ("FAIL  " + $text)
}

function Pass([string]$text) {
    Write-Output ("ok    " + $text)
}

function Note([string]$text) {
    $script:notes.Add($text)
    Write-Output ("note  " + $text)
}

# --- byte scanning ----------------------------------------------------------
# Latin1 is a 1:1 byte<->char mapping, so IndexOf over the decoded string is a
# byte search without any character-set normalisation.
function Get-Latin1([byte[]]$bytes) {
    return [Text.Encoding]::GetEncoding(28591).GetString($bytes)
}

function Count-Occurrence([string]$haystack, [string]$needle) {
    if ([string]::IsNullOrEmpty($needle)) { return 0 }
    $count = 0
    $i = 0
    while ($true) {
        $i = $haystack.IndexOf($needle, $i, [StringComparison]::Ordinal)
        if ($i -lt 0) { break }
        $count++
        $i = $i + 1
    }
    return $count
}

function Measure-Token([string]$path, [string]$token) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $latin = Get-Latin1 $bytes

    $utf8Needle  = Get-Latin1 ([Text.Encoding]::UTF8.GetBytes($token))
    $utf16Needle = Get-Latin1 ([Text.Encoding]::Unicode.GetBytes($token))

    $utf8  = Count-Occurrence $latin $utf8Needle
    $utf16 = Count-Occurrence $latin $utf16Needle

    return [pscustomobject]@{
        Token = $token
        Utf8  = $utf8
        Utf16 = $utf16
        Total = $utf8 + $utf16
    }
}

# --- 1. process freshness ---------------------------------------------------
Say '=== 1. process freshness ==='

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    Fail ("process '$ProcessName' not running")
    $procStart = $null
} else {
    $procStart = $proc.StartTime
    Pass ("process {0} pid={1} started={2}" -f $proc.ProcessName, $proc.Id, $procStart.ToString('yyyy-MM-dd HH:mm:ss'))
}

if (-not $BaseDir) {
    if ($proc -and $proc.Path) {
        $BaseDir = Split-Path -Parent $proc.Path
    } elseif ($proc) {
        Fail 'cannot resolve process image path; pass -BaseDir explicitly'
    }
}
if ($BaseDir) {
    Say ("      base dir: " + $BaseDir)
}

# --- 2. assembly mtime vs process start -------------------------------------
Say '=== 2. assembly freshness ==='

$newestMtime = $null
if ($BaseDir -and (Test-Path $BaseDir)) {
    foreach ($name in $Assembly) {
        $file = Join-Path $BaseDir $name
        if (-not (Test-Path $file)) {
            Note ("assembly missing (may be normal for this build): " + $name)
            continue
        }
        $item = Get-Item $file
        $mtime = $item.LastWriteTime
        if (-not $newestMtime -or $mtime -gt $newestMtime) { $newestMtime = $mtime }

        if ($procStart) {
            if ($mtime -le $procStart) {
                Pass ("{0} mtime={1} <= start={2}" -f $name, $mtime.ToString('yyyy-MM-dd HH:mm:ss'), $procStart.ToString('yyyy-MM-dd HH:mm:ss'))
            } else {
                Fail ("{0} mtime={1} is NEWER than process start={2} -> running stale code" -f `
                      $name, $mtime.ToString('yyyy-MM-dd HH:mm:ss'), $procStart.ToString('yyyy-MM-dd HH:mm:ss'))
            }
        }
    }
} else {
    Fail ("base dir not found: " + $BaseDir)
}

if ($procStart -and $newestMtime) {
    $lagSeconds = [math]::Round(($procStart - $newestMtime).TotalSeconds, 1)
    Note ("process started {0}s after the newest assembly was written" -f $lagSeconds)
}

# --- 3. tokens + controls ---------------------------------------------------
Say '=== 3. token presence (controls mandatory) ==='

if (-not $BaseDir) {
    Fail 'no base dir; cannot scan assemblies'
} else {
    $probePath = Join-Path $BaseDir $ProbeAssembly
    if (-not (Test-Path $probePath)) {
        Fail ("probe assembly not found: " + $probePath)
    } else {
        $pos = Measure-Token $probePath $PositiveControl
        if ($pos.Total -gt 0) {
            Pass ("positive control '{0}' found (utf8={1} utf16={2})" -f $PositiveControl, $pos.Utf8, $pos.Utf16)
        } else {
            Fail ("positive control '{0}' NOT found -> scanner is broken, ignore every other result" -f $PositiveControl)
        }

        $neg = Measure-Token $probePath $NegativeControl
        if ($neg.Total -eq 0) {
            Pass ("negative control '{0}' absent" -f $NegativeControl)
        } else {
            Fail ("negative control '{0}' found {1} times -> scanner matches everything" -f $NegativeControl, $neg.Total)
        }

        foreach ($token in $ExpectToken) {
            $hit = Measure-Token $probePath $token
            if ($hit.Total -gt 0) {
                Pass ("expected token '{0}' present (utf8={1} utf16={2})" -f $token, $hit.Utf8, $hit.Utf16)
            } else {
                Fail ("expected token '{0}' NOT present in {1}" -f $token, $ProbeAssembly)
            }
        }

        foreach ($token in $ForbidToken) {
            $hit = Measure-Token $probePath $token
            if ($hit.Total -eq 0) {
                Pass ("forbidden token '{0}' absent" -f $token)
            } else {
                Fail ("forbidden token '{0}' present {1} times" -f $token, $hit.Total)
            }
        }
    }
}

# --- summary ----------------------------------------------------------------
Say '=== summary ==='
if ($script:failures.Count -eq 0) {
    Say 'RESULT: PASS'
    Say 'All freshness and token checks passed.'
    Say 'Rollback path: git revert <bad-sha> (or git reset --hard <good-sha>) then rebuild and restart.'
    Say 'Do NOT patch the deployed bin directory by hand - the next deploy overwrites it.'
    exit 0
}

Say ("RESULT: FAIL ({0} problem(s))" -f $script:failures.Count)
foreach ($f in $script:failures) { Say ('  - ' + $f) }
Say 'Rollback path: git revert <bad-sha> (or git reset --hard <good-sha>) then rebuild and restart.'
exit 1
