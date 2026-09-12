#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'invoke-pudding-desktop-deployment.ps1'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
# Load only the pure predicate: tests must never execute maintenance operations.
$predicate = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-CoreQuiescent' }, $false)
if (!$predicate) { throw 'Quiescence predicate not found' }
. ([scriptblock]::Create($predicate.Extent.Text))
$cases = @(
    @{ State='Stopped'; Id=8160; Alive=$false; Expected=$true },
    @{ State='Stopped'; Id=8160; Alive=$true; Expected=$false },
    @{ State='Starting'; Id=8160; Alive=$false; Expected=$false },
    @{ State='Ready'; Id=8160; Alive=$true; Expected=$false },
    @{ State='Idle'; Id=$null; Alive=$false; Expected=$true },
    @{ State='Faulted'; Id=8160; Alive=$false; Expected=$false }
)
foreach ($case in $cases) {
    $actual = Test-CoreQuiescent -Snapshot ([pscustomobject]@{coreState=$case.State;coreProcessId=$case.Id}) -ProcessExists $case.Alive
    if ($actual -ne $case.Expected) { throw "Unexpected quiescence result: $($case.State), alive=$($case.Alive)" }
}
if (Test-CoreQuiescent -Snapshot $null -ProcessExists $false) { throw 'Missing snapshot must fail closed' }
[pscustomobject]@{ Passed=($cases.Count + 1); Failed=0 } | ConvertTo-Json
