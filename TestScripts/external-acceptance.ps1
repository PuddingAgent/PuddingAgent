<#
.SYNOPSIS
    External acceptance scaffolding (B7, outside the product): build the smoke checklist template and
    compute the external-lifecycle-verified verdict from the observations file.

.DESCRIPTION
    This script is for the party OUTSIDE the product. Inside the product nobody can judge
    startup / restart / crash-recovery / exit-reclamation (see Agents.md "verification boundary"),
    so the internal side delivers `ready-for-external-deploy` and this script closes the loop:

      1. -Template writes a facts skeleton (every checklist item = not-run) plus the observation table;
      2. the operator performs each observation on a REAL device and edits the facts file;
      3. -Conclude feeds manifest + facts into the CLI, which either allows
         `external-lifecycle-verified` (exit 0) or refuses with concrete reasons (exit 2).

    The checklist ids are the single source of truth in Source/Pudding.DesktopService/BrowserSmokePlan.cs;
    this script only mirrors them (ASCII-only on purpose: Windows PowerShell 5.1 reads a BOM-less .ps1
    as ANSI, so non-ASCII literals here would be a parse error).

.EXAMPLE
    # 1) facts skeleton (all items not-run) + the observation table:
    powershell -File TestScripts/external-acceptance.ps1 -Template -Facts temp/test-out/facts.json

    # 2) after the operator fills the facts file, compute the verdict:
    #    exit 0 = external-lifecycle-verified allowed; exit 2 = refused with reasons (e.g. any not-run item)
    powershell -File TestScripts/external-acceptance.ps1 -SkipTemplate -Conclude -Facts temp/test-out/facts.json

    # NOTE: -Template / -Conclude / -SkipTemplate are SWITCHES. Passing a path to a switch
    #       (e.g. `-Template some\path.json`) silently binds that path to a POSITIONAL parameter
    #       and looks like it worked. The observations file is -Facts. (Learned the hard way.)
