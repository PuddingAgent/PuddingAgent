# ============================================================
# 通用「日线 + 事件标记」面板图生成器（无脚本、可切换区间、逐点悬停读数）
#
# 为什么独立成一个工具：这张图的难点不在某个标的上，而在三条约束上——
#   1) render_panel 里只要出现 <script>，同一面板的 <svg> 就会退化成空壳；
#   2) 面板只能读工作区目录内的 html_path，不能读仓库 temp；
#   3) SVG 属性必须带引号，否则清洗器会丢弃。
# 把这三条与绘图逻辑固化成工具后，换标的只换参数，不再重写脚本。
#
# 用法：
#   pwsh -File Tools/Charts/render-series-chart.ps1 -Code 518880 -Title "黄金 9999（518880 代理）"
#   pwsh -File Tools/Charts/render-series-chart.ps1 -Csv data.csv -Title "自备序列" -RightAxisFactor 0
#
# 输出：写到 -Out（默认仓库 temp），并打印需要复制到工作区的目标路径。
# 渲染：把文件复制到 D:\Data\workspaces\default\deliverables\ 后
#       render_panel(panel_id=..., html_path=<工作区内路径>)
# 验收：必须 screen_capture 看一眼，不要用「文件里有那段字符串」当证据。
# ============================================================
param(
    [string]$Title = '日线走势 × 关键事件',
    [string]$Code = '',                       # 必盈基金/ETF 6 位代码（走 jj/lskx/{code}/d）
    [string]$Csv = '',                        # 或自备 CSV：date,close,volume
    [string]$From = '2026-01-02',
    [string]$EventsPath = '',                 # {events:[{date,label,category,source}]}
    [string]$Out = 'D:\CodeProject\PuddingAgent\PuddingAgent\temp\series-chart.html',
    [double]$RightAxisFactor = 107.4,          # 0 = 不画右轴
    [string]$RightAxisUnit = '元/克',
    [int[]]$Ranges = @(0, 120, 60, 20),
    [int]$MaFast = 20,
    [int]$MaSlow = 60,
    [double]$KeyLevelLow = 0,
    [double]$KeyLevelHigh = 0,
    [string]$KeyLevelLabel = '',
    [string]$DeliverablesRoot = 'D:\Data\workspaces\default\deliverables'
)

$ErrorActionPreference = 'Stop'

function Get-Series {
    if ($Csv) {
        if (-not (Test-Path $Csv)) { throw "CSV not found: $Csv" }
        return @(Get-Content $Csv -Encoding UTF8 | Select-Object -Skip 1 | ForEach-Object {
                $f = $_ -split ','
                if ($f.Count -lt 2) { return }
                [pscustomobject]@{ d = $f[0].Trim(); c = [double]$f[1]; v = if ($f.Count -gt 2) { [double]$f[2] } else { 0 }
                }
            })
    }
    if (-not $Code) { throw 'Either -Code or -Csv is required.' }
    $cfgPath = 'D:\Data\config\biying.api.json'
    if (-not (Test-Path $cfgPath)) { throw "Licence config not found: $cfgPath" }
    $lic = (Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json).licence
    $url = "https://api.biyingapi.com/jj/lskx/$Code/d/$lic"
    $body = (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 40).Content
    return @($body | ConvertFrom-Json | Sort-Object d | ForEach-Object {
            [pscustomobject]@{ d = $_.d; c = [double]$_.c; v = [double]$_.v }
        })
}

function Get-MAAt($arr, $i, $k) {
    if ($i + 1 -lt $k) { return $null }
    ($arr[($i - $k + 1)..$i] | Measure-Object -Average).Average
}

$raw = Get-Series
$cAll = @($raw | ForEach-Object { $_.c })
$p = @()
for ($i = 0; $i -lt $raw.Count; $i++) {
    if ($raw[$i].d -lt $From) { continue }
    $p += [pscustomobject]@{
        d = $raw[$i].d; c = $raw[$i].c; v = $raw[$i].v
        maF = (Get-MAAt $cAll $i $MaFast); maS = (Get-MAAt $cAll $i $MaSlow)
    }
}
if ($p.Count -lt 2) { throw 'Series has fewer than 2 points after -From filter.' }
$n = $p.Count

