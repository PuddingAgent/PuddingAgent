[CmdletBinding()]
param(
    [int]$HoldSeconds = 30,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$smokeRoot = Join-Path $env:TEMP ('PuddingAgent\phase2a3-webview2-' + [guid]::NewGuid().ToString('N'))
$evidenceRoot = Join-Path $repositoryRoot '.tmp-test-out\phase2a3-webview2-smoke'
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null

function Remove-SmokeTemporaryTree {
    param([Parameter(Mandatory)][string]$Path)

    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'PuddingAgent'))
    $target = [IO.Path]::GetFullPath($Path)
    if (-not $target.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove smoke artifacts outside $allowedRoot"
    }

    foreach ($attempt in 1..20) {
        try {
            Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -ge 20) { break }
            Start-Sleep -Milliseconds 250
        }
    }

    # A WebView2 child may keep a profile file open briefly after the WPF host
    # exits. Cleanup must not turn a successful browser assertion into failure.
    Write-Warning "WebView2 smoke passed, but temporary artifacts are still locked: $target"
}

$siteOut = Join-Path $evidenceRoot 'testsite.stdout.log'
$siteErr = Join-Path $evidenceRoot 'testsite.stderr.log'
$smokeOut = Join-Path $evidenceRoot 'smoke.stdout.log'
$smokeErr = Join-Path $evidenceRoot 'smoke.stderr.log'
Remove-Item -LiteralPath $siteOut, $siteErr, $smokeOut, $smokeErr -ErrorAction SilentlyContinue

$siteDll = Join-Path $repositoryRoot 'Tests\PuddingBrowser.TestSite\bin\Debug\net10.0\PuddingBrowser.TestSite.dll'
$smokeExe = Join-Path $repositoryRoot 'Tests\PuddingBrowser.WebView2.Smoke\bin\Debug\net10.0-windows10.0.17763.0\PuddingBrowser.WebView2.Smoke.exe'
if (-not (Test-Path -LiteralPath $siteDll) -or -not (Test-Path -LiteralPath $smokeExe)) {
    throw 'Build PuddingBrowser.TestSite and PuddingBrowser.WebView2.Smoke before running this script.'
}

$site = $null
$smoke = $null
try {
    $site = Start-Process dotnet -ArgumentList @($siteDll, '--urls', 'http://127.0.0.1:0') `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $siteOut -RedirectStandardError $siteErr
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $siteUrl = $null
    while ([DateTime]::UtcNow -lt $deadline -and -not $siteUrl) {
        Start-Sleep -Milliseconds 100
        if (Test-Path -LiteralPath $siteOut) {
            $siteLog = Get-Content -LiteralPath $siteOut -Raw
            if ([string]::IsNullOrWhiteSpace($siteLog)) { continue }
            $match = [regex]::Match(
                $siteLog,
                'Now listening on:\s+(http://127\.0\.0\.1:\d+)')
            if ($match.Success) { $siteUrl = $match.Groups[1].Value + '/' }
        }
    }
    if (-not $siteUrl) { throw 'TestSite did not become ready.' }

    $smoke = Start-Process $smokeExe -ArgumentList @(
        '--url', $siteUrl,
        '--data-root', (Join-Path $smokeRoot 'data'),
        '--hold-seconds', ([Math]::Clamp($HoldSeconds, 0, 120)).ToString()) `
        -PassThru -RedirectStandardOutput $smokeOut -RedirectStandardError $smokeErr

    [pscustomobject]@{
        event = 'phase2a3-smoke-started'
        sitePid = $site.Id
        smokePid = $smoke.Id
        siteUrl = $siteUrl
        smokeRoot = $smokeRoot
        evidenceRoot = $evidenceRoot
        smokeExe = $smokeExe
    } | ConvertTo-Json -Compress

    $smoke.WaitForExit()
    if (Test-Path -LiteralPath $smokeOut) { Get-Content -LiteralPath $smokeOut }
    if (Test-Path -LiteralPath $smokeErr) { Get-Content -LiteralPath $smokeErr }
    if ($smoke.ExitCode -ne 0) { throw "WebView2 smoke exited with code $($smoke.ExitCode)." }
}
finally {
    if ($smoke -and -not $smoke.HasExited) { Stop-Process -Id $smoke.Id -ErrorAction SilentlyContinue }
    if ($site -and -not $site.HasExited) { Stop-Process -Id $site.Id -ErrorAction SilentlyContinue }
    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $smokeRoot)) {
        Remove-SmokeTemporaryTree -Path $smokeRoot
    }
}
