<#
.SYNOPSIS
    Delivery manifest collection (B7, internal side): run tests -> gather facts -> emit a SELF-CHECKED manifest.

.DESCRIPTION
    Follows the repo build/verification isolation rules:
      * build/test output only under temp\build, temp\test-out (never D:\data);
      * Desktop build/test/publish run serially;
      * this script only does what the INTERNAL side can do: it does NOT judge startup/restart/
        crash-recovery/exit-reclamation (only an external controller may), and hands those
        steps over in the manifest's requiredExternalSteps.

    Facts come from files on disk, never from hand-written numbers:
      artifacts  -> .exe/.dll in the publish directory (file names only)
      checks     -> *.trx in the results directory (produced by dotnet test)
      unverified -> markdown in the changelog directory ("what the inside cannot prove")

    Exit codes: 0 = manifest emitted; 2 = facts incomplete/unacceptable (NO manifest); 1 = usage/env error.

.EXAMPLE
    powershell -File TestScripts/delivery-manifest.ps1 -SkipTests -PublishDirectory Source\Pudding.DeliveryManifest.Cli\bin\Debug\net10.0
#>
[CmdletBinding()]
param(
    [string]$BuildId = "",
    [string]$Commit = "",
    [string]$PublishDirectory = "temp/build/desktop-preview",
    [string]$ResultsDirectory = "temp/test-out/trx",
    [string]$ChangelogDirectory = "Docs/00_changelog/2026Year/10",
    [string]$Out = "temp/test-out/delivery-manifest.json",
    # When set, also render the manifest into a durable markdown report at this path
    # (Front Matter included), e.g. Docs/14_reports/<date>-<topic>.md
    [string]$Report = "",
    [string[]]$Suites = @(
        "Source/PuddingBrowser.AutomationTests/PuddingBrowser.AutomationTests.csproj",
        "Tests/PuddingBrowser.AgentTools.Tests/PuddingBrowser.AgentTools.Tests.csproj",
        "Source/Pudding.ContractsTests/Pudding.ContractsTests.csproj",
        "Source/Pudding.DesktopServiceTests/Pudding.DesktopServiceTests.csproj",
        "Source/Pudding.DesktopSurface.BrowserTests/Pudding.DesktopSurface.BrowserTests.csproj",
        "Tests/PuddingDesktop.Tests/PuddingDesktop.Tests.csproj",
        "Tests/PuddingHost.Tests/PuddingHost.Tests.csproj"
    ),
    [switch]$SkipTests,
    # Re-run suites whose TRX shows failures, at most N times each (0 = off, the default).
    # Retries are NOT hidden: a retried suite's TRX is renamed with a "-retried" suffix, so the manifest
    # check name carries it and anyone reading the manifest can see that a retry happened.
    [int]$RetryFailedSuites = 0,
    # Print the retry decision from existing TRX files without running tests (verification aid).
    [switch]$DryRunRetry
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

try {
    if ([string]::IsNullOrWhiteSpace($BuildId)) {
        $BuildId = "local-" + (Get-Date -Format "yyyyMMdd-HHmmss")
    }

    if ([string]::IsNullOrWhiteSpace($Commit)) {
        $Commit = (git rev-parse --short HEAD).Trim()
    }

    Write-Host "=== delivery manifest: build=$BuildId commit=$Commit"

    if ($DryRunRetry) {
        # Decision-only mode (early exit): report which suites the retry phase WOULD re-run, reading the
        # TRX files already on disk and running nothing. This is how the retry decision gets verified --
        # no suite fails on demand, so a dry run plus fabricated TRX files is the honest way to test it.
        Write-Host "--- dry run: reading TRX failure counts only (no tests executed)"
        foreach ($suite in $Suites) {
            $name = [System.IO.Path]::GetFileNameWithoutExtension($suite)
            $trx = Join-Path (Join-Path $repositoryRoot $ResultsDirectory) "$name.trx"
            if (-not (Test-Path $trx)) {
                Write-Host "    $name : no TRX (would NOT retry)"
                continue
            }

            $failed = ([regex]::Match((Get-Content $trx -Raw -Encoding UTF8), 'failed="(\d+)"')).Groups[1].Value
            if (-not $failed) {
                Write-Host "    $name : TRX has no failed counter (would NOT retry)"
                continue
            }

            if ([int]$failed -eq 0) {
                Write-Host "    $name : failed=0 (would NOT retry)"
            }
            elseif ($RetryFailedSuites -gt 0) {
                Write-Host "    $name : failed=$failed => WOULD RETRY (max $RetryFailedSuites attempt(s))"
            }
            else {
                Write-Host "    $name : failed=$failed but -RetryFailedSuites is 0 => would NOT retry"
            }
        }

        exit 0
    }

    if (-not $SkipTests) {
        New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null

        # Only drop the TRX files this run will rewrite, so stale results cannot leak in.
        Get-ChildItem -Path $ResultsDirectory -Filter *.trx -ErrorAction SilentlyContinue | Remove-Item -Force

        foreach ($suite in $Suites) {
            $name = [System.IO.Path]::GetFileNameWithoutExtension($suite)
            Write-Host "--- test $name"

            # Keep going after a failing suite: the manifest needs "which suite failed how many",
            # not an early abort.
            $testArguments = @(
                "test", $suite, "--nologo", "--no-restore",
                "--logger", "trx;LogFileName=$name.trx",
                "--results-directory", $ResultsDirectory
            )
            & dotnet @testArguments
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "$name has failing tests (recorded honestly; the manifest will refuse to exist)"
            }
        }

        if ($RetryFailedSuites -gt 0) {
            Write-Host "--- retrying suites whose TRX shows failures (max $RetryFailedSuites attempt(s) each)"
            foreach ($suite in $Suites) {
                $name = [System.IO.Path]::GetFileNameWithoutExtension($suite)
                $trx = Join-Path $ResultsDirectory "$name.trx"
                if (-not (Test-Path $trx)) { continue }

                $failed = ([regex]::Match((Get-Content $trx -Raw -Encoding UTF8), 'failed="(\d+)"')).Groups[1].Value
                if (-not $failed -or [int]$failed -eq 0) { continue }

                for ($attempt = 1; $attempt -le $RetryFailedSuites; $attempt++) {
                    Write-Host "--- retry $attempt/$RetryFailedSuites for $name"
                    $retryArguments = @(
                        "test", $suite, "--nologo", "--no-restore",
                        "--logger", "trx;LogFileName=$name.trx",
                        "--results-directory", $ResultsDirectory
                    )
                    & dotnet @retryArguments
                    $failed = ([regex]::Match((Get-Content $trx -Raw -Encoding UTF8), 'failed="(\d+)"')).Groups[1].Value
                    if ([int]$failed -eq 0) {
                        # Keep the retry visible: the manifest check name will carry "(retried)".
                        Move-Item -Force $trx (Join-Path $ResultsDirectory "$name-retried.trx")
                        Write-Host "--- $name green on retry (manifest will show it as retried)"
                        break
                    }
                }
            }
        }
    }
    else {
        Write-Host "--- tests skipped (-SkipTests): using existing TRX in $ResultsDirectory"
    }

    Write-Host "=== gathering facts and building the manifest"
    # Native stderr must not be turned into a terminating error by $ErrorActionPreference: the CLI
    # prints its concrete refusal reasons there, and we report them by exit code.
    $ErrorActionPreference = "Continue"
    $cliArguments = @(
        "run", "--project", "Source/Pudding.DeliveryManifest.Cli/Pudding.DeliveryManifest.Cli.csproj",
        "--no-restore", "--",
        "--repo", $repositoryRoot,
        "--build", $BuildId,
        "--commit", $Commit,
        "--publish", (Join-Path $repositoryRoot $PublishDirectory),
        "--results", (Join-Path $repositoryRoot $ResultsDirectory),
        "--changelog", (Join-Path $repositoryRoot $ChangelogDirectory),
        "--out", (Join-Path $repositoryRoot $Out)
    )

    if (-not [string]::IsNullOrWhiteSpace($Report)) {
        # Keep this script ASCII-only: Windows PowerShell 5.1 reads a BOM-less UTF-8 .ps1 as ANSI,
        # so non-ASCII literals here turn into a parse error (learned the hard way).
        $cliArguments += @("--report", (Join-Path $repositoryRoot $Report))
    }

    & dotnet @cliArguments

    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        Write-Host "=== manifest emitted (stage: ready-for-external-deploy)"
    }
    else {
        Write-Warning "manifest NOT emitted (exit=$exitCode): facts incomplete or unacceptable; see reasons above."
    }

    exit $exitCode
}
finally {
    Pop-Location
}