$events = @()
if ($EventsPath -and (Test-Path $EventsPath)) {
    $events = @((Get-Content $EventsPath -Raw -Encoding UTF8 | ConvertFrom-Json).events | ForEach-Object {
            [pscustomobject]@{
                date = $(if ($_.date -is [datetime]) { $_.date.ToString('yyyy-MM-dd') } else { [string]$_.date })
                label = [string]$_.label
                category = [string]$_.category
            }
        }) | Sort-Object date
}
$catColors = @{ rates = '#ff5d73'; trade = '#ffa94d'; forecast = '#c77dff'; price = '#4ea1ff' }

$cw = 1040; $cl = 64; $cr = 118; $ptop = 52; $pheight = 286; $vtop = 382; $vheight = 84
$plotW = $cw - $cl - $cr
$lastDate = $p[$n - 1].d
$staleDays = ((Get-Date).Date - [datetime]::ParseExact($lastDate, 'yyyy-MM-dd', $null).Date).Days
$staleColor = if ($staleDays -le 2) { '#7fd1ae' } elseif ($staleDays -le 7) { '#ffb648' } else { '#ff5d73' }
$staleText = if ($staleDays -le 0) { '数据截至 ' + $lastDate + '（今日）' } else { '数据截至 ' + $lastDate + '（' + $staleDays + ' 天前）' }

