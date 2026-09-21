<#
.SYNOPSIS
  技能组合盘点（G1，只读）。产出：存量 / 关键词空间 / 家族分布 / 成本 / 淘汰候选代理指标。

.DESCRIPTION
  为什么需要它：在它之前，"技能太多"只是印象。没有量化事实就无法回答
  「哪些技能该合并、哪些该淘汰、哪些只能靠噪声命中」，也就无法让裁决（ChangeVerdict）有据可依。
  本脚本**只读**：不写任何技能文件、不改 manifest、不动 enabled 状态。

  两项口径必须对齐既有实现与既有事实：

  1) **关键词空间 = 注入器实际用的那一套**，不是 manifest.keywords 原样。
     口径逐字对齐 ``Source/PuddingRuntime/Services/Skills/SkillEnforcerService.cs:126`` 的 CollectKeywords：
        Keywords → Tags → SkillId → Name → Name 按 [空格 | , / ： 、] 切分（长度 > 1 的片段），
        全部按 OrdinalIgnoreCase 去重。
     只统计 manifest.keywords 会漏掉 tags 泄漏 —— 而 D1（治理/溯源标签进关键词空间）正是最严重的一项。

  2) **token 成本是估算，不是实测**。报告以**字符数（确定值）**为主，
     并列一个显式公式的估算值（ASCII 4 字符/token，非 ASCII 1 字符/token）。

.EXAMPLE
  pwsh -NoProfile -File "TestScripts\report-skill-portfolio.ps1"
  pwsh -NoProfile -File "TestScripts\report-skill-portfolio.ps1" -OutFile "Docs\Reports\skills-G1.md"
#>
[CmdletBinding()]
param(
    [string]$SkillsRoot = 'D:\data\agents\default.global_general-assistant.6a8\skills',
    [string]$OutFile,
    [int]$TopOffenders = 15,
    [ValidateSet('portfolio', 'denoise-impact')]
    [string]$Mode = 'portfolio'
)

$ErrorActionPreference = 'Stop'

# 疑似工具名：snake_case（至少一个下划线）且全小写 ASCII —— 这正是工具名被当关键词的形态。
$script:ToolNamePattern = '^[a-z][a-z0-9]*(_[a-z0-9]+)+$'

# 裸名工具（无下划线），形态规则识别不了 —— 显式声明为**补充清单**（来源：平台工具目录）。
# 这是声明而非推导：可能有遗漏 ⇒ D2 只会低估，不会高估。
$script:BareToolNames = @('shell', 'sleep', 'asr')

# CollectKeywords 里 Name 的切分字符（逐字对齐源码）。
$script:NameSeparators = @(' ', '|', ',', '/', '：', '、')

function Get-KeywordCategory {
    <#
      按**来源**而非猜测分类：
        D1 治理/溯源  = 来自 Tags（系统写入的元数据，与"技能教了什么"无关）
        D2 工具名     = 命中 snake_case 形态
        D3 名称分词   = 来自 Name 切分出的片段（通用词，如 agent / check / status）
        语义          = 其余（显式 keywords、Name 全句、SkillId）
      来源信息来自 CollectKeywords 的复现过程，因此 D1 是**精确**分类，不是正则猜测。
    #>
    param([string]$Origin, [string]$Keyword)
    if ($Origin -eq 'tag') { return 'D1' }
    if ($Keyword -match $script:ToolNamePattern) { return 'D2' }
    if ($script:BareToolNames -contains $Keyword.ToLowerInvariant()) { return 'D2' }
    if ($Origin -eq 'name-token') { return 'D3' }
    return '语义'
}

