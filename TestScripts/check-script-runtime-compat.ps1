<#
.SYNOPSIS
  C-6 gate: every command and parameter a script references must RESOLVE in the
  PowerShell engine that is actually running this check.

.DESCRIPTION
  C-5 (check-script-encodings.ps1) answers two questions only:
    "can this file be LOADED (decode + parse)?" and
    "does it declare an engine this machine does not have (#requires)?"
  It cannot answer "will the commands INSIDE it actually bind here?".

  A script can parse perfectly, declare nothing, and still die on the first parameter
  that does not exist in the running engine.

  Concrete case (found 2026-10-08):
    TestScripts/check-circular-deps.ps1 parses clean and declares no engine
    (C-5 reports errors=0, req=-), yet it calls "Split-Path -LeafBase", which is a
    PowerShell 6+ parameter. Under Windows PowerShell 5.1 that call throws
    ParameterBindingException. C-5 cannot see this; C-6 does.

  For every script in scope C-6:
    (1) decodes the bytes EXACTLY like the running engine labels them
        (EF BB BF -> UTF-8, otherwise [Text.Encoding]::Default), then
    (2) parses the text with the real PowerShell parser, then
    (3) walks every CommandAst, resolves the command name with Get-Command, and
    (4) for resolved cmdlet/function commands checks each named parameter, accepting
        unambiguous prefixes (PowerShell binds "-Fore" to "-ForegroundColor").
  Parameters that do not resolve are PARAM_MISSING and fail the gate. Command names
  that do not resolve are CMD_MISSING and fail the gate, UNLESS the same file also
  injects definitions dynamically (see below), in which case they are reported as
  CMD_UNRESOLVED and only warn.

  DYNAMIC DEFINITION INJECTION
    A file that builds a scriptblock at runtime, for example
        $sb = [scriptblock]::Create($text); . $sb
    can define functions this gate can never see. Real example:
    test-pudding-deployment-gates.ps1 dot-sources the pure predicate Test-CoreQuiescent
    out of another file, so Test-CoreQuiescent looks "missing" while the script in fact
    runs and passes. Such files get CMD_UNRESOLVED instead of CMD_MISSING.
    TRADE-OFF, stated plainly: in a file that also injects definitions dynamically, a
    genuinely missing command is downgraded to a warning as well. Use -StrictCommands
    when you want the gate to fail on CMD_UNRESOLVED too.

  Deliberate scope limits (printed in the report footer, not hidden):
    - names defined as functions inside the same file are never flagged
    - path-invoked scripts ("& .\x.ps1") and dynamic invocations ("& $name") are skipped
    - native applications (CommandType Application, for example git/dotnet/python)
      are name-checked only: their parameter surface is not described by Get-Command
    - splatting (@args), positional arguments and provider dynamic parameters are
      NOT analysed
    - only names that are literally present in the source can be checked

.PARAMETER Root
  Directory to scan. Default: TestScripts (resolved relative to the current dir).

.PARAMETER Recurse
  Scan subdirectories too. Default: top level only.

.PARAMETER ExcludeDir
  Directory names (any path segment) to skip. Default: temp, node_modules, .git, bin, obj, dist, .venv, .pudding

.PARAMETER MaxReport
  Maximum findings printed per category. Default: 200.

.PARAMETER StrictCommands
  Treat CMD_UNRESOLVED (unresolved name inside a file that injects definitions at
  runtime) as a failure instead of a warning.

.PARAMETER SelfTest
  Run the in-memory controls and exit. Writes no files.

.EXIT CODES
  0 = PASS       : files enumerated, no unresolvable command or parameter
  1 = FAIL       : at least one unresolvable command or parameter in scope
  2 = FAIL       : a file in scope cannot be decoded/parsed, so it cannot be analysed
  3 = FAIL-CLOSED: no script enumerated in scope; refuses to report green
  4 = FAIL       : self-test did not detect the planted defects
#>

