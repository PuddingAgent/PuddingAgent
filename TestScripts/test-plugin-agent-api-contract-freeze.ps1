param()
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repoRoot 'Docs/12_features/插件Agent配置API-核心文件指纹.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($manifest.version -ne 1 -or $manifest.normalization -ne 'utf8-lf' -or $manifest.files.Count -lt 1) { throw 'Invalid plugin Agent API freeze manifest.' }
foreach ($entry in $manifest.files) {
    $resolved = [IO.Path]::GetFullPath((Join-Path $repoRoot $entry.path))
    if (!$resolved.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Contract path escaped repository.' }
    if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Missing protected file: $($entry.path)" }
    $content = [IO.File]::ReadAllText($resolved).Replace("`r`n", "`n").Replace("`r", "`n")
    $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($content)))
    if ($actual -ne $entry.sha256) { throw "Plugin Agent API core changed: $($entry.path). Stop and report; do not update the manifest." }
}
Write-Output "PASS: $($manifest.files.Count) protected files match the plugin Agent API baseline."
