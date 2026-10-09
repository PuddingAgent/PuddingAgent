<#
.SYNOPSIS
    Find the test suites that actually LOAD a given assembly (transitive project-reference graph), and
    optionally run them.

.DESCRIPTION
    Why this exists (2026-10-08): after changing the shared `Pudding.Contracts` assembly I verified only my
    own 7-suite delivery chain, and when I picked adjacent suites I matched the STRING "Pudding.Contracts"
    inside .csproj files -- which selected a suite that does not load the assembly at all (its bin has no
    Pudding.Contracts.dll), so its green result was not evidence.

    The correct question is "who reaches this assembly through <ProjectReference> (transitively)?". This
    script walks that graph from every test project, so the selection is reproducible instead of ad hoc.

    ASCII-only on purpose: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.

.PARAMETER Assembly
    Assembly/project name to look for, e.g. "Pudding.Contracts" (default).

.PARAMETER Configuration
    Build configuration used for the bin path when -Run is given (default Debug).

.PARAMETER Run
    Also run the selected projects with `dotnet test --no-restore` (otherwise selection only).

.EXAMPLE
    powershell -File TestScripts/check-dependent-suites.ps1
    powershell -File TestScripts/check-dependent-suites.ps1 -Assembly Pudding.Contracts -Run
#>
[CmdletBinding()]
param(
    [string]$Assembly = "Pudding.Contracts",
    [string]$Configuration = "Debug",
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

try {
    function Get-ProjectReferences {
        param([string]$ProjectPath)

        $text = Get-Content $ProjectPath -Raw -Encoding UTF8
        $matches = [regex]::Matches($text, '<ProjectReference\s+Include="([^"]+)"')
        $directory = Split-Path -Parent $ProjectPath
        foreach ($match in $matches) {
            $include = $match.Groups[1].Value.Replace('\', [System.IO.Path]::DirectorySeparatorChar)
            $resolved = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($directory, $include))
            if (Test-Path $resolved) { $resolved }
        }
    }

    # Transitive reachability: does this project's reference graph contain a project whose file name is
    # "<Assembly>.csproj"?
    function Test-ReachesAssembly {
        param([string]$ProjectPath, [string]$TargetAssembly)

        $targetFile = "$TargetAssembly.csproj"
        $visited = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        $queue = New-Object 'System.Collections.Generic.Queue[string]'
        $queue.Enqueue($ProjectPath)

        while ($queue.Count -gt 0) {
            $current = $queue.Dequeue()
            if (-not $visited.Add($current)) { continue }
            if ([System.IO.Path]::GetFileName($current) -ieq $targetFile) { return $true }

            foreach ($reference in (Get-ProjectReferences -ProjectPath $current)) {
                $queue.Enqueue($reference)
            }
        }

        return $false
    }

    $testProjects = Get-ChildItem -Recurse -Path Source, Tests -Filter "*Tests.csproj" -File |
        Sort-Object FullName

    if ($testProjects.Count -eq 0) {
        Write-Warning "no test projects found; the scan roots may be wrong"
        exit 1
    }

    $selected = @()
    $skipped = @()

    foreach ($project in $testProjects) {
        if (Test-ReachesAssembly -ProjectPath $project.FullName -TargetAssembly $Assembly) {
            $selected += $project
        }
        else {
            $skipped += $project
        }
    }

    Write-Host ("=== test projects scanned: {0}; reach '{1}': {2}; not reach: {3}" -f `
        $testProjects.Count, $Assembly, $selected.Count, $skipped.Count)
    foreach ($project in $selected) {
        Write-Host ("    REACHES  {0}" -f (Resolve-Path -Relative $project.FullName))
    }

    if (-not $Run) {
        Write-Host "=== selection only (-Run not given)"
        exit 0
    }

    $failures = @()
    foreach ($project in $selected) {
        Write-Host ("--- running {0}" -f (Resolve-Path -Relative $project.FullName))
        & dotnet test $project.FullName --nologo --no-restore
        if ($LASTEXITCODE -ne 0) {
            $failures += $project.FullName
        }
    }

    if ($failures.Count -gt 0) {
        Write-Warning ("suites failed: {0}" -f (($failures | ForEach-Object { Split-Path $_ -Leaf }) -join ", "))
        exit 1
    }

    Write-Host ("=== all {0} dependent suite(s) passed" -f $selected.Count)
    exit 0
}
finally {
    Pop-Location
}