function Get-SkillPortfolio {
    param([string]$Root)
    if (-not (Test-Path -LiteralPath $Root)) {
        throw "技能根目录不存在：$Root"
    }

    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($dir in (Get-ChildItem -LiteralPath $Root -Directory | Sort-Object Name)) {
        $manifestPath = Join-Path $dir.FullName 'manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) { continue }

        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $skillMdPath = Join-Path $dir.FullName 'SKILL.md'
        $bodyBytes = 0
        $bodyLines = 0
        if (Test-Path -LiteralPath $skillMdPath) {
            $bodyBytes = (Get-Item -LiteralPath $skillMdPath).Length
            $bodyLines = (Get-Content -LiteralPath $skillMdPath -Encoding UTF8).Count
        }

        $skillId = [string]$manifest.skillId
        $name = [string]$manifest.name
        $tags = @($manifest.tags | Where-Object { $_ })
        $keywords = @($manifest.keywords | Where-Object { $_ })

        # ── 复现 CollectKeywords（顺序即优先级），并记录每个关键词的来源 ──
        $rows = New-Object System.Collections.Generic.List[object]
        $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($kw in $keywords) { if ($seen.Add($kw)) { $rows.Add([pscustomobject]@{ Keyword = $kw; Origin = 'explicit' }) } }
        foreach ($tag in $tags) { if ($seen.Add($tag)) { $rows.Add([pscustomobject]@{ Keyword = $tag; Origin = 'tag' }) } }
        if ($skillId -and $seen.Add($skillId)) { $rows.Add([pscustomobject]@{ Keyword = $skillId; Origin = 'id' }) }
        if ($name) {
            if ($seen.Add($name)) { $rows.Add([pscustomobject]@{ Keyword = $name; Origin = 'name' }) }
            foreach ($part in $name.Split([char[]]$script:NameSeparators)) {
                $token = $part.Trim()
                if ($token.Length -gt 1 -and $seen.Add($token)) {
                    $rows.Add([pscustomobject]@{ Keyword = $token; Origin = 'name-token' })
                }
            }
        }

        # 索引投影 = 运行时索引项实际承载的字段（name + tags + keywords）。
        $indexProjection = (@($name) + $tags + $keywords) -join ' | '

        $entries.Add([pscustomobject]@{
            SkillId      = $skillId
            Name         = $name
            Version      = [string]$manifest.version
            Enabled      = [bool]$manifest.enabled
            Tags         = $tags
            Keywords     = $keywords
            KeywordRows  = $rows.ToArray()
            IndexChars   = $indexProjection.Length
            BodyBytes    = $bodyBytes
            BodyLines    = $bodyLines
            UpdatedAt    = [string]$manifest.updatedAt
        })
    }
    return $entries.ToArray()
}

function Get-KeywordRows {
    <# 展平成 (Keyword, SkillId, Enabled, Category, Origin) 行。 #>
    param([object[]]$Entries)
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($entry in $Entries) {
        foreach ($row in $entry.KeywordRows) {
            $rows.Add([pscustomobject]@{
                    Keyword  = $row.Keyword
                    SkillId  = $entry.SkillId
                    Enabled  = $entry.Enabled
                    Category = Get-KeywordCategory -Origin $row.Origin -Keyword $row.Keyword
                    Origin   = $row.Origin
                })
        }
    }
    return $rows.ToArray()
}

function Get-Root {
    param($Parent, [string]$Node)
    while ($Parent[$Node] -ne $Node) { $Node = $Parent[$Node] }
    return $Node
}

function Get-Families {
    <#
      并查集聚簇：两个启用技能共享 ≥1 个给定关键词即同族。
      传入的 $KeywordRecords 决定聚簇口径（本报告默认只用"语义"行，
      把 D1/D2/D3 噪声排除在外 —— 它们几乎人人都有，算进去会退化成"一个大家族"的假象）。
    #>
    param([object[]]$KeywordRecords, [object[]]$Entries)

    $parent = @{}
    foreach ($entry in $Entries) { if ($entry.Enabled) { $parent[$entry.SkillId] = $entry.SkillId } }

    $byKeyword = @{}
    foreach ($record in $KeywordRecords) {
        if (-not $parent.ContainsKey($record.SkillId)) { continue }
        $key = $record.Keyword.ToLowerInvariant()
        if (-not $byKeyword.ContainsKey($key)) {
            $byKeyword[$key] = New-Object System.Collections.Generic.List[string]
        }
        $byKeyword[$key].Add($record.SkillId)
    }

    foreach ($key in $byKeyword.Keys) {
        $members = $byKeyword[$key]
        if ($members.Count -lt 2) { continue }
        $rootFirst = Get-Root -Parent $parent -Node $members[0]
        for ($i = 1; $i -lt $members.Count; $i++) {
            $rootOther = Get-Root -Parent $parent -Node $members[$i]
            if ($rootOther -ne $rootFirst) { $parent[$rootOther] = $rootFirst }
        }
    }

    $groups = @{}
    foreach ($skillId in $parent.Keys) {
        $root = Get-Root -Parent $parent -Node $skillId
        if (-not $groups.ContainsKey($root)) {
            $groups[$root] = New-Object System.Collections.Generic.List[string]
        }
        $groups[$root].Add($skillId)
    }

    $families = New-Object System.Collections.Generic.List[object]
    foreach ($root in $groups.Keys) {
        $families.Add([pscustomobject]@{ Root = $root; Members = $groups[$root].ToArray() })
    }
    return $families.ToArray()
}

function Get-AsciiTokenEstimate {
    <#
      显式公式的估算：ASCII 4 字符 ≈ 1 token；非 ASCII 1 字符 ≈ 1 token。
      这是估算不是实测 —— 必须与字符数并列展示，不得单独引用。
    #>
    param([string]$Text)
    $ascii = 0
    $nonAscii = 0
    foreach ($ch in $Text.ToCharArray()) {
        if ([int]$ch -lt 128) { $ascii++ } else { $nonAscii++ }
    }
    return [int][Math]::Ceiling($ascii / 4.0) + $nonAscii
}

