# UIA sidecar 冒烟测试。
#
# 目的：Tools/UiaSidecar/observe.ps1 是「唯一已知能读到完整 UIA 树」的观察通道
# （见 Docs/14_reports/2026-11-11-UIA观察层可读性核查-actionable恒0定案.md §11）。
# 它坏掉时必须是**显式失败**，而不是悄悄返回一份看似正常的结果。
#
# 三个断言：
#   A. 正例：观察前台窗口 → ok=true 且 elementCount >= 0（0 也算合法，可能读的是空窗口）
#   B. 反例：无效句柄 → ok=false 且 error 以 bad_hwnd 开头
#   C. 反例：指定了标题但匹配不到 → ok=false 且 error=title_not_found
#      （这条锁的是「不得静默回退到前台窗口」，修之前会返回 ok=true）
#
# 任一条失败 → 退出码 1。

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$observe = Join-Path $repoRoot 'Tools\UiaSidecar\observe.ps1'
$workDir = Join-Path $repoRoot '.work\uia-sidecar'
if (-not (Test-Path $workDir)) { New-Item -ItemType Directory -Path $workDir -Force | Out-Null }

if (-not (Test-Path $observe)) {
    Write-Output "FAIL: observe.ps1 not found at $observe"
    exit 1
}

$failures = @()

function Invoke-Observe {
    param([hashtable]$Params = @{}, [string]$OutName)
    $outPath = Join-Path $workDir $OutName
    if (Test-Path $outPath) { Remove-Item $outPath -Force }

    # 必须用 hashtable splatting。数组 splat 会按**位置**绑定：
    # @('-TitleLike','xxx') 会把 '-TitleLike' 绑给 $TitleLike、'xxx' 绑给 $Hwnd，
    # 使本用例测试的根本不是它声称的东西（本脚本首次运行就踩了这个坑）。
    $all = @{ OutPath = $outPath }
    foreach ($k in $Params.Keys) { $all[$k] = $Params[$k] }

    & $observe @all | Out-Null
    if (-not (Test-Path $outPath)) { return $null }
    return (Get-Content $outPath -Raw | ConvertFrom-Json)
}

# ---- A. 正例：前台窗口 ----
$a = Invoke-Observe -Params @{} -OutName 'smoke-foreground.json'
if ($null -eq $a) {
    $failures += "A: no output file produced for foreground observation"
}
elseif (-not $a.ok) {
    $failures += ("A: expected ok=true for foreground, got ok=false error=" + $a.error)
}
else {
    Write-Output ("PASS A: foreground -> elems=" + $a.elementCount + " actionable=" + $a.actionableCount + " ms=" + $a.elapsedMs)
}

# ---- B. 反例：无效句柄 ----
$b = Invoke-Observe -Params @{ Hwnd = '1' } -OutName 'smoke-badhwnd.json'
if ($null -eq $b) {
    $failures += "B: no output file produced for bad hwnd"
}
elseif ($b.ok) {
    $failures += "B: expected ok=false for invalid hwnd, got ok=true"
}
elseif (("" + $b.error) -notlike 'bad_hwnd*') {
    $failures += ("B: expected error starting with bad_hwnd, got " + $b.error)
}
else {
    Write-Output ("PASS B: invalid hwnd -> ok=false error=" + $b.error)
}

# ---- C. 反例：标题匹配不到必须显式失败，不得回退前台 ----
$c = Invoke-Observe -Params @{ TitleLike = 'NONEXISTENT_WINDOW_XYZ_123' } -OutName 'smoke-notitle.json'
if ($null -eq $c) {
    $failures += "C: no output file produced for unmatched title"
}
elseif ($c.ok) {
    $failures += ("C: expected ok=false for unmatched title, got ok=true source=" + $c.source + " (silent fallback to foreground!)")
}
elseif (("" + $c.error) -ne 'title_not_found') {
    $failures += ("C: expected error=title_not_found, got " + $c.error)
}
else {
    Write-Output ("PASS C: unmatched title -> ok=false error=" + $c.error)
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "FAILURES:"
    foreach ($f in $failures) { Write-Output ("  - " + $f) }
    exit 1
}

Write-Output ""
Write-Output "RESULT: OK (3/3)"
exit 0