[CmdletBinding()]
param(
    [string]$Root = 'TestScripts',
    [switch]$Recurse,
    [string[]]$ExcludeDir = @('temp', 'node_modules', '.git', 'bin', 'obj', 'dist', '.venv', '.pudding'),
    [int]$MaxReport = 200,
    [switch]$StrictCommands,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$EXIT_PASS = 0
$EXIT_FINDINGS = 1
$EXIT_UNANALYSABLE = 2
$EXIT_EMPTY = 3
$EXIT_SELFTEST = 4

$LF = [string][char]10
$BS = [string][char]92
$SQ = [string][char]39

# ---------------------------------------------------------------------------
# Decode exactly the way the running engine labels raw bytes, then parse.
# ---------------------------------------------------------------------------
function Get-DecodedText {
    param([byte[]]$Bytes)
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) {
        return [System.Text.Encoding]::UTF8.GetString($Bytes, 3, $Bytes.Length - 3)
    }
    return [System.Text.Encoding]::Default.GetString($Bytes)
}

function Get-ParsedAst {
    param([string]$Text)
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors)
    $errCount = 0
    $firstError = ''
    if ($null -ne $errors -and $errors.Count -gt 0) {
        $errCount = $errors.Count
        $e = $errors[0]
        $firstError = '{0}@L{1}C{2}' -f $e.ErrorId, $e.Extent.StartLineNumber, $e.Extent.StartColumnNumber
    }
    return New-Object PSObject -Property @{
        Ast        = $ast
        ErrorCount = $errCount
        FirstError = $firstError
    }
}

# ---------------------------------------------------------------------------
# Does this file build definitions at runtime? Then unresolved names are expected.
# ---------------------------------------------------------------------------
function Test-DynamicDefinitionInjection {
    param([string]$Text)
    if ($Text -match '\[scriptblock\]::Create') { return $true }
    if ($Text -match 'Invoke-Expression') { return $true }
    return $false
}

# ---------------------------------------------------------------------------
# Returning one item unrolls a collection in PowerShell, and a New-Object PSObject
# does NOT get the scalar ".Count = 1" shim. Always re-wrap before counting.
# ---------------------------------------------------------------------------
function Get-FindingCount {
    param($Findings)
    return @($Findings).Count
}

# ---------------------------------------------------------------------------
# Core: resolve every literal command name and every named parameter.
# ---------------------------------------------------------------------------
function Get-CommandFindings {
    param(
        [System.Management.Automation.Language.ScriptBlockAst]$Ast,
        [bool]$DynamicDefines = $false
    )

    $findings = New-Object 'System.Collections.Generic.List[object]'

    $defined = @{}
    $fnNodes = $Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)
    foreach ($f in $fnNodes) { $defined[$f.Name.ToLowerInvariant()] = $true }

    $cmdNodes = $Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)
    foreach ($c in $cmdNodes) {
        $name = $c.GetCommandName()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }

        $lower = $name.ToLowerInvariant()
        if ($defined.ContainsKey($lower)) { continue }
        if ($name.IndexOf($BS) -ge 0) { continue }
        if ($name.IndexOf([char]47) -ge 0) { continue }
        if ($lower.EndsWith('.ps1')) { continue }

        $cmd = @(Get-Command -Name $name -ErrorAction SilentlyContinue)
        if ($cmd.Count -eq 0) {
            $kind = 'CMD_MISSING'
            if ($DynamicDefines) { $kind = 'CMD_UNRESOLVED' }
            $findings.Add((New-Object PSObject -Property @{
                Kind = $kind; Name = $name; Param = ''; Line = $c.Extent.StartLineNumber
            }))
            continue
        }
        $resolved = $cmd[0]

        # Native applications do not publish a PowerShell parameter surface.
        if ($resolved.CommandType -eq 'Application') { continue }

        $paramKeys = @()
        if ($null -ne $resolved.Parameters) { $paramKeys = @($resolved.Parameters.Keys) }
        if ($paramKeys.Count -eq 0) { continue }

        $lowerKeys = @{}
        foreach ($k in $paramKeys) { $lowerKeys[$k.ToLowerInvariant()] = $k }

        foreach ($el in $c.CommandElements) {
            if (-not ($el -is [System.Management.Automation.Language.CommandParameterAst])) { continue }
            $pn = $el.ParameterName
            if ([string]::IsNullOrWhiteSpace($pn)) { continue }
            $pnl = $pn.ToLowerInvariant()
            if ($lowerKeys.ContainsKey($pnl)) { continue }
            $prefixHits = 0
            foreach ($k in @($lowerKeys.Keys)) {
                if ($k.StartsWith($pnl)) { $prefixHits++ }
            }
            if ($prefixHits -eq 1) { continue }
            $findings.Add((New-Object PSObject -Property @{
                Kind = 'PARAM_MISSING'; Name = $name; Param = $pn; Line = $el.Extent.StartLineNumber
            }))
        }
    }

    return $findings
}