function Get-IfElse {
    param($Condition, $IfTrue, $IfFalse)
    if ($Condition) { return $IfTrue }
    return $IfFalse
}

function New-PortfolioReport {
    param([object[]]$Entries, [int]$TopOffenders)

    $lines = New-Object System.Collections.Generic.List[string]
    $enabled = @($Entries | Where-Object { $_.Enabled })
    $disabled = @($Entries | Where-Object { -not $_.Enabled })
    $rows = Get-KeywordRows -Entries $Entries
    $enabledRows = @($rows | Where-Object { $_.Enabled })

    $lines.Add('# 技能组合盘点（G1 · 只读）')
    $lines.Add('')
    $lines.Add("生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
    $lines.Add("技能根目录：$SkillsRoot")
    $lines.Add('')
    $lines.Add('> 只读产出：未写任何技能文件、未改 manifest、未动 enabled 状态。')
    $lines.Add('> 关键词口径**逐字对齐既有实现** CollectKeywords（`Source/PuddingRuntime/Services/Skills/SkillEnforcerService.cs:126`）：')
    $lines.Add('> `Keywords → Tags → SkillId → Name → Name 分词`，全部 `OrdinalIgnoreCase` 去重。')
    $lines.Add('')

    # ── 1. 存量 ────────────────────────────────────────────────────────────
    $lines.Add('## 1. 存量')
    $lines.Add('')
    $lines.Add('| 指标 | 值 |')
    $lines.Add('|------|----|')
    $lines.Add("| 技能总数 | $($Entries.Count) |")
    $lines.Add("| 启用 | $($enabled.Count) |")
    $lines.Add("| 禁用 | $($disabled.Count) |")
    $lines.Add("| 关键词槽位（启用技能，含重复，小写归一后计） | $($enabledRows.Count) |")
    $distinctEnabled = @($enabledRows | ForEach-Object { $_.Keyword.ToLowerInvariant() } | Sort-Object -Unique)
    $lines.Add("| 去重后关键词 | $($distinctEnabled.Count) |")
    if ($enabledRows.Count -gt 0) {
        $dupRatio = [Math]::Round(100.0 * ($enabledRows.Count - $distinctEnabled.Count) / $enabledRows.Count, 1)
        $lines.Add("| 重复占用比（槽位中属于「第二次及以后」声明的比例） | $dupRatio% |")
    }
    $lines.Add('')

    # ── 2. 关键词空间：噪声占比 ───────────────────────────────────────────
    $lines.Add('## 2. 关键词空间：噪声占比')
    $lines.Add('')
    $lines.Add('| 类别 | 判定规则 | 槽位（全部技能） | 占比 |') 
    $lines.Add('|------|----------|------------------|------|')
    $totalSlots = $rows.Count
    $categoryDefs = @(
        @{ Code = 'D1'; Label = 'D1 治理/溯源标签'; Rule = '来自 manifest.tags（精确分类）' },
        @{ Code = 'D2'; Label = 'D2 工具名'; Rule = 'snake_case 形态 + 显式裸名清单' },
        @{ Code = 'D3'; Label = 'D3 名称分词'; Rule = 'Name 切分出的片段' },
        @{ Code = '语义'; Label = '语义关键词'; Rule = '显式 keywords / Name 全句 / SkillId' }
    )
    foreach ($def in $categoryDefs) {
        $subset = @($rows | Where-Object { $_.Category -eq $def.Code })
        $pct = Get-IfElse -Condition ($totalSlots -gt 0) -IfTrue ([Math]::Round(100.0 * $subset.Count / $totalSlots, 1)) -IfFalse 0
        $lines.Add("| $($def.Label) | $($def.Rule) | $($subset.Count) | $pct% |")
    }
    $noiseCount = @($rows | Where-Object { $_.Category -ne '语义' }).Count
    $noisePct = Get-IfElse -Condition ($totalSlots -gt 0) -IfTrue ([Math]::Round(100.0 * $noiseCount / $totalSlots, 1)) -IfFalse 0
    $lines.Add('')
    $lines.Add("**结论**：$totalSlots 个关键词槽位中 **$noiseCount 个（$noisePct%）** 与「技能教了什么」无关（D1+D2+D3）。")
    $lines.Add('')

    $lines.Add('### 2.1 占用最多的单个关键词')
    $lines.Add('')
    $lines.Add('| 关键词 | 类别 | 声明的技能数 |')
    $lines.Add('|--------|------|--------------|')
    foreach ($group in ($rows | Group-Object { $_.Keyword.ToLowerInvariant() } | Sort-Object Count -Descending | Select-Object -First $TopOffenders)) {
        $sample = $group.Group[0]
        $lines.Add("| $($sample.Keyword) | $($sample.Category) | $($group.Count) |")
    }
    $lines.Add('')

    # ── 3. 冲突：谁实际能命中 ─────────────────────────────────────────────
    $conflictGroups = $enabledRows | Group-Object { $_.Keyword.ToLowerInvariant() } | Where-Object { $_.Count -ge 2 }
    $lostOpportunities = 0
    foreach ($group in $conflictGroups) { $lostOpportunities += ($group.Count - 1) }

    $lines.Add('## 3. 关键词冲突：被静默挤掉的注入机会')
    $lines.Add('')
    $lines.Add('**结构根因**（既有实现，非本报告推测）：`SkillEnforcerService.GetOrRefreshKeywordMapAsync` 建映射时')
    $lines.Add('`if (!map.ContainsKey(kw)) map[kw] = entry.SkillId;` 是**先到先得** —— 同一关键词被多个技能声明时，')
    $lines.Add('只有索引顺序里的第一个能通过该关键词被注入，其余**静默失去这次注入机会**（技能仍启用，效果被抵消）。')
    $lines.Add('')
    $lines.Add("| 指标 | 值 |")
    $lines.Add('|------|----|')
    $lines.Add("| 被 ≥2 个启用技能共享的关键词 | $($conflictGroups.Count) / $($distinctEnabled.Count) |")
    $lines.Add("| **被挤掉的注入机会总数**（Σ 声明数-1） | **$lostOpportunities** |")
    $lines.Add('')
    $lines.Add('> 表中"实际命中者"按 **skillId 序**取第一个 —— 这是**近似**：真实顺序由 SkillEnforcer 的索引构造顺序决定，')
    $lines.Add('> 本报告不读取运行时索引，故不声称精确。但**被挤掉的机会数**与顺序无关，是确定值。')
    $lines.Add('')
    $lines.Add('| 共享关键词 | 类别 | 声明数 | 实际命中者（近似） | 被挤掉 |')
    $lines.Add('|------------|------|--------|--------------------|--------|')
    foreach ($group in ($conflictGroups | Sort-Object Count -Descending | Select-Object -First $TopOffenders)) {
        $sample = $group.Group[0]
        $winner = @($group.Group | ForEach-Object { $_.SkillId } | Sort-Object)[0]
        $lines.Add("| $($sample.Keyword) | $($sample.Category) | $($group.Count) | $winner | $($group.Count - 1) |")
    }
    $lines.Add('')

    # ── 4. 家族分布 ───────────────────────────────────────────────────────
    $lines.Add('## 4. 家族分布（按语义关键词聚簇）')
    $lines.Add('')
    $lines.Add('聚簇规则：两个启用技能共享 ≥1 个**语义类**关键词即同族（并查集）。')
    $lines.Add('刻意排除 D1/D2/D3 —— 它们几乎人人都有，算进去会退化成"一个大家族"的假象，所以并列给出含噪声口径作对照。')
    $lines.Add('')
    $semanticRows = @($rows | Where-Object { $_.Category -eq '语义' })
    $semanticFamilies = Get-Families -KeywordRecords $semanticRows -Entries $Entries
    $allFamilies = Get-Families -KeywordRecords $rows -Entries $Entries

    $lines.Add('| 口径 | 家族数 | 最大族 | 成员 ≥2 的族 | 孤立技能（族大小=1） |')
    $lines.Add('|------|--------|--------|--------------|----------------------|')
    foreach ($view in @(
            @{ Label = '仅语义关键词'; Families = $semanticFamilies },
            @{ Label = '含全部关键词（噪声危害对照）'; Families = $allFamilies })) {
        $sizes = @($view.Families | ForEach-Object { $_.Members.Count } | Sort-Object -Descending)
        $maxSize = Get-IfElse -Condition ($sizes.Count -gt 0) -IfTrue $sizes[0] -IfFalse 0
        $multi = @($sizes | Where-Object { $_ -ge 2 }).Count
        $singles = @($sizes | Where-Object { $_ -eq 1 }).Count
        $lines.Add("| $($view.Label) | $($view.Families.Count) | $maxSize | $multi | $singles |")
    }
    $lines.Add('')

    $lines.Add('### 4.1 最大的 10 个语义家族')
    $lines.Add('')
    $lines.Add('| 族内技能数 | 成员 |')
    $lines.Add('|------------|------|')
    foreach ($family in ($semanticFamilies | Sort-Object { $_.Members.Count } -Descending | Select-Object -First 10)) {
        $members = @($family.Members | Sort-Object)
        $lines.Add("| $($members.Count) | $($members -join ' / ') |")
    }
    $lines.Add('')

    # ── 5. 成本 ───────────────────────────────────────────────────────────
    $lines.Add('## 5. 成本：索引投影 与 注入体量')
    $lines.Add('')
    $indexProjection = @($enabled | ForEach-Object { "$($_.Name) | $($_.Tags -join ',') | $($_.Keywords -join ',')" }) -join "`n"
    $indexChars = $indexProjection.Length
    $indexTokens = Get-AsciiTokenEstimate -Text $indexProjection
    $bodyTotal = ($enabled | Measure-Object -Property BodyBytes -Sum).Sum
    $avgBody = Get-IfElse -Condition ($enabled.Count -gt 0) -IfTrue ([Math]::Round($bodyTotal / $enabled.Count)) -IfFalse 0

    $lines.Add('| 指标 | 值 | 说明 |')
    $lines.Add('|------|----|------|')
    $lines.Add("| 索引投影字符数（启用技能） | $indexChars | name + tags + keywords 拼接，**确定值** |")
    $lines.Add("| 索引投影 token 估算 | ≈ $indexTokens | 公式 ASCII/4 + 非ASCII/1，**估算值** |")
    $lines.Add("| 全量 SKILL.md 字节（启用技能） | $bodyTotal | 若全部注入的体量上界 |")
    $lines.Add("| 平均单技能 SKILL.md 字节 | $avgBody | 单次注入的典型量级 |")
    $lines.Add('')
    $lines.Add('**体量最大的 10 个启用技能**（单次注入成本最高者）：')
    $lines.Add('')
    $lines.Add('| 技能 | 字节 | 行数 | 关键词槽位 |')
    $lines.Add('|------|------|------|------------|')
    foreach ($entry in ($enabled | Sort-Object BodyBytes -Descending | Select-Object -First 10)) {
        $lines.Add("| $($entry.SkillId) | $($entry.BodyBytes) | $($entry.BodyLines) | $($entry.KeywordRows.Count) |")
    }
    $lines.Add('')

    # ── 6. 淘汰候选代理指标 ───────────────────────────────────────────────
    $semanticBySkill = @{}
    foreach ($row in $semanticRows) {
        if (-not $semanticBySkill.ContainsKey($row.SkillId)) {
            $semanticBySkill[$row.SkillId] = New-Object System.Collections.Generic.List[string]
        }
        $semanticBySkill[$row.SkillId].Add($row.Keyword)
    }
    $noSemantic = @($enabled | Where-Object { -not $semanticBySkill.ContainsKey($_.SkillId) })
    $thinSemantic = @($enabled | Where-Object {
            $semanticBySkill.ContainsKey($_.SkillId) -and $semanticBySkill[$_.SkillId].Count -le 1
        })

    $lines.Add('## 6. 淘汰候选**代理指标**（不是判决）')
    $lines.Add('')
    $lines.Add('这些是**代理指标，不是判决**：本报告不使用遥测（谁真正被命中、命中后是否有用），')
    $lines.Add('只能指出"命中能力最弱"的技能，**不能**据此淘汰任何技能。真正的淘汰判据要等 G2 使用遥测。')
    $lines.Add('')
    $lines.Add('| 代理指标 | 技能数 | 含义 |')
    $lines.Add('|----------|--------|------|')
    $lines.Add("| 零语义关键词 | $($noSemantic.Count) | 只能靠工具名/标签/名字分词命中 ⇒ 不是可复用的程序知识 |")
    $lines.Add("| 语义关键词 ≤ 1 | $($thinSemantic.Count) | 命中面极窄 ⇒ 与噪声无法区分 |")
    $lines.Add('')
    if ($noSemantic.Count -gt 0) {
        $lines.Add('零语义关键词的启用技能（最多列 20 个）：')
        $lines.Add('')
        foreach ($entry in ($noSemantic | Select-Object -First 20)) {
            $lines.Add("- $($entry.SkillId)")
        }
        $lines.Add('')
    }

    # ── 7. 方法与边界 ─────────────────────────────────────────────────────
    $lines.Add('## 7. 方法与边界（读报告时必看）')
    $lines.Add('')
    $lines.Add('- **数据源**：每技能目录的 `manifest.json`（skillId/name/tags/keywords/enabled）与 `SKILL.md` 字节数。')
    $lines.Add('- **关键词口径**：逐字复现 `CollectKeywords`（含 Tags 与 Name 分词、OrdinalIgnoreCase 去重）。')
    $lines.Add('- **D2 判定**：snake_case 形态规则 + 显式补充的裸名工具清单（shell/sleep/asr）；可能有遗漏 ⇒ D2 只低估不高估。')
    $lines.Add('- **确定值**：存量、分类、共享统计、被挤掉机会数、字节数、字符数 —— 均可由本脚本复现。')
    $lines.Add('- **估算项**：token 数（公式 ASCII/4 + 非ASCII/1）。真实值取决于 tokenizer，**不得**当实测值引用。')
    $lines.Add('- **近似项**："实际命中者"按 skillId 序取第一；真实顺序由运行时索引构造决定。被挤掉的机会数不受此影响。')
    $lines.Add('- **未覆盖**：不读取运行时日志 ⇒ 不含"实际注入次数 / 命中次数"（那是 G2 使用遥测的范围）。')
    $lines.Add('- **不改状态**：全程只读；运行前后技能文件与 enabled 状态必须逐字节一致。')
    $lines.Add('')
    return ($lines -join "`n")
}

# ── 主流程 ────────────────────────────────────────────────────────────────
function Get-ScenarioMetrics {
    <#
      某个「来源白名单 + 类别排除」口径下的组合指标。
      ⭐ 决定性指标是 ZeroKeywordSkills：该口径下**再也无法被任何关键词命中**的技能数
      —— 去噪不能把技能变成死数据。
    #>
    param([object[]]$Rows, [object[]]$Enabled, [string[]]$OriginWhitelist, [string[]]$ExcludedCategories)

    $kept = New-Object System.Collections.Generic.List[object]
    foreach ($row in $Rows) {
        if (-not $row.Enabled) { continue }
        if ($OriginWhitelist -notcontains $row.Origin) { continue }
        if ($ExcludedCategories -contains $row.Category) { continue }
        $kept.Add($row)
    }
    $keptRows = $kept.ToArray()

    $distinctKeys = @($keptRows | ForEach-Object { $_.Keyword.ToLowerInvariant() } | Sort-Object -Unique)
    $shared = @($keptRows | Group-Object { $_.Keyword.ToLowerInvariant() } | Where-Object { $_.Count -ge 2 })
    $lost = 0
    foreach ($group in $shared) { $lost += ($group.Count - 1) }

    $reachable = @{}
    foreach ($row in $keptRows) { $reachable[$row.SkillId] = $true }
    $zero = 0
    foreach ($entry in $Enabled) {
        if (-not $reachable.ContainsKey($entry.SkillId)) { $zero++ }
    }

    $avg = 0
    if ($Enabled.Count -gt 0) { $avg = [Math]::Round($keptRows.Count / $Enabled.Count, 2) }

    return [pscustomobject]@{
        Slots             = $keptRows.Count
        Distinct          = $distinctKeys.Count
        Shared            = $shared.Count
        LostOpportunities = $lost
        ZeroKeywordSkills = $zero
        AvgPerSkill       = $avg
    }
}

function New-DenoiseImpactReport {
    <#
      G5-a：去噪口径**预演**（只读）。逐场景给出组合指标，重点回答两件事：
        ① 能恢复多少次被挤掉的注入机会（收益）；
        ② 会让多少技能变成「零关键词」死数据（唯一会伤到能力的风险）。
      本函数**不改任何行为**：不写技能文件、不改 CollectKeywords、不动 enabled。
    #>
    param([object[]]$Entries)

    $lines = New-Object System.Collections.Generic.List[string]
    $enabled = @($Entries | Where-Object { $_.Enabled })
    $rows = Get-KeywordRows -Entries $Entries

    $allOrigins = @('explicit', 'tag', 'id', 'name', 'name-token')
    $scenarios = @(
        @{ Id = 'S0'; Label = '现状（全部来源，无过滤）'; Origins = $allOrigins; Excluded = @() },
        @{ Id = 'S1'; Label = '去 D1：丢掉 Tags 来源'; Origins = @('explicit', 'id', 'name', 'name-token'); Excluded = @() },
        @{ Id = 'S2'; Label = '去 D1+D2：再去掉工具名'; Origins = @('explicit', 'id', 'name', 'name-token'); Excluded = @('D2') },
        @{ Id = 'S3'; Label = '去 D1+D2+D3：只留语义'; Origins = @('explicit', 'id', 'name'); Excluded = @('D2') },
        @{ Id = 'S4'; Label = '仅显式 keywords 且去噪'; Origins = @('explicit'); Excluded = @('D2') },
        @{ Id = 'S5'; Label = '仅显式 keywords（原样）'; Origins = @('explicit'); Excluded = @() }
    )

    $results = New-Object System.Collections.Generic.List[object]
    $baselineLost = 0
    foreach ($scenario in $scenarios) {
        $metrics = Get-ScenarioMetrics -Rows $rows -Enabled $enabled -OriginWhitelist $scenario.Origins -ExcludedCategories $scenario.Excluded
        if ($scenario.Id -eq 'S0') { $baselineLost = $metrics.LostOpportunities }
        $results.Add([pscustomobject]@{ Id = $scenario.Id; Label = $scenario.Label; Metrics = $metrics })
    }

    $lines.Add('# G5-a 去噪影响评估（只读预演）')
    $lines.Add('')
    $lines.Add("生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
    $lines.Add("技能根目录：$SkillsRoot")
    $lines.Add('')
    $lines.Add('> **只读预演**：本报告不改 `CollectKeywords`、不写任何技能文件、不动 enabled 状态。')
    $lines.Add('> 目的：在动生产行为之前，先量出每个候选口径的**收益（恢复的机会）**与**风险（变成零关键词死数据的技能）**。')
    $lines.Add('')
    $lines.Add("基准 S0 = 现状口径，$($enabled.Count) 个启用技能。来源白名单指 `CollectKeywords` 的四类来源（显式 Keywords / Tags / SkillId / Name 与 Name 分词）。")
    $lines.Add('')
    $lines.Add('| 场景 | 口径 | 槽位 | 去重 | 共享关键词 | 被挤掉机会 | 恢复 | 零关键词技能 | 平均每技能 |')
    $lines.Add('|------|------|------|------|------------|------------|------|--------------|------------|')
    foreach ($result in $results) {
        $m = $result.Metrics
        $recovered = $baselineLost - $m.LostOpportunities
        $lines.Add("| $($result.Id) | $($result.Label) | $($m.Slots) | $($m.Distinct) | $($m.Shared) | $($m.LostOpportunities) | $recovered | $($m.ZeroKeywordSkills) | $($m.AvgPerSkill) |")
    }
    $lines.Add('')

    $lines.Add('## 怎么读这张表')
    $lines.Add('')
    $lines.Add('- **恢复** = S0 被挤掉机会 − 本口径被挤掉机会：去噪真正的收益（噪声不再抢占映射位）。')
    $lines.Add('- **零关键词技能** = 该口径下没有任何关键词能把它拉进上下文的技能数。**这是唯一会伤到能力的指标**：')
    $lines.Add('  它意味着「技能还在、但永远不会被触发」。任何口径若让该数显著大于 0，就必须先为这些技能补写**语义**关键词，')
    $lines.Add('  否则等同于**静默停用**一批技能——这正是本评估要拦住的事故。')
    $lines.Add('')

    $safe = @($results | Where-Object { $_.Id -ne 'S0' -and $_.Metrics.ZeroKeywordSkills -eq 0 })
    if ($safe.Count -eq 0) {
        $lines.Add('### ⚠️ 结论：没有「零风险」的候选口径')
        $lines.Add('')
        $lines.Add('**没有任何候选口径能在不产生死数据的前提下完成去噪。**')
        $lines.Add('因此 G5 的实施不能只做「过滤关键词来源」，必须配套：')
        $lines.Add('')
        $lines.Add('1. 先为「零关键词技能」补写语义关键词（人工或由整理作业提炼）；')
        $lines.Add('2. 再切换口径，并以**注入行为对比**作为回归证据（同一条输入下，注入的技能集合变化必须逐条可解释）；')
        $lines.Add('3. 保留一键回滚（口径开关走配置，不改结构、可即时退回现状）。')
    }
    else {
        $lines.Add('### 无「零关键词」风险的候选口径')
        $lines.Add('')
        $lines.Add('| 场景 | 口径 | 恢复 | 被挤掉机会 | 平均每技能 |')
        $lines.Add('|------|------|------|------------|------------|')
        foreach ($result in $safe) {
            $m = $result.Metrics
            $lines.Add("| $($result.Id) | $($result.Label) | $($baselineLost - $m.LostOpportunities) | $($m.LostOpportunities) | $($m.AvgPerSkill) |")
        }
        $lines.Add('')
        $lines.Add('选择仍须由人/判据决定：本报告只提供事实，不给建议值。')
    }
    $lines.Add('')

    $lines.Add('### ⚠️ 重要：上面的「零关键词技能 = 0」很可能是**假安全**')
    $lines.Add('')
    $lines.Add('该指标只检查「是否还剩至少一个关键词」，**不检查剩下的关键词是否可能出现在真实输入里**。')
    $lines.Add('S2/S3 剩余的关键词主要是 `Name` 全句与 `SkillId` 这类 slug（例如 `agent-repo-health-check`），')
    $lines.Add('它们几乎不会出现在用户文本中 ⇒ 技能「有键但永不命中」，等价于静默停用。')
    $lines.Add('')
    $lines.Add('**这说明单靠关键词形态过滤无法证明去噪安全。** 要回答真正的问题——')
    $lines.Add('「现在到底是哪些关键词在真实触发注入？」——必须有**使用遥测（G2）**，本报告回答不了。')
    $lines.Add('')
    $lines.Add('### 由此得到的重述：去噪不是「删关键词」，而是「给关键词定主」')
    $lines.Add('')
    $lines.Add('1735 次被挤掉的机会，本质是**共享关键词的归属未裁决**（`map[kw]` 先到先得），')
    $lines.Add('而不是「关键词太多」。因此正确动作可能是：')
    $lines.Add('')
    $lines.Add('1. **保留**有真实命中的关键词（哪怕它是工具名），但让它**有主**——同一关键词只归属一个技能；')
    $lines.Add('2. 被挤掉的技能改用**各自的语义关键词**（而非共享的工具名）来触发；')
    $lines.Add('3. 只有确认「无命中、且无主」的关键词才删。')
    $lines.Add('')
    $lines.Add('⚠️ 走哪条路**取决于 G2 遥测**：若真实注入主要靠 D2 工具名命中，则 S2/S3 会**直接切断现有注入路径**，')
    $lines.Add('那是比噪声更严重的能力回退。⇒ **G5 的实施前置条件是 G2 遥测**：先有命中事实，再谈删或定主。')
    $lines.Add('')

    $lines.Add('## 方法与边界')
    $lines.Add('')
    $lines.Add('- **只读**：全程不写技能文件、不改 manifest、不动 enabled。')
    $lines.Add('- **口径复用**：与 G1 报告共用同一份 `CollectKeywords` 复现逻辑（同一脚本的 `-Mode portfolio`），避免两处真相各自漂移。')
    $lines.Add('- **确定值**：槽位、去重、共享数、被挤掉机会、零关键词技能数、平均每技能数。')
    $lines.Add('- **未覆盖**：本报告不模拟「用户输入命中哪几个关键词」，因此**不能**回答「某个具体输入下注入集合如何变化」；')
    $lines.Add('  那是 G5-b 实施时必须补的注入行为对比（需要运行时链路）。')
    $lines.Add('- **不改判据**：本报告不设定任何阈值，不宣布哪个口径「正确」。')
    $lines.Add('')

    foreach ($result in $results) {
        $m = $result.Metrics
        Write-Host ("SCENARIO {0} slots={1} distinct={2} shared={3} lost={4} recovered={5} zeroKeyword={6}" -f `
                $result.Id, $m.Slots, $m.Distinct, $m.Shared, $m.LostOpportunities, ($baselineLost - $m.LostOpportunities), $m.ZeroKeywordSkills)
    }

    return ($lines -join "`n")
}

$entries = Get-SkillPortfolio -Root $SkillsRoot
if ($Mode -eq 'denoise-impact') {
    $report = New-DenoiseImpactReport -Entries $entries
}
else {
    $report = New-PortfolioReport -Entries $entries -TopOffenders $TopOffenders
}

if ($OutFile) {
    $parent = Split-Path -Parent $OutFile
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($OutFile, $report, $utf8NoBom)
    Write-Host "REPORT WRITTEN: $OutFile"
}

$rows = Get-KeywordRows -Entries $entries
$enabledRows = @($rows | Where-Object { $_.Enabled })
$enabledCount = @($entries | Where-Object { $_.Enabled }).Count
$distinct = @($enabledRows | ForEach-Object { $_.Keyword.ToLowerInvariant() } | Sort-Object -Unique).Count
$conflicts = @($enabledRows | Group-Object { $_.Keyword.ToLowerInvariant() } | Where-Object { $_.Count -ge 2 })
$lost = 0
foreach ($group in $conflicts) { $lost += ($group.Count - 1) }
$semanticFamilies = Get-Families -KeywordRecords @($rows | Where-Object { $_.Category -eq '语义' }) -Entries $entries
$famSizes = @($semanticFamilies | ForEach-Object { $_.Members.Count } | Sort-Object -Descending)

Write-Host "SKILLS total=$($entries.Count) enabled=$enabledCount disabled=$($entries.Count - $enabledCount) mode=$Mode"
Write-Host "KEYWORDS slots=$($enabledRows.Count) distinct=$distinct conflicts=$($conflicts.Count) lostOpportunities=$lost"
foreach ($code in @('D1', 'D2', 'D3', '语义')) {
    $count = @($rows | Where-Object { $_.Category -eq $code }).Count
    Write-Host ("CATEGORY {0} slots={1}" -f $code, $count)
}
Write-Host "FAMILIES semantic-only=$($semanticFamilies.Count) max=$(Get-IfElse -Condition ($famSizes.Count -gt 0) -IfTrue $famSizes[0] -IfFalse 0)"
