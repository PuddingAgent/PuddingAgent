# R2-01 step 2: migrate retrieval contracts from PuddingCodeIndex to PuddingRetrieval.Contracts.
# Default is DRY RUN. Pass -Apply to actually move files and rewrite namespaces.
#
# Why scripted: it is 30 file moves + namespace rewrite + hundreds of using rewrites.
# Doing it by hand invites partial migration that still compiles in some projects.

param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$repo = 'D:\CodeProject\PuddingAgent\PuddingAgent'
Set-Location $repo

$sourceDir = Join-Path $repo 'Source\PuddingCodeIndex\Contracts\Retrieval'
$targetDir = Join-Path $repo 'Source\PuddingRetrieval.Contracts'

$oldNamespace = 'PuddingCodeIndex.Contracts.Retrieval'
$newNamespace = 'PuddingRetrieval.Contracts'

if (-not (Test-Path $sourceDir)) { throw "source dir missing: $sourceDir" }
if (-not (Test-Path $targetDir)) { throw "target dir missing: $targetDir" }

$files = @(Get-ChildItem $sourceDir -Filter *.cs -File | Sort-Object Name)
"contract files            : $($files.Count)"

$allCs = @(Get-ChildItem (Join-Path $repo 'Source') -Filter *.cs -File -Recurse |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
"scanned cs files          : $($allCs.Count)"

$refFiles = @($allCs | Where-Object {
        try { [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8).Contains($oldNamespace, [StringComparison]::Ordinal) }
        catch { $false }
    })
"files referencing old ns  : $($refFiles.Count)"

$nsDeclFiles = @($files | Where-Object {
        try { [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8).Contains("namespace $oldNamespace;", [StringComparison]::Ordinal) }
        catch { $false }
    })
"files declaring old ns    : $($nsDeclFiles.Count)"

"--- referencing files by project ---"
$refFiles |
    ForEach-Object {
        $rel = $_.FullName.Substring($repo.Length + 1)
        ($rel -split '\\')[1]
    } |
    Group-Object |
    Sort-Object Count -Descending |
    ForEach-Object { '  {0,-42} {1}' -f $_.Name, $_.Count }

if (-not $Apply) {
    $lines = @()
    $lines += "contract files            : $($files.Count)"
    $lines += "scanned cs files          : $($allCs.Count)"
    $lines += "files referencing old ns  : $($refFiles.Count)"
    $lines += "files declaring old ns    : $($nsDeclFiles.Count)"
    $lines += '--- referencing files by project ---'
    $lines += ($refFiles |
        ForEach-Object { ($_.FullName.Substring($repo.Length + 1) -split '\\')[1] } |
        Group-Object |
        Sort-Object Count -Descending |
        ForEach-Object { '  {0,-42} {1}' -f $_.Name, $_.Count })
    $lines += '--- DRY RUN: nothing changed. Re-run with -Apply to migrate. ---'

    $reportPath = Join-Path $repo 'temp\migrate-dryrun.txt'
    [IO.File]::WriteAllLines($reportPath, $lines, (New-Object Text.UTF8Encoding($false)))
    'report=' + $reportPath
    return
}

# ---- apply ----
foreach ($file in $files) {
    $dest = Join-Path $targetDir $file.Name
    if (Test-Path $dest) { throw "target already exists: $dest" }
    git mv $file.FullName $dest
}
"moved $($files.Count) files"

foreach ($file in $files) {
    $dest = Join-Path $targetDir $file.Name
    $text = [IO.File]::ReadAllText($dest, [Text.Encoding]::UTF8)
    $updated = $text.Replace("namespace $oldNamespace;", "namespace $newNamespace;")
    if ($updated -ne $text) {
        [IO.File]::WriteAllText($dest, $updated, (New-Object Text.UTF8Encoding($false)))
    }
}
"rewrote namespace in $($files.Count) files"

$rewritten = 0
foreach ($file in $allCs) {
    if (-not (Test-Path $file.FullName)) { continue }   # moved out
    $text = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
    if ($text -notmatch [regex]::Escape($oldNamespace)) { continue }
    $updated = $text.Replace("using $oldNamespace;", "using $newNamespace;")
    if ($updated -ne $text) {
        [IO.File]::WriteAllText($file.FullName, $updated, (New-Object Text.UTF8Encoding($false)))
        $rewritten++
    }
}
"rewrote using in $rewritten files"
"NOTE: PuddingCodeIndex.csproj must reference PuddingRetrieval.Contracts.csproj before building."