#>
[CmdletBinding()]
param(
    [string]$Manifest = "temp/test-out/delivery-manifest.json",
    [string]$Facts = "temp/test-out/external-facts.json",
    [string]$Record = "temp/test-out/external-acceptance-record.md",
    [switch]$Template,
    [switch]$Conclude,
    [switch]$SkipTemplate,
    # Accepting retried suite results is the ACCEPTOR's decision: it is never assumed.
    [switch]$AcceptRetries
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

# Mirror of BrowserSmokePlan.Checks (id + gate). The expectations live in
# Docs/14_reports/2026-10-09-<external smoke checklist>.md and in the code.
$checks = @(
    @{ Id = "S-01"; Gate = "G1"; Area = "control-authority" },
    @{ Id = "S-02"; Gate = "G2"; Area = "page-grant" },
    @{ Id = "S-03"; Gate = "G3"; Area = "receipts" },
    @{ Id = "S-04"; Gate = "G4"; Area = "element-refs" },
    @{ Id = "S-05"; Gate = "G5"; Area = "reading-budget" },
    @{ Id = "S-06"; Gate = "G6"; Area = "article-paging" },
    @{ Id = "S-07"; Gate = "G7"; Area = "locators-input" },
    @{ Id = "S-08"; Gate = "G8"; Area = "scrolling" },
    @{ Id = "S-09"; Gate = "G9"; Area = "screenshot-coordinates" },
    @{ Id = "S-10"; Gate = "G10"; Area = "delegation" },
    @{ Id = "S-11"; Gate = "G11"; Area = "lifecycle" },
    @{ Id = "S-12"; Gate = "G7"; Area = "file-picker-input-path" },
    @{ Id = "S-13"; Gate = "G3"; Area = "page-events" }
)

$lifecycleSteps = @("startup", "restart", "crash-recovery", "exit-reclamation")

$cliProject = "Source/Pudding.DeliveryManifest.Cli/Pudding.DeliveryManifest.Cli.csproj"

function Write-Template {
    param([string]$Path)

    $observedBuildId = "<build id of the delivered manifest>"
    if (Test-Path $Manifest) {
        try {
            $parsed = Get-Content $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($parsed.buildId) { $observedBuildId = $parsed.buildId }
        }
        catch {
            Write-Warning "cannot read $Manifest to prefill observedBuildId; leaving a placeholder"
        }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("{")
    $lines.Add("  ""observedBuildId"": ""$observedBuildId"",")
    $lines.Add("  ""smoke"": [")

    for ($index = 0; $index -lt $checks.Count; $index++) {
        $check = $checks[$index]
        $comma = if ($index -lt $checks.Count - 1) { "," } else { "" }
        $lines.Add("    { ""checkId"": ""$($check.Id)"", ""outcome"": ""not-run"", ""evidence"": null }$comma")
    }

    $lines.Add("  ],")
    $lines.Add("  ""lifecycle"": [")

    for ($index = 0; $index -lt $lifecycleSteps.Count; $index++) {
        $step = $lifecycleSteps[$index]
        $comma = if ($index -lt $lifecycleSteps.Count - 1) { "," } else { "" }
        $lines.Add("    { ""step"": ""$step"", ""passed"": false, ""evidence"": null }$comma")
    }

    $lines.Add("  ]")
    $lines.Add("}")

    $directory = Split-Path -Parent $Path
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    Set-Content -Path $Path -Value ($lines -join "`n") -Encoding UTF8

    Write-Host "=== facts template written: $Path"
    Write-Host "    allowed outcomes: passed | failed | not-run (anything else is refused)"
    Write-Host ""
    Write-Host "| item | gate | area |"
    Write-Host "|---|---|---|"
    foreach ($check in $checks) {
        Write-Host "| $($check.Id) | $($check.Gate) | $($check.Area) |"
    }
    Write-Host ""
    Write-Host "Expectations per item: Docs/14_reports/2026-10-09-<external smoke checklist>.md"
    Write-Host "Lifecycle steps: $($lifecycleSteps -join ', ')"
}

if (-not $SkipTemplate) {
    if ($Template -or -not (Test-Path $Facts)) {
        Write-Template -Path $Facts
    }
    else {
        Write-Host "=== facts file already exists, keeping it: $Facts"
    }
}

if ($Conclude -or -not $Template) {
    if (-not (Test-Path $Manifest)) {
        Write-Error "delivery manifest not found: $Manifest (run TestScripts/delivery-manifest.ps1 first)"
        exit 1
    }

    if (-not (Test-Path $Facts)) {
        Write-Error "facts file not found: $Facts (run with -Template first, then fill it in)"
        exit 1
    }

    Write-Host "=== computing the external verdict"
    $ErrorActionPreference = "Continue"

    # stderr carries the concrete refusal reasons; report them through the exit code.
    $cliArguments = @(
        "run", "--project", $cliProject, "--no-restore", "--",
        "--conclude", (Join-Path $repositoryRoot $Facts),
        "--manifest", (Join-Path $repositoryRoot $Manifest),
        "--record", (Join-Path $repositoryRoot $Record)
    )
    if ($AcceptRetries) { $cliArguments += "--accept-retries" }

    # Surface retried suites BEFORE concluding: a "green" manifest that came from a retry is not
    # the same claim as one that passed on the first attempt.
    if (Test-Path $Manifest) {
        try {
            $manifestJson = Get-Content $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
            $retryNotes = @($manifestJson.retryNotes)
            if ($retryNotes.Count -gt 0) {
                Write-Host "--- delivered manifest reports retried suite(s):"
                foreach ($note in $retryNotes) { Write-Host "      $note" }
                if (-not $AcceptRetries) {
                    Write-Host "      (not accepted yet: pass -AcceptRetries to acknowledge, otherwise the verdict is refused)"
                }
            }
        }
        catch {
            Write-Warning "could not read retryNotes from the manifest; the CLI will still enforce the rule"
        }
    }

    & dotnet @cliArguments

    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        Write-Host "=== ALLOWED: external-lifecycle-verified (record: $Record)"
    }
    else {
        Write-Warning "NOT allowed: external-lifecycle-verified (exit=$exitCode). Fix the reasons above and re-run."
    }

    exit $exitCode
}

exit 0
