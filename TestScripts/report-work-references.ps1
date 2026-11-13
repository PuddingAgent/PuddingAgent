#requires -Version 7.0
<#
.SYNOPSIS
    列出 Docs/ 中引用了 .work 路径的文档，供删除前核对。

.DESCRIPTION
    Docs/10_conventions/工作区产物清理规程.md §6 实测：169 份文档把 .work/ 下的路径当作
    证据位置引用。批量删除 .work/<任务> 会让这些指针变成死链，且删除过程不会报错。

    本脚本只读：扫描 Docs/**/*.md，对每个 .work 子目录统计引用它的文档数，并给出样例。
    删除前先跑一次；某目录被引用时，先按规程 §6.3 把证据搬迁再删。

.EXAMPLE
    pwsh -NoProfile -File TestScripts/report-work-references.ps1
    pwsh -NoProfile -File TestScripts/report-work-references.ps1 -Name agent-tool-ownership
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = 'D:\CodeProject\PuddingAgent\PuddingAgent',
    [string]$Name
)

$ErrorActionPreference = 'Stop'
$workDir = Join-Path $RepoRoot '.work'
$docsDir = Join-Path $RepoRoot 'Docs'

if (-not (Test-Path $docsDir)) { throw "Docs not found: $docsDir" }

# 读取全部文档正文一次（UTF-8 显式指定：Get-Content 默认按 GBK 解码会让中文匹配失配）
$docs = Get-ChildItem $docsDir -Recurse -File -Filter '*.md' | ForEach-Object {
    [pscustomobject]@{
        Path = $_.FullName.Substring($RepoRoot.Length + 1)
        Text = [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
    }
}
'DOCS_SCANNED={0}' -f $docs.Count

$targets = if ($Name) {
    @($Name)
} elseif (Test-Path $workDir) {
    @(Get-ChildItem $workDir -Directory -Force | Select-Object -ExpandProperty Name)
} else {
    @()
}

$rows = foreach ($t in ($targets | Sort-Object)) {
    $needle = '.work/' + $t
    $needle2 = '.work\' + $t
    $hits = @($docs | Where-Object {
            $lines = $_.Text -split "`n"
            $live = $lines | Where-Object {
                ($_.Contains($needle) -or $_.Contains($needle2)) -and
                # 已标注为「已清理/原在/待删/产物」的行是历史描述，不算活引用
                -not ($_ -match '已清理|原在|待删|产物已|构建产物|已搬入')
            }
            $live.Count -gt 0
        })
    [pscustomobject]@{
        Work        = $t
        CitedBy     = $hits.Count
        Sample      = if ($hits.Count -gt 0) { $hits[0].Path } else { '' }
    }
}

$rows | Sort-Object CitedBy -Descending | Format-Table -AutoSize -Wrap | Out-String -Width 200

$flagged = @($rows | Where-Object { $_.CitedBy -gt 0 })
''
'REFERENCED={0} / TOTAL={1}' -f $flagged.Count, $rows.Count
if ($flagged.Count -gt 0) {
    'ACTION: 先按清理规程 §6.3 搬迁证据并改指针，再删除；不要直接删。'
}

# 已删除但仍被引用：文档里出现过、磁盘上已不存在的 .work 子目录名
$pattern = '\.work[\\/]([A-Za-z0-9_][A-Za-z0-9_.\-]*)'
$cited = @{}
foreach ($d in $docs) {
    foreach ($m in [regex]::Matches($d.Text, $pattern)) {
        $n = $m.Groups[1].Value.TrimEnd('.', '-')
        if ($n) { $cited[$n] = 1 }
    }
}
$existing = if (Test-Path $workDir) { @(Get-ChildItem $workDir -Directory -Force | Select-Object -ExpandProperty Name) } else { @() }
$missing = @($cited.Keys | Where-Object { $existing -notcontains $_ } | Sort-Object)
''
'CITED_NAMES={0}  EXISTING_DIRS={1}  CITED_BUT_MISSING={2}' -f $cited.Count, $existing.Count, $missing.Count
if ($missing.Count -gt 0) {
    'ALREADY_DANGLING (first 40):'
    $missing | Select-Object -First 40 | ForEach-Object { '    ' + $_ }
}