function Get-FindingsFromText {
    param([string]$Text)
    $parsed = Get-ParsedAst -Text $Text
    if ($parsed.ErrorCount -gt 0) { return @() }
    $dyn = Test-DynamicDefinitionInjection -Text $Text
    return @(Get-CommandFindings -Ast $parsed.Ast -DynamicDefines $dyn)
}

function Test-NoFinding {
    param([string]$Text, [string]$Kind)
    $f = @(Get-FindingsFromText -Text $Text)
    foreach ($x in $f) { if ($x.Kind -eq $Kind) { return $false } }
    return $true
}

# ---------------------------------------------------------------------------
# Self test: in-memory controls only, no file I/O.
# ---------------------------------------------------------------------------
if ($SelfTest) {
    $failures = @()
    $curEngine = $PSVersionTable.PSVersion.ToString()

    # Control A: a command that cannot exist -> must be reported CMD_MISSING.
    if (Test-NoFinding -Text ('Get-PuddingNoSuchCommandXYZ -Quiet' + $LF) -Kind 'CMD_MISSING') {
        $failures += 'A: missing command was not detected'
    }

    # Control B: resolvable command + resolvable parameter -> must stay clean.
    if (-not (Test-NoFinding -Text ('Split-Path -Parent ' + $SQ + 'x' + $SQ + $LF) -Kind 'PARAM_MISSING')) {
        $failures += 'B: false positive on Split-Path -Parent'
    }
    if (-not (Test-NoFinding -Text ('Split-Path -Parent ' + $SQ + 'x' + $SQ + $LF) -Kind 'CMD_MISSING')) {
        $failures += 'B: false positive CMD_MISSING on Split-Path'
    }

    # Control C: engine-relative parameter availability. The assertion is expressed
    # against THIS engine so it holds under both 5.1 and 7+.
    $splitPath = @(Get-Command -Name 'Split-Path' -ErrorAction SilentlyContinue)
    $leafBaseExists = $false
    if ($splitPath.Count -gt 0 -and $null -ne $splitPath[0].Parameters) {
        $leafBaseExists = $splitPath[0].Parameters.ContainsKey('LeafBase')
    }
    $cText = 'Split-Path -LeafBase ' + $SQ + 'x' + $SQ + $LF
    $cClean = Test-NoFinding -Text $cText -Kind 'PARAM_MISSING'
    if ($leafBaseExists) {
        if (-not $cClean) { $failures += 'C: flagged -LeafBase on an engine that HAS it' }
    }
    else {
        if ($cClean) { $failures += 'C: -LeafBase not flagged although this engine lacks it' }
    }

    # Control D: a function defined in the same text is not a missing command.
    $dText = 'function Get-PuddingLocalHelper { return 1 }' + $LF + 'Get-PuddingLocalHelper' + $LF
    if (-not (Test-NoFinding -Text $dText -Kind 'CMD_MISSING')) {
        $failures += 'D: locally defined function reported as CMD_MISSING'
    }

    # Control E: native application -> name only, parameters not judged.
    $appCmd = @(Get-Command -Name 'cmd.exe' -ErrorAction SilentlyContinue)
    if ($appCmd.Count -gt 0) {
        $eText = 'cmd.exe -NotARealPowerShellParameter' + $LF
        if (-not (Test-NoFinding -Text $eText -Kind 'PARAM_MISSING')) {
            $failures += 'E: parameter check applied to a native application'
        }
    }

    # Control F: unambiguous prefix must be accepted (engine-relative, tolerant).
    $wh = @(Get-Command -Name 'Write-Host' -ErrorAction SilentlyContinue)
    if ($wh.Count -gt 0 -and $null -ne $wh[0].Parameters) {
        $pref = @()
        foreach ($k in @($wh[0].Parameters.Keys)) { if ($k.ToLowerInvariant().StartsWith('fore')) { $pref += $k } }
        if ($pref.Count -eq 1) {
            $fText = 'Write-Host -Fore ' + $SQ + 'x' + $SQ + $LF
            if (-not (Test-NoFinding -Text $fText -Kind 'PARAM_MISSING')) {
                $failures += 'F: unambiguous prefix rejected'
            }
        }
    }

    # Control G: a file that injects definitions at runtime must downgrade an
    # unresolved name to CMD_UNRESOLVED, never CMD_MISSING.
    $gText = '$sb = [scriptblock]::Create(' + $SQ + 'function Get-PuddingInjected { return 1 }' + $SQ + ')' + $LF +
        '. $sb' + $LF + 'Get-PuddingInjected' + $LF
    if (-not (Test-NoFinding -Text $gText -Kind 'CMD_MISSING')) {
        $failures += 'G: dynamic-injection file still reported CMD_MISSING'
    }
    if (Test-NoFinding -Text $gText -Kind 'CMD_UNRESOLVED') {
        $failures += 'G: dynamic-injection file did not report CMD_UNRESOLVED'
    }

    # Control H: a SINGLE finding must still be counted as 1. A one-item collection is
    # unrolled by PowerShell and a bare PSObject has no Count, so a naive count is 0.
    $hFindings = @(Get-FindingsFromText -Text ('Get-PuddingNoSuchCommandXYZ -Quiet' + $LF))
    if ((Get-FindingCount -Findings $hFindings) -ne 1) {
        $failures += ('H: single finding counted as ' + (Get-FindingCount -Findings $hFindings) + ', expected 1')
    }

    Write-Output ('ENGINE=' + $curEngine)
    Write-Output ('LEAFBASE_EXISTS=' + $leafBaseExists)
    if ($failures.Count -gt 0) {
        foreach ($f in $failures) { Write-Output ('SELFTEST FAIL: ' + $f) }
        Write-Output 'RESULT: SELFTEST FAIL'
        exit $EXIT_SELFTEST
    }
    Write-Output 'RESULT: SELFTEST PASS'
    exit $EXIT_PASS
}