function New-Chart([int]$win) {
    $start = 0
    if ($win -gt 0 -and $win -lt $n) { $start = $n - $win }
    $w = @($p[$start..($n - 1)])
    $wn = $w.Count
    $ys = @()
    foreach ($row in $w) { $ys += $row.c; if ($null -ne $row.maF) { $ys += $row.maF }; if ($null -ne $row.maS) { $ys += $row.maS } }
    $lo = [Math]::Floor((($ys | Measure-Object -Minimum).Minimum - 0.12) * 20) / 20
    $hi = [Math]::Ceiling((($ys | Measure-Object -Maximum).Maximum + 0.12) * 20) / 20
    $maxV = ($w | ForEach-Object { $_.v } | Measure-Object -Maximum).Maximum
    if ($maxV -le 0) { $maxV = 1 }
    $step = $plotW / [Math]::Max(1, ($wn - 1))
    $barW = [Math]::Max(1, [Math]::Round($step))
    $sx = { param($i) [Math]::Round($cl + $plotW * $i / [Math]::Max(1, ($wn - 1))) }
    $sy = { param($v) [Math]::Round($ptop + $pheight * ($hi - $v) / ($hi - $lo)) }
    $sv = { param($v) [Math]::Round($vtop + $vheight - $vheight * $v / $maxV) }

    $s = New-Object System.Text.StringBuilder
    [void]$s.Append('<svg viewBox="0 0 ' + $cw + ' 540" style="width:100%;height:auto;display:block">')
    $gstep = if ($hi - $lo -gt 4) { 1.0 } elseif ($hi - $lo -gt 2) { 0.5 } elseif ($hi - $lo -gt 0.6) { 0.25 } else { 0.1 }
    $gv = [Math]::Ceiling($lo / $gstep) * $gstep
    while ($gv -le $hi) {
        $y = & $sy $gv
        [void]$s.Append('<line x1="' + $cl + '" y1="' + $y + '" x2="' + ($cw - $cr) + '" y2="' + $y + '" stroke="#1b2736"/>')
        $digits = if ($gstep -lt 0.25) { '0.00' } elseif ($gstep -lt 1) { '0.00' } else { '0.00' }
        [void]$s.Append('<text x="' + ($cl - 8) + '" y="' + ($y + 4) + '" fill="#6f86a0" font-size="11" text-anchor="end">' + $gv.ToString($digits) + '</text>')
        if ($RightAxisFactor -gt 0) {
            [void]$s.Append('<text x="' + ($cw - $cr + 8) + '" y="' + ($y + 4) + '" fill="#4f637a" font-size="11">' + [Math]::Round($gv * $RightAxisFactor) + '</text>')
        }
        $gv += $gstep
    }
    $seen = @{}
    for ($i = 0; $i -lt $wn; $i++) {
        $m = $w[$i].d.Substring(0, 7)
        if ($seen.ContainsKey($m)) { continue }
        if ($i -gt 0 -and (& $sx $i) -lt ($cl + 26)) { continue }
        $seen[$m] = $true
        [void]$s.Append('<text x="' + (& $sx $i) + '" y="' + ($vtop + $vheight + 18) + '" fill="#6f86a0" font-size="10" text-anchor="middle">' + $m.Substring(2) + '</text>')
    }
    $seg = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $wn; $i++) { $seg.Add((& $sx $i).ToString() + ',' + (& $sy $w[$i].c).ToString()) }
    [void]$s.Append('<polyline points="' + ($seg -join ' ') + '" fill="none" stroke="#4ea1ff" stroke-width="1.8" stroke-linejoin="round"/>')
    foreach ($k in @(@{ k = 'maF'; c = '#ffb648'; t = ('MA' + $MaFast) }, @{ k = 'maS'; c = '#c77dff'; t = ('MA' + $MaSlow) })) {
        $seg = New-Object System.Collections.Generic.List[string]
        $lastV = $null
        for ($i = 0; $i -lt $wn; $i++) { $mv = $w[$i].($k.k); if ($null -ne $mv) { $seg.Add((& $sx $i).ToString() + ',' + (& $sy $mv).ToString()); $lastV = $mv } }
        if ($seg.Count -lt 2) { continue }
        [void]$s.Append('<polyline points="' + ($seg -join ' ') + '" fill="none" stroke="' + $k.c + '" stroke-width="1.2" opacity="0.9"/>')
        [void]$s.Append('<text x="' + ($cw - $cr + 8) + '" y="' + ((& $sy $lastV) + 4) + '" fill="' + $k.c + '" font-size="10">' + $k.t + ' ' + $lastV.ToString('0.00') + '</text>')
    }
    if ($KeyLevelLow -gt 0 -and $KeyLevelHigh -gt 0) {
        foreach ($lv in @(@{ v = $KeyLevelLow; t = ('阻力 ' + $KeyLevelLow + '-' + $KeyLevelHigh); c = '#ff8fa3' }, @{ v = $KeyLevelHigh; t = ''; c = '#ff8fa3' })) {
            if ($lv.v -lt $lo -or $lv.v -gt $hi) { continue }
            $y = & $sy $lv.v
            [void]$s.Append('<line x1="' + $cl + '" y1="' + $y + '" x2="' + ($cw - $cr) + '" y2="' + $y + '" stroke="' + $lv.c + '" stroke-width="1" stroke-dasharray="6 4" opacity="0.45"/>')
            if ($lv.t -and $KeyLevelLabel) {
                [void]$s.Append('<text x="' + ($cl + 6) + '" y="' + ($y - 5) + '" fill="' + $lv.c + '" font-size="10" opacity="0.8">' + $KeyLevelLabel + '</text>')
            }
        }
    }
    $vseg = New-Object System.Collections.Generic.List[string]
    $vseg.Add($cl.ToString() + ',' + ($vtop + $vheight).ToString())
    for ($i = 0; $i -lt $wn; $i++) { $vseg.Add((& $sx $i).ToString() + ',' + (& $sv $w[$i].v).ToString()) }
    $vseg.Add($cl.ToString() + ',' + ($vtop + $vheight).ToString())
    [void]$s.Append('<polygon points="' + ($vseg -join ' ') + '" fill="#2f6ea8" opacity="0.42"/>')
    [void]$s.Append('<text x="' + ($cl - 8) + '" y="' + ($vtop + 10) + '" fill="#6f86a0" font-size="10" text-anchor="end">量</text>')

    $rowY = @{ 0 = ($vtop + $vheight + 40); 1 = ($vtop + $vheight + 60) }
    $rowLastX = @{ 0 = -999; 1 = -999 }
    $legend = New-Object System.Collections.Generic.List[string]
    $idxEvt = 0
    foreach ($e in $events) {
        if ($e.date -lt $w[0].d -or $e.date -gt $w[$wn - 1].d) { continue }
        $idxEvt++
        $idx = -1
        for ($i = $wn - 1; $i -ge 0; $i--) { if ($w[$i].d -le $e.date) { $idx = $i; break } }
        if ($idx -lt 0) { continue }
        $col = $catColors[$e.category]; if (-not $col) { $col = '#ff5d73' }
        $x = & $sx $idx
        $row = 0
        if (($x - $rowLastX[0]) -lt 34) { if (($x - $rowLastX[1]) -ge 34) { $row = 1 } }
        $rowLastX[$row] = $x
        $by = $rowY[$row]
        [void]$s.Append('<line x1="' + $x + '" y1="' + $ptop + '" x2="' + $x + '" y2="' + ($vtop + $vheight) + '" stroke="' + $col + '" stroke-width="1" stroke-dasharray="3 3" opacity="0.4"/>')
        [void]$s.Append('<line x1="' + $x + '" y1="' + ($vtop + $vheight) + '" x2="' + $x + '" y2="' + ($by - 9) + '" stroke="' + $col + '" stroke-width="1" opacity="0.3"/>')
        [void]$s.Append('<circle cx="' + $x + '" cy="' + $by + '" r="8" fill="' + $col + '"><title>' + $e.date + ' ' + $e.label + '</title></circle>')
        [void]$s.Append('<text x="' + $x + '" y="' + ($by + 4) + '" fill="#0d1520" font-size="9" font-weight="700" text-anchor="middle">' + $idxEvt + '</text>')
        $legend.Add('<span style="display:inline-block;margin:1px 8px 1px 0;white-space:nowrap"><b style="color:' + $col + '">' + $idxEvt + '</b> ' + $e.date.Substring(5) + ' · <span style="color:#b9c9dc">' + $e.label + '</span></span>')
    }

    # 逐日读数：透明热区 + 原生 title（面板禁脚本，这是唯一可行的悬停方案）
    for ($i = 0; $i -lt $wn; $i++) {
        $bx = [Math]::Round((& $sx $i) - $barW / 2)
        $tip = $w[$i].d + ' 收 ' + $w[$i].c.ToString('0.000')
        if ($RightAxisFactor -gt 0) { $tip += ' = ' + $RightAxisUnit + [Math]::Round($w[$i].c * $RightAxisFactor) }
        if ($null -ne $w[$i].maF) { $tip += ' · MA' + $MaFast + ' ' + $w[$i].maF.ToString('0.00') }
        if ($null -ne $w[$i].maS) { $tip += ' · MA' + $MaSlow + ' ' + $w[$i].maS.ToString('0.00') }
        if ($w[$i].v -gt 0) { $tip += ' · 量 ' + [Math]::Round($w[$i].v / 10000) + '万' }
        [void]$s.Append('<rect x="' + $bx + '" y="' + $ptop + '" width="' + $barW + '" height="' + ($vtop + $vheight - $ptop) + '" fill="transparent"><title>' + $tip + '</title></rect>')
    }
    [void]$s.Append('</svg>')
    [void]$s.Append('<div style="margin-top:2px;font-size:11.5px;line-height:1.85">' + ($legend -join '') + '</div>')
    return $s.ToString()
}

