# 工具可见性闸门审计（只读，不改任何状态）。
#
# 为什么需要它：本仓踩过两次同一类坑 —— 工具代码、包清单都做了，
# 但某个 agent 的「能力快照」里没有对应 cap-xxx，于是工具静默不可见：
#   · BuiltInAgentTemplates.DefaultToolNames 漏登记（commit 46588b4）
#   · 模板预设加了 cap-send-file，但存量 agent 的 manifest.json 没跟着变
# 判据只有三道（实测更正，模板预设不是运行时闸门 —— send_message 从未在
# 预设里，却一直可用）：
#   ① core/ToolPackages/*.json 的 exports 有 <tool> -> 类型
#   ② 宿主程序集里真的存在该类型（附带阴性对照探针，防「搜不到=没有」误判）
#   ③ Agent 实例 manifest.json 的 capabilities.allowedToolIds 含 cap-<tool>
#
# 用法：
#   pwsh -NoProfile -File TestScripts/verify-tool-gate.ps1 -ToolIds send_file
#   pwsh -NoProfile -File TestScripts/verify-tool-gate.ps1 -ToolIds send_file,send_image
# 退出码：0 = 全部 PASS；1 = 至少一个 BLOCKED。
param(
  [string]$ToolIds = 'send_file',
  [string]$CoreDir,
  [string]$AgentManifest = 'D:\Data\agents\default.global_general-assistant.6a8\manifest.json'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$ids = @($ToolIds -split '[,;\s]+' | Where-Object { $_ })
if ($ids.Count -eq 0) { throw 'ToolIds is empty.' }

if (-not $CoreDir) {
  $cand = Get-ChildItem -Path (Join-Path $repo 'temp\build') -Directory -Recurse -Depth 6 -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'core' -and (Test-Path (Join-Path $_.FullName 'PuddingHost.dll')) } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
  if (-not $cand) { throw 'Cannot locate a deployed core directory under temp\build. Pass -CoreDir explicitly.' }
  $CoreDir = $cand.FullName
}
Write-Output ("core      = " + $CoreDir)
Write-Output ("manifest  = " + $AgentManifest)

# ① 包清单 exports（合并 ToolPackages 下所有 *.json）
$exports = @{}
$pkgDir = Join-Path $CoreDir 'ToolPackages'
if (Test-Path $pkgDir) {
  Get-ChildItem $pkgDir -Filter *.json | ForEach-Object {
    $j = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($j.exports) {
      $j.exports.PSObject.Properties | ForEach-Object { $exports[$_.Name] = $_.Value }
    }
  }
}

# ② 宿主程序集：把 core 下所有 dll 拼成一份 ASCII 文本做类型探针
$dllText = New-Object Text.StringBuilder
Get-ChildItem $CoreDir -Filter *.dll | ForEach-Object {
  try {
    $fs = New-Object IO.FileStream($_.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
      $len = [int]$fs.Length
      if ($len -gt 0 -and $len -lt 200MB) {
        $buf = New-Object byte[] $len
        $fs.Read($buf, 0, $len) | Out-Null
        [void]$dllText.Append([Text.Encoding]::ASCII.GetString($buf))
      }
    } finally { $fs.Dispose() }
  } catch { }
}
$dllAll = $dllText.ToString()

# 阴性对照：这个串必然不存在；若为 True 说明探针本身失效
$neg = $dllAll.Contains('ZZZ_PROBE_TOOLGATE_9Q2')
Write-Output ("negative-control ZZZ_PROBE_TOOLGATE_9Q2 present = $neg (must be False)")
if ($neg) { throw 'Probe is unreliable: negative control string was found.' }

# ③ Agent 实例 allowedToolIds
$instCaps = @()
if (Test-Path $AgentManifest) {
  $m = Get-Content $AgentManifest -Raw -Encoding UTF8 | ConvertFrom-Json
  $instCaps = @($m.capabilities.allowedToolIds)
} else {
  Write-Output 'WARN: agent manifest not found; gate 3 cannot be evaluated.'
}

# 模板预设（只作参考，不是闸门）
$presetCaps = @()
$presetPath = Join-Path $CoreDir 'default-data\agent-template-presets\general-assistant.json'
if (Test-Path $presetPath) {
  $p = Get-Content $presetPath -Raw -Encoding UTF8 | ConvertFrom-Json
  $presetCaps = @($p.selectedCapabilityIds)
}
Write-Output ("exports=" + $exports.Count + "  preset（参考）=" + $presetCaps.Count + "  instance=" + $instCaps.Count)
Write-Output ''

$failed = 0
Write-Output ("{0,-18} {1,-9} {2,-10} {3,-10} {4}" -f 'tool', 'export', 'typeInDll', 'instance', 'verdict')
Write-Output ('-' * 60)
foreach ($id in $ids) {
  $cap = 'cap-' + ($id -replace '_', '-')
  $hasExport = $exports.ContainsKey($id)
  $typeName = if ($hasExport) { [string]$exports[$id] } else { '' }
  $shortType = if ($typeName) { ($typeName -split '\.')[-1] } else { '' }
  $hasType = if ($shortType) { $dllAll.Contains($shortType) } else { $false }
  $inInst = $instCaps -contains $cap
  $ok = $hasExport -and $hasType -and $inInst
  if (-not $ok) { $failed++ }
  Write-Output ("{0,-18} {1,-9} {2,-10} {3,-10} {4}" -f $id, $hasExport, $hasType, $inInst, $(if ($ok) { 'PASS' } else { 'BLOCKED' }))
}

Write-Output ''
if ($failed -eq 0) {
  Write-Output 'All gates pass on disk. If search_tools still cannot load it, the running Core holds a startup snapshot: reload is required.'
  exit 0
} else {
  Write-Output ("$failed tool(s) blocked. Fix the failing column above; the tool will stay invisible silently otherwise.")
  exit 1
}