# ---------------------------------------------------------------------------
# Scan
# ---------------------------------------------------------------------------
$rootPath = $Root
if (-not [System.IO.Path]::IsPathRooted($rootPath)) {
    $rootPath = Join-Path (Get-Location).Path $Root
}
if (-not (Test-Path -LiteralPath $rootPath)) {
    Write-Output ('ERROR: root not found: ' + $rootPath)
    exit $EXIT_EMPTY
}

$all = @(Get-ChildItem -LiteralPath $rootPath -Filter '*.ps1' -File -Recurse:$Recurse -ErrorAction SilentlyContinue)
$files = @()
foreach ($f in $all) {
    $skip = $false
    foreach ($seg in $ExcludeDir) {
        if ($f.FullName.ToLowerInvariant().Contains(($BS + $seg + $BS).ToLowerInvariant())) { $skip = $true; break }
    }
    if (-not $skip) { $files += $f }
}
$files = @($files | Sort-Object FullName)

if ($files.Count -eq 0) {
    Write-Output ('FAIL-CLOSED: no .ps1 enumerated under ' + $rootPath + ' - refusing to report green')
    exit $EXIT_EMPTY
}

Write-Output ('ROOT=' + $rootPath)
Write-Output ('ENGINE=' + $PSVersionTable.PSVersion.ToString())
Write-Output ''

$cmdMissing = @()
$cmdUnresolved = @()
$paramMissing = @()
$skippedDep = @()
$analysed = 0
$totalCmdRefs = 0
$dynFiles = 0