$rangeDefs = @()
foreach ($rg in $Ranges) {
    $label = if ($rg -le 0) { '全部' } else { '近 ' + $rg + ' 日' }
    $rangeDefs += [pscustomobject]@{ id = ('r' + $rg); cls = ('v' + $rg); w = $rg; label = $label }
}

$html = New-Object System.Text.StringBuilder
[void]$html.Append('<style>')
[void]$html.Append('#gcroot input[name=gcrange]{display:none}#gcroot .gcview{display:none}')
foreach ($def in $rangeDefs) { [void]$html.Append('#gcroot #gc-' + $def.id + ':checked ~ .gcbody .' + $def.cls + '{display:block}') }
[void]$html.Append('#gcroot input[name=gcrange]:checked + label{background:#2f5f9e;color:#fff}')
[void]$html.Append('#gcroot label{cursor:pointer;background:#1b2a3d;color:#cfe0f2;border:1px solid #2b3d54;border-radius:6px;padding:3px 10px;font-size:12px;margin-right:6px;display:inline-block}')
[void]$html.Append('</style>')
[void]$html.Append('<div id="gcroot" style="font:13px/1.5 -apple-system,Segoe UI,sans-serif;color:#e8eef7;background:#0d1520;padding:14px 16px 10px;border-radius:10px">')
[void]$html.Append('<div style="display:flex;align-items:baseline;gap:12px;flex-wrap:wrap"><div style="font-size:15px;font-weight:600">' + $Title + '</div><div style="font-size:12px;color:' + $staleColor + '">' + $staleText + '</div></div>')
$rightNote = if ($RightAxisFactor -gt 0) { ' · 右轴 ' + $RightAxisUnit + '（×' + $RightAxisFactor + '）' } else { '' }
[void]$html.Append('<div style="color:#8ba0b8;font-size:12px;margin:2px 0 8px">' + $From + ' ~ ' + $lastDate + ' · 日线 · 悬停任一交易日可读数' + $rightNote + '</div>')
foreach ($def in $rangeDefs) {
    $checked = if ($def.w -le 0) { ' checked' } else { '' }
    [void]$html.Append('<input type="radio" name="gcrange" id="gc-' + $def.id + '"' + $checked + '><label for="gc-' + $def.id + '">' + $def.label + '</label>')
}
[void]$html.Append('<div class="gcbody" style="margin-top:8px">')
foreach ($def in $rangeDefs) {
    [void]$html.Append('<div class="gcview ' + $def.cls + '">')
    [void]$html.Append((New-Chart $def.w))
    [void]$html.Append('</div>')
}
[void]$html.Append('</div>')
[void]$html.Append('<div style="margin-top:4px;font-size:11px;color:#8ba0b8"><span style="color:#ff5d73">● 利率</span>&nbsp;&nbsp;<span style="color:#ffa94d">● 贸易</span>&nbsp;&nbsp;<span style="color:#c77dff">● 机构预期</span>&nbsp;&nbsp;<span style="color:#4ea1ff">● 价格事件</span></div>')
[void]$html.Append('</div>')

