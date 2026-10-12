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
    [switch]$DryRunRetry,
    # Accept a retry that absorbed a failure NOT on TestScripts/known-flaky.txt (default: refuse).
    [switch]$AllowNewInstability
)
# ── Shell 构建守卫（2026-10-08）────────────────────────────────────────────
# WinUI 产品工程不在 suites 里（它没有测试），但曾因**引用边界**被破坏而链全绿、Shell 却构建不了
# （CS0012：Automation 类型进入 Shell 编译面）。⇒ 单独构建它，失败即拒绝生成清单。
$shellProject = "Source/PuddingDesktop/PuddingDesktop.csproj"
Write-Host ""
Write-Host "=== step 0/3: build the WinUI shell (product project, not a test suite) ==="
$shellBuild = & dotnet build $shellProject --nologo 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host $shellBuild
    Write-Host "SHELL-BUILD-FAILED: the product shell does not build => refusing to emit a manifest"
    exit 1
}
Write-Host "    shell build: OK"

# ── 发布产物守卫（2026-10-08）──────────────────────────────────────────────
# 链的"事实来源"包含 Desktop 发布目录（DeliveryFactCollector 会在其不存在时加入一条事实，
# 从而拒绝产出清单）。该目录**不在链的步骤内** ⇒ 缺失时**自行发布**（存在时零成本）。
$repoRootForPublish = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishPath = Join-Path $repoRootForPublish $PublishDirectory
if (-not (Test-Path $publishPath)) {
    Write-Host ""
    Write-Host "=== step 0b/4: publish the Desktop preview (missing: $PublishDirectory) ==="
    & dotnet publish (Join-Path $repoRootForPublish 'Source\PuddingDesktop\PuddingDesktop.csproj') -c Release -o $publishPath --nologo | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $publishPath)) {
        Write-Host "PUBLISH-FAILED: the Desktop preview could not be produced => refusing to emit a manifest"
        exit 1
    }
}
Write-Host "    publish dir: OK ($PublishDirectory)"

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
            # IMPORTANT: a native tool writing to stderr (xUnit prints [FAIL]) becomes a TERMINATING
            # error under ErrorActionPreference=Stop, which aborted this script at the FIRST failing
            # suite: later suites never ran, the retry block was unreachable, and no manifest decision
            # was ever made. Gate on exit codes instead and keep going.
            $ErrorActionPreference = "Continue"
            & dotnet @testArguments
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "$name has failing tests (recorded honestly; the manifest will refuse to exist)"
            }
        }

        # A suite that never produced a TRX (build failure, bad path, crash) would simply be ABSENT
        # from the manifest: the remaining green suites would make it look complete. Refuse instead.
        $missingTrx = @()
        foreach ($suite in $Suites) {
            $name = [System.IO.Path]::GetFileNameWithoutExtension($suite)
            $trx = Join-Path $ResultsDirectory "$name.trx"
            $retried = Join-Path $ResultsDirectory "$name-retried.trx"
            if (-not (Test-Path $trx) -and -not (Test-Path $retried)) {
                $missingTrx += $name
            }
        }

        if ($missingTrx.Count -gt 0) {
            Write-Warning ("REFUSING to build a manifest: these suites produced no TRX: {0}" -f ($missingTrx -join ", "))
            exit 2
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

                    # Capture HOW BAD the attempt before the retry was: renaming alone only says
                    # "a retry happened", a reader still cannot tell 1 flaky test from 40 failures.
                    $trxText = Get-Content $trx -Raw -Encoding UTF8
                    $firstAttemptFailures = ([regex]::Match($trxText, 'failed="(\d+)"')).Groups[1].Value

                    # ...and WHICH tests failed (bounded): a suite name + a count still cannot point at
                    # the flaky test. Attribute order in TRX varies, so match the element then inspect it.
                    $failedNames = @()
                    foreach ($element in [regex]::Matches($trxText, '<UnitTestResult\b[^>]*>')) {
                        if ($element.Value -notmatch 'outcome="Failed"') { continue }
                        $nameMatch = [regex]::Match($element.Value, 'testName="([^"]+)"')
                        if ($nameMatch.Success) { $failedNames += $nameMatch.Groups[1].Value }
                    }
                    $nameSummary = if ($failedNames.Count -eq 0) {
                        "no test names in the TRX"
                    }
                    elseif ($failedNames.Count -le 5) {
                        ($failedNames -join ", ")
                    }
                    else {
                        (($failedNames | Select-Object -First 5) -join ", ") + (" (+" + ($failedNames.Count - 5) + " more)")
                    }
                    Write-Host ("--- $name : first attempt failed {0} test(s): {1}" -f $firstAttemptFailures, $nameSummary)

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

                        # A retry that succeeds is NOT automatically benign: compare the failing test names with
                        # the known-flaky registry. A failure that is NOT on the list is NEW instability and must
                        # be investigated -- otherwise every future flake would be silently absorbed by the retry.
                        $registryPath = Join-Path $PSScriptRoot "known-flaky.txt"
                        $knownNames = @()
                        if (Test-Path $registryPath) {
                            $knownNames = Get-Content $registryPath -Encoding UTF8 |
                                Where-Object { $_ -and -not $_.TrimStart().StartsWith("#") } |
                                ForEach-Object { $_.Trim() }
                        }

                        $unknown = @()
                        foreach ($failedName in $failedNames) {
                            if ($knownNames -notcontains $failedName) { $unknown += $failedName }
                        }

                        $verdict = if ($unknown.Count -eq 0 -and $failedNames.Count -gt 0) {
                            "all of them are on the known-flaky registry"
                        }
                        elseif ($unknown.Count -gt 0) {
                            "NEW INSTABILITY (not on TestScripts/known-flaky.txt): " + ($unknown -join ", ")
                        }
                        else {
                            "no test names in the TRX; cannot tell whether this was a known flake"
                        }

                        # "It flakes" is not actionable; "it has flaked N times since <date>" is. Every absorbed
                        # failure is appended to an append-only log owned by this delivery chain (so the owning
                        # subsystem's test code is NOT touched), and the running count goes into the note.
                        $observationsPath = Join-Path $PSScriptRoot "flake-observations.txt"
                        $statsScript = Join-Path $PSScriptRoot "flake-stats.ps1"
                        $today = (Get-Date).ToString("yyyy-MM-dd")
                        foreach ($failedName in $failedNames) {
                            Add-Content -Path $observationsPath -Encoding UTF8 -Value ("$today $name $failedName")
                        }

                        $historyNote = ""
                        if ((Test-Path $statsScript) -and (Test-Path $observationsPath)) {
                            foreach ($failedName in $failedNames) {
                                $counted = @(& $statsScript -Path $observationsPath -TestName $failedName 2>$null |
                                    Where-Object { $_ -match "^TOTAL`t" })
                                if ($counted.Count -gt 0) {
                                    $total = ($counted[0] -split "`t")[1]
                                    $historyNote += " observed $total time(s) in total (TestScripts/flake-observations.txt);"
                                }
                            }
                        }

                        # ...and record what the first attempt looked like, for the collector/report.
                        $notesPath = Join-Path $ResultsDirectory "retry-notes.txt"
                        Add-Content -Path $notesPath -Encoding UTF8 -Value (
                            "$name : first attempt had $firstAttemptFailures failing test(s) [$nameSummary]; $verdict;$historyNote the result below is from the retry")
                        Write-Host "--- $name green on retry (manifest will show it as retried; $verdict)"
                        break
                    }
                }
            }
        }
    }
    else {
        Write-Host "--- tests skipped (-SkipTests): using existing TRX in $ResultsDirectory"
    }

    # A retry must not silently absorb an UNKNOWN failure. The retry path records, per suite, whether the
    # first-attempt failures were all on the known-flaky registry; if any was not, the delivery stops here
    # unless the operator explicitly allows it (-AllowNewInstability). Green-after-retry is NOT evidence
    # that the failure was benign.
    $notesPath = Join-Path $ResultsDirectory "retry-notes.txt"
    if ((Test-Path $notesPath)) {
        $newInstability = @(Get-Content $notesPath -Encoding UTF8 |
            Where-Object { $_ -match "NEW INSTABILITY" })
        if ($newInstability.Count -gt 0) {
            if ($AllowNewInstability) {
                Write-Warning ("absorbing {0} retried suite(s) with NEW instability (-AllowNewInstability was given):" -f $newInstability.Count)
                $newInstability | ForEach-Object { Write-Warning ("  " + $_) }
            }
            else {
                Write-Warning "REFUSING to build a manifest: a retry absorbed a failure that is NOT on TestScripts/known-flaky.txt."
                $newInstability | ForEach-Object { Write-Warning ("  " + $_) }
                Write-Warning "Investigate it, add it to the registry with evidence if it is a known timing flake, or pass -AllowNewInstability to accept it explicitly."
                exit 2
            }
        }
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