foreach ($file in $files) {
    $rel = $file.FullName.Substring($rootPath.Length).TrimStart([char]92)
    $text = Get-DecodedText -Bytes ([System.IO.File]::ReadAllBytes($file.FullName))
    $parsed = Get-ParsedAst -Text $text

    if ($parsed.ErrorCount -gt 0) {
        $skippedDep += ($rel + ' (' + $parsed.FirstError + ')')
        Write-Output ('{0,-14} {1}' -f 'UNANALYSABLE', $rel)
        continue
    }

    $dyn = Test-DynamicDefinitionInjection -Text $text
    if ($dyn) { $dynFiles++ }

    $findings = @(Get-CommandFindings -Ast $parsed.Ast -DynamicDefines $dyn)
    $analysed++

    $cmdNodes = @($parsed.Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))
    $totalCmdRefs += $cmdNodes.Count

    $tag = 'OK'
    if ((Get-FindingCount -Findings $findings) -gt 0) { $tag = 'FINDINGS' }
    if ($dyn) { $tag = $tag + '/DYN' }

    Write-Output ('{0,-14} refs={1,-4} findings={2,-3} {3}' -f $tag, $cmdNodes.Count, (Get-FindingCount -Findings $findings), $rel)
    foreach ($x in $findings) {
        $shown = $x.Name
        if ($x.Param -ne '') { $shown = $x.Name + ' -' + $x.Param }
        Write-Output ('               L{0,-6} {1,-14} {2}' -f $x.Line, $x.Kind, $shown)
        if ($x.Kind -eq 'CMD_MISSING') { $cmdMissing += ($rel + ':' + $x.Line + ' ' + $x.Name) }
        elseif ($x.Kind -eq 'CMD_UNRESOLVED') { $cmdUnresolved += ($rel + ':' + $x.Line + ' ' + $x.Name) }
        else { $paramMissing += ($rel + ':' + $x.Line + ' ' + $x.Name + ' -' + $x.Param) }
    }
}

Write-Output ''
Write-Output ('FILES={0} ANALYSED={1} UNANALYSABLE={2} DYN_FILES={3} CMDREF={4} CMD_MISSING={5} CMD_UNRESOLVED={6} PARAM_MISSING={7}' -f `
    $files.Count, $analysed, $skippedDep.Count, $dynFiles, $totalCmdRefs, (Get-FindingCount -Findings $cmdMissing), (Get-FindingCount -Findings $cmdUnresolved), (Get-FindingCount -Findings $paramMissing))

$failed = $false

if ($skippedDep.Count -gt 0) {
    Write-Output ('FAIL: {0} file(s) cannot be parsed, so their commands cannot be checked' -f (Get-FindingCount -Findings $skippedDep))
    foreach ($s in $skippedDep) { Write-Output ('  - ' + $s) }
    $failed = $true
}

if ((Get-FindingCount -Findings $cmdMissing) -gt 0) {
    Write-Output ('FAIL: {0} command name(s) do not resolve in {1}' -f (Get-FindingCount -Findings $cmdMissing), $PSVersionTable.PSVersion)
    $shown = 0
    foreach ($m in $cmdMissing) {
        if ($shown -ge $MaxReport) { break }
        Write-Output ('  - ' + $m)
        $shown++
    }
    $failed = $true
}

if ((Get-FindingCount -Findings $paramMissing) -gt 0) {
    Write-Output ('FAIL: {0} parameter(s) do not exist on the resolved command in {1}' -f (Get-FindingCount -Findings $paramMissing), $PSVersionTable.PSVersion)
    $shown = 0
    foreach ($m in $paramMissing) {
        if ($shown -ge $MaxReport) { break }
        Write-Output ('  - ' + $m)
        $shown++
    }
    $failed = $true
}

if ((Get-FindingCount -Findings $cmdUnresolved) -gt 0) {
    $level = 'WARN'
    if ($StrictCommands) { $level = 'FAIL' }
    Write-Output ('{0}: {1} unresolved name(s) in file(s) that inject definitions at runtime' -f $level, (Get-FindingCount -Findings $cmdUnresolved))
    $shown = 0
    foreach ($m in $cmdUnresolved) {
        if ($shown -ge $MaxReport) { break }
        Write-Output ('  - ' + $m)
        $shown++
    }
    if ($StrictCommands) { $failed = $true }
    else { Write-Output '  (pass -StrictCommands to make these fatal)' }
}

Write-Output ''
Write-Output 'SCOPE LIMITS: dynamic invocations and & $var skipped; path-invoked *.ps1 skipped;'
Write-Output '             locally defined functions skipped; native applications are name-only;'
Write-Output '             splatting, positional arguments and provider dynamic parameters are not analysed.'

if ($failed) {
    Write-Output 'RESULT: FAIL'
    if ($skippedDep.Count -gt 0 -and $cmdMissing.Count -eq 0 -and $paramMissing.Count -eq 0) {
        exit $EXIT_UNANALYSABLE
    }
    exit $EXIT_FINDINGS
}

Write-Output 'RESULT: PASS'
exit $EXIT_PASS