Set-Content -LiteralPath $Out -Value ($html.ToString() -split "`r?`n") -Encoding UTF8
$scriptCount = ([regex]::Matches($html.ToString(), '<script').Count)
if ($scriptCount -gt 0) { throw 'Output contains <script>; the panel would drop every SVG element. Remove it before rendering.' }

# render_panel 只能读工作区目录内的 html_path；这里直接把产物落到那里并打印可用的调用参数。
$delivered = ''
if ($DeliverablesRoot) {
    if (-not (Test-Path $DeliverablesRoot)) { New-Item -ItemType Directory -Path $DeliverablesRoot -Force | Out-Null }
    $delivered = Join-Path $DeliverablesRoot (Split-Path $Out -Leaf)
    if ([IO.Path]::GetFullPath($delivered) -ne [IO.Path]::GetFullPath($Out)) {
        Copy-Item -LiteralPath $Out -Destination $delivered -Force
    }
}

Write-Output ('out=' + $Out)
Write-Output ('points=' + $n + '  bytes=' + (Get-Item $Out).Length + '  script_tags=' + $scriptCount + '  events=' + $events.Count)
if ($delivered) {
    Write-Output ('delivered=' + $delivered)
    Write-Output ('render_panel(panel_id="' + [IO.Path]::GetFileNameWithoutExtension($delivered) + '", html_path="' + $delivered + '")')
}
else {
    Write-Output 'next: copy into the workspace deliverables directory, then render_panel(html_path=...)'
}
