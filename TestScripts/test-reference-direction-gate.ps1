# 依赖方向门禁的常驻登记校验。
#
# 背景：根 Directory.Build.targets 的 EnforceReferenceDirection 是 **opt-in** 的——
# 不声明 AllowedProjectReferences 就完全不检查。这带来一个天然漏洞：
# 有人把某组件的声明删掉，门禁就悄悄失效了，而仓库不会报任何错。
# （方案 §4 提到这条，此前未做。）
#
# 两条断言：
#   A. 全仓含 <AllowedProjectReferences> 的项目集合，必须等于下方 $Expected 登记表。
#      —— 删声明 → 红；新增声明未登记 → 也红（强制走一次 review）。
#   B. 三个契约层组件的自定义边界 target 必须仍在（它们是比白名单更严的全禁规则，
#      一旦被人删掉，契约层就失去编译期保护）。
#
# 任一条失败 → 退出码 1。

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'Source'

# ---- 登记表：新增 must be updated here in the same commit ----
$Expected = @(
    'PuddingComputer.Automation'
    'PuddingComputer.UIAutomation'
)

# 契约层：它们的自定义 target 是「全禁」而非白名单，比 PUDDING001 更严
$ContractGuards = [ordered]@{
    'Pudding.Contracts'             = 'EnforceContractsBoundary'
    'Pudding.ToolPlugins.Contracts' = 'EnforceLeafBoundary'
    'Pudding.Api.Contracts'         = 'EnforcePublicContractBoundary'
}

$failures = @()

if (-not (Test-Path $sourceRoot)) {
    Write-Output "FAIL: Source directory not found at $sourceRoot"
    exit 1
}

# ---- A. 声明集合必须等于登记表 ----
$found = @()
foreach ($p in (Get-ChildItem -Path $sourceRoot -Recurse -Filter *.csproj -File)) {
    $text = [IO.File]::ReadAllText($p.FullName, [Text.Encoding]::UTF8)
    if ($text -match '<AllowedProjectReferences[ >]') {
        $found += $p.BaseName
    }
}
$found = @($found | Sort-Object -Unique)
$expectedSorted = @($Expected | Sort-Object -Unique)

$missing = @($expectedSorted | Where-Object { $found -notcontains $_ })
$unregistered = @($found | Where-Object { $expectedSorted -notcontains $_ })

if ($missing.Count -gt 0) {
    $failures += ("A: 登记表里的项目缺少 AllowedProjectReferences 声明 -> " + ($missing -join ', '))
}
if ($unregistered.Count -gt 0) {
    $failures += ("A: 发现未登记却声明了 AllowedProjectReferences 的项目 -> " + ($unregistered -join ', ') + "（请同步更新本脚本的 `$Expected 登记表）")
}
if ($missing.Count -eq 0 -and $unregistered.Count -eq 0) {
    Write-Output ("PASS A: AllowedProjectReferences 声明集合 = 登记表（" + $found.Count + " 项：" + ($found -join ', ') + "）")
}

# ---- B. 契约层自定义边界 target 必须仍在 ----
$guardResults = @()
foreach ($name in $ContractGuards.Keys) {
    $csproj = Join-Path (Join-Path $sourceRoot $name) ($name + '.csproj')
    if (-not (Test-Path $csproj)) {
        $failures += ("B: 找不到 $name 的 csproj")
        continue
    }
    $text = [IO.File]::ReadAllText($csproj, [Text.Encoding]::UTF8)
    $guard = $ContractGuards[$name]
    if ($text -notmatch [regex]::Escape($guard)) {
        $failures += ("B: $name 缺少自定义边界 target '$guard'（契约层将失去编译期保护）")
    }
    else {
        $guardResults += $name
    }
}
if ($guardResults.Count -eq $ContractGuards.Count) {
    Write-Output ("PASS B: 契约层自定义边界 target 齐备（" + ($guardResults -join ', ') + "）")
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "FAILURES:"
    foreach ($f in $failures) { Write-Output ("  - " + $f) }
    exit 1
}

Write-Output ""
Write-Output "RESULT: OK (2/2)"
exit 0
