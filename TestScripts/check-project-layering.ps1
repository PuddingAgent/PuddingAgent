<#
.SYNOPSIS
    Project layering / dependency gate for the PuddingAgent repository.

.DESCRIPTION
    Implements two machine-checkable items from the Harness standard appendix A
    (Docs/10_conventions, file dated 2026-09-24, section 11) and the measured
    dependency graph (Docs/10_conventions, file dated 2026-10-08):

      C-1  The project dependency graph must be ACYCLIC (all *.csproj in the repo).
      C-4  No production project may reference a test project (*Tests).

    Reported but NOT enforced (scope undecided, see D-6 in the mapping document):
      edges from production projects into fixture / probe / benchmark / archive projects.

    EXIT CODES
      0 = PASS
      1 = FAIL   (a violation was found)
      3 = INSTRUMENT_FAILURE (enumeration produced 0 projects: treat as broken, not green)
      4 = SELFTEST_FAIL (the checker failed to detect an injected violation)

    NOTE - keep this file ASCII-ONLY.
      Windows PowerShell 5.1 decodes BOM-less script files as ANSI; a UTF-8 file that
      contains non-ASCII text therefore fails to PARSE on this machine (measured
      2026-10-08: 4 of 18 scripts under TestScripts are unparseable for this reason,
      including test-pudding-suite-gates.ps1). An ASCII-only script is immune.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File TestScripts\check-project-layering.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File TestScripts\check-project-layering.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- scope helpers
$script:ExcludeRegex  = '\\(bin|obj|node_modules|temp|\.vs|\.git)\\'
$script:ExternalRegex = '\\external\\'   # vendored tree: INCLUDED in the graph, reported separately (D-5)

# Projects whose production/test nature is UNDECIDED (D-6). Reported, never enforced.
$script:UndecidedFixtureProjects = @(
    'Pudding.Rpc.IpcProbe'
    'PuddingRecovery.SchemaProbe'
    'PuddingRetrievalEvalProbe'
    'PuddingMemoryEngineBenchmarks'
    'PerformanceTest'
    'PuddingBrowser.WebView2.Smoke'
    'PuddingBrowser.TestSite'
    'PuddingWsTest'
    'MiniProject'
    'Mcp.Cli'
    'PuddingDesktop.WpfArchive'
)

function Test-TestProjectName([string]$Name) {
    return ($Name -match '(?i)Tests$')
}

