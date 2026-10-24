#Requires -Version 5.1
<#
.SYNOPSIS
    Prepare the DeepSeek tokenizer asset used by PuddingTokenization.

.DESCRIPTION
    Downloads DeepSeek-V4.1-Flash tokenizer.json into the data root and verifies it
    against a pinned length + SHA-256 before use.

    Why a script and not a checked-in file: the asset is ~6.4 MB. Committing it would
    bloat every clone and every diff review. Pinning the hash instead gives the same
    guarantee (you cannot silently get a different vocabulary) without the weight.

    Why the data root and not the build output: the vocabulary is environment data,
    like the code index database. Build outputs get cleaned; the data root survives.

    Idempotent: if the target already matches the pinned hash, nothing is downloaded.

    IMPORTANT: this must be the V4.1-Flash vocabulary. V4-Pro/V4-Flash ship a
    DIFFERENT tokenizer.json (different ETag and byte length), and using the wrong one
    silently produces wrong token counts rather than an error. The hash below is the
    guard against exactly that mistake.

.EXAMPLE
    pwsh -NoProfile -File Tools\Tokenization\Prepare-TokenizerAssets.ps1

.EXAMPLE
    pwsh -NoProfile -File Tools\Tokenization\Prepare-TokenizerAssets.ps1 -Destination D:\tmp\tok -Force
#>
[CmdletBinding()]
param(
    [string]$Destination = 'D:\Data\models\tokenizers',

    # Install from an already-downloaded local file instead of the network.
    # Needed on hosts where PowerShell's TLS stack cannot reach HuggingFace even
    # though other HTTP clients can; the hash check is identical either way.
    [string]$SourcePath,

    # Re-download and overwrite even when the existing file already matches.
    [switch]$Force,

    # Register $Destination as the user-level PUDDING_TOKENIZER_ASSETS variable.
    # Without it the runtime cannot find the asset at all: LlmOptions looks for the
    # variable first, then for a tokenizers/ directory beside the application, and
    # the build output gets cleaned. Downloading into the data root and stopping
    # there leaves the file present but unreachable.
    [switch]$RegisterEnvironmentVariable
)

$ErrorActionPreference = 'Stop'

$assetName      = 'deepseek-v4.1-flash.tokenizer.json'
$url            = 'https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash/raw/main/tokenizer.json'
$expectedSha256 = 'C90DFA01249DB1BE4245780A052EDE752E1361C612AC6D08E2BDADA7D599476B'
$expectedBytes  = 6367257

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash
}

function Test-Asset([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne $expectedBytes) { return $false }
    return (Get-Sha256 $Path) -eq $expectedSha256
}

function Set-TokenizerAssetEnvironment {
    $resolved = (Resolve-Path -LiteralPath $Destination).Path
    [Environment]::SetEnvironmentVariable('PUDDING_TOKENIZER_ASSETS', $resolved, 'User')
    Write-Host "[env] PUDDING_TOKENIZER_ASSETS (user) = $resolved"
    Write-Host "      A running host keeps its old value; restart it to pick this up."
}

# A downloaded-but-unreachable asset is the worst outcome: everything looks fine
# while the runtime silently falls back to the generic encoding. So say it on every
# path, including the skip path where someone is most likely to assume it works.
function Write-ReachabilityNote {
    Write-Host ""
    Write-Host "[note] The runtime will NOT use this file until it can find it:"
    Write-Host "       set PUDDING_TOKENIZER_ASSETS=$((Resolve-Path -LiteralPath $Destination).Path)"
    Write-Host "       or re-run with -RegisterEnvironmentVariable."
}

if (-not (Test-Path -LiteralPath $Destination)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

$target = Join-Path $Destination $assetName

if (-not $Force -and (Test-Asset $target)) {
    Write-Host "[skip] asset already present and verified"
    Write-Host "       $target"
    Write-Host "       sha256=$expectedSha256"
    if ($RegisterEnvironmentVariable) { Set-TokenizerAssetEnvironment }
    else { Write-ReachabilityNote }
    return
}


# Staging path: the asset is moved into place only after both checks pass.
$temp = Join-Path $Destination ($assetName + '.download')

if ($SourcePath) {
    if (-not (Test-Path -LiteralPath $SourcePath)) {
        throw "SourcePath not found: $SourcePath"
    }
    Write-Host "[copy] $SourcePath"
    Copy-Item -LiteralPath $SourcePath -Destination $temp -Force
}
else {
    Write-Host "[fetch] $url"
    try {
        Invoke-WebRequest -Uri $url -OutFile $temp -TimeoutSec 300 -UseBasicParsing
    }
    catch {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
        throw "Download failed: $($_.Exception.Message). If this host cannot reach HuggingFace, re-run with -SourcePath pointing at a local copy."
    }
}

$actualBytes = (Get-Item -LiteralPath $temp).Length
if ($actualBytes -ne $expectedBytes) {
    Remove-Item -LiteralPath $temp -Force
    throw "Length mismatch: expected $expectedBytes, got $actualBytes. Refusing to install."
}

$actualSha = Get-Sha256 $temp
if ($actualSha -ne $expectedSha256) {
    Remove-Item -LiteralPath $temp -Force
    throw "SHA-256 mismatch: expected $expectedSha256, got $actualSha. Refusing to install."
}

# Only move into place after both checks pass, so a partial/corrupt download can
# never leave a file that the runtime would happily load.
Move-Item -LiteralPath $temp -Destination $target -Force

Write-Host "[ok] installed and verified"
Write-Host "     $target"
Write-Host "     bytes=$actualBytes"
Write-Host "     sha256=$actualSha"

if ($RegisterEnvironmentVariable) { Set-TokenizerAssetEnvironment }
else { Write-ReachabilityNote }