function Get-ProjectGraph([string]$RepoRoot) {
    $graph = @{}
    $files = Get-ChildItem -LiteralPath $RepoRoot -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch $script:ExcludeRegex }

    foreach ($f in $files) {
        $name = [IO.Path]::GetFileNameWithoutExtension($f.Name)
        $text = [IO.File]::ReadAllText($f.FullName)
        $deps = New-Object System.Collections.ArrayList
        foreach ($m in [regex]::Matches($text, '<ProjectReference\s+[^>]*Include="([^"]+)"')) {
            $dep = [IO.Path]::GetFileNameWithoutExtension($m.Groups[1].Value.Replace('\', '/'))
            if (-not $deps.Contains($dep)) { [void]$deps.Add($dep) }
        }
        $graph[$name] = @{ Path = $f.FullName; Refs = @($deps) }
    }
    return $graph
}

function Get-DependencyEdges([hashtable]$Graph) {
    $edges = New-Object System.Collections.ArrayList
    foreach ($n in $Graph.Keys) {
        foreach ($d in @($Graph[$n].Refs)) {
            if ($Graph.ContainsKey($d)) { [void]$edges.Add((New-Object psobject -Property @{ From = $n; To = $d })) }
        }
    }
    return , $edges
}

# ------------------------------------------------------------ C-1: acyclic check
function Find-Cycle([hashtable]$Graph) {
    $found = @{ Cycle = $null }
    $state = @{}                      # 1 = on stack, 2 = done
    $stack = New-Object System.Collections.Stack

    function Walk([string]$n) {
        if ($found.Cycle) { return }
        $state[$n] = 1
        [void]$stack.Push($n)
        foreach ($d in @($Graph[$n].Refs)) {
            if (-not $Graph.ContainsKey($d)) { continue }
            if ($state.ContainsKey($d) -and $state[$d] -eq 1) {
                $path = @($stack.ToArray())          # top-first
                [array]::Reverse($path)              # bottom-first
                $idx = [array]::IndexOf($path, $d)
                $found.Cycle = @($path[$idx..($path.Count - 1)] + $d)
                return
            }
            if (-not $state.ContainsKey($d)) { Walk $d }
        }
        [void]$stack.Pop()
        $state[$n] = 2
    }

    foreach ($n in @($Graph.Keys)) {
        if (-not $state.ContainsKey($n)) { Walk $n }
    }
    return $found.Cycle
}

# --------------------------------------------- C-4: production -> test references
function Get-TestReferenceViolations([hashtable]$Graph) {
    $violations = New-Object System.Collections.ArrayList
    foreach ($n in $Graph.Keys) {
        if (Test-TestProjectName $n) { continue }
        foreach ($d in @($Graph[$n].Refs)) {
            if ((Test-TestProjectName $d) -and $Graph.ContainsKey($d)) {
                [void]$violations.Add(($n + ' -> ' + $d))
            }
        }
    }
    return , $violations
}

function Get-UndecidedFixtureEdges([hashtable]$Graph) {
    $hits = New-Object System.Collections.ArrayList
    foreach ($n in $Graph.Keys) {
        foreach ($d in @($Graph[$n].Refs)) {
            if ($script:UndecidedFixtureProjects -contains $d) { [void]$hits.Add(($n + ' -> ' + $d)) }
        }
    }
    return , $hits
}

# ------------------------------------------------------------------ real graph
if ([string]::IsNullOrWhiteSpace($Root)) {
    if ($PSScriptRoot) { $Root = Split-Path -Parent $PSScriptRoot } else { $Root = (Get-Location).Path }
}
$Root = (Resolve-Path -LiteralPath $Root).Path

Write-Host ("ROOT = " + $Root)
$graph = Get-ProjectGraph -RepoRoot $Root
$edges = Get-DependencyEdges -Graph $graph
Write-Host ("PROJECT_COUNT = " + $graph.Count)
Write-Host ("EDGE_COUNT    = " + @($edges).Count)
$external = @($graph.Keys | Where-Object { $graph[$_].Path -match $script:ExternalRegex } | Sort-Object)
Write-Host ("EXTERNAL_TREE_PROJECTS = " + $external.Count + " (vendored; still checked by C-1/C-4; governance undecided: D-5)")
foreach ($x in $external) { Write-Host ("  ext: " + $x) }

if ($graph.Count -eq 0) {
    Write-Host 'INSTRUMENT_FAILURE: no *.csproj enumerated - refusing to report green' -ForegroundColor Red
    exit 3
}
if ($graph.Count -lt 20) {
    Write-Host ('INSTRUMENT_WARNING: only ' + $graph.Count + ' projects found (expected ~81); check the scope filter') -ForegroundColor Yellow
}

# ------------------------------------------------------------------- self test
if ($SelfTest) {
    $probe = @{}
    foreach ($k in $graph.Keys) { $probe[$k] = @{ Refs = @($graph[$k].Refs) } }

    # mutation 1: inject a 2-node cycle
    $probe['__ZZ_CycleA'] = @{ Refs = @('__ZZ_CycleB') }
    $probe['__ZZ_CycleB'] = @{ Refs = @('__ZZ_CycleA') }
    $cycle = Find-Cycle -Graph $probe
    if (-not $cycle) {
        Write-Host 'SELFTEST_FAIL: injected cycle was NOT detected' -ForegroundColor Red
        exit 4
    }
    Write-Host ('SELFTEST ok: injected cycle detected -> ' + ($cycle -join ' -> ')) -ForegroundColor Green

    # mutation 2: inject a production -> test reference
    $probe['__ZZ_Prod'] = @{ Refs = @('__ZZ_Prod.Tests') }
    $probe['__ZZ_Prod.Tests'] = @{ Refs = @() }
    $mut = Get-TestReferenceViolations -Graph $probe
    if (@($mut | Where-Object { $_ -like '__ZZ_Prod*' }).Count -eq 0) {
        Write-Host 'SELFTEST_FAIL: injected production->test reference was NOT detected' -ForegroundColor Red
        exit 4
    }
    Write-Host 'SELFTEST ok: injected production->test reference detected' -ForegroundColor Green
    Write-Host 'SELFTEST: PASS' -ForegroundColor Green
}

# ------------------------------------------------------------------- C-1 check
$cycle = Find-Cycle -Graph $graph
if ($cycle) {
    Write-Host ('FAIL C-1: dependency cycle found -> ' + ($cycle -join ' -> ')) -ForegroundColor Red
    exit 1
}
Write-Host 'PASS C-1: dependency graph is acyclic' -ForegroundColor Green

# ------------------------------------------------------------------- C-4 check
$violations = Get-TestReferenceViolations -Graph $graph
if (@($violations).Count -gt 0) {
    Write-Host 'FAIL C-4: production project(s) reference test project(s):' -ForegroundColor Red
    foreach ($v in $violations) { Write-Host ('  ' + $v) -ForegroundColor Red }
    exit 1
}
Write-Host 'PASS C-4: no production project references a test project' -ForegroundColor Green

# --------------------------------------------------- informational (not enforced)
$fixture = Get-UndecidedFixtureEdges -Graph $graph
if (@($fixture).Count -gt 0) {
    Write-Host ('INFO: ' + @($fixture).Count + ' edge(s) into undecided fixture/probe projects (D-6, NOT enforced):') -ForegroundColor Yellow
    foreach ($d in $fixture) { Write-Host ('  ' + $d) -ForegroundColor DarkYellow }
}

Write-Host 'RESULT: PASS' -ForegroundColor Green
exit 0
