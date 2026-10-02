# 从 Source/PuddingCodeIndexTests/code_map.md 迁出的历史变更记录（迁出日 2026-10-02）

> **为什么在这里**：`Source/PuddingCodeIndexTests/code_map.md` 只保留索引（关键概念 · 组件 · 关键文件 · 用途）。原先按轮次/日期堆叠在其中的变更、门禁与验收记录迁出到本文件。日志规则见 `Docs/00_changelog/README.md`。
>
> **内容来源**：迁出前 `Source/PuddingCodeIndexTests/code_map.md` 的原文，**逐字保留，未做删改**（节之间仅插入 `---` 分隔，不改变任何原文字）。原文件快照可 `git show <迁出前提交>:Source/PuddingCodeIndexTests/code_map.md`。
> **路径约定**：链接目标已改写为**相对本文件**的可点击路径（链接文字未变）；正文反引号内的路径仍保持原文的仓库根口径。
> **覆盖范围**：原文件第 58–79 行，共 1 节；日期 2 ~ 4。

**〔原文第 58–79 行：U4-2a 更新（2026-09-24）— 检索意图与结果合同的契约测试（82 → 98 用例）〕**

## U4-2a 更新（2026-09-24）— 检索意图与结果合同的契约测试（82 → 98 用例）

**新增 10 文件（`Contracts/Retrieval/`）**：新增 **16 个用例**，全部只引用 `PuddingCodeIndex`（边界未击穿）。

| 文件 | 覆盖 |
|------|------|
| `RetrievalTestData.cs` | 共享构造器（命中/请求/落盘/建议/小体积页）；刻意只依赖本组件 |
| `RetrievalIntentContractTests.cs` | **A01** 缺省即 Auto（`(int)Auto == 0`、`default` 即 Auto、成员集合 12 值冻结）；**A07** 显式 intent 的层序**不被 Auto 改写**（7 层全序 ×12 张表 + 成文表逐条冻结 + 行为证明 + intent→过滤表） |
| `RetrievalResultContractTests.cs` | **A02** "hits=0 无 reason"不可表示（无公开构造函数 + 工厂拒绝 + 反射结构证明）；**A03** 降级必须带原因（反向亦然）；**A04** 至多一条下一步（**语义选"失败"**，不静默截断）；**A09** 不得静默截断（真实总数 + 游标 + 落盘 + 非空分布 + 落盘路径守卫负面对照 + `totalCount` 无默认值） |
| `RetrievalRankingContractTests.cs` | **A05** 显式全序与确定性（末级身份键承重 + 3 次打乱逐位相同 + 逆序必被 `EnsureOrdered` 拒绝）；**A05b** 多 scope 佐证加权确定性/单调/去重 |
| `RetrievalCapabilityContractTests.cs` | **A06** 能力矩阵诚实（不支持 ⇒ 必须给替代方案；端口形状冻结；替换实现替身；结果层不得把能力缺口报成真空/过泛） |
| `RetrievalBudgetContractTests.cs` | **A08** 单次返回有界（条数 ≤ PageSize、字节/token 双预算未超、超预算构造即拒、页大小有界） |
| `RetrievalOverloadContractTests.cs` | **A10** 过载即信号（Low 三原因 + 分布附加信号 + 诊断自洽 + **过载但未截断仍须恰好一条收窄建议** + **③ 误报守卫**：正当的多与紧凑高语义量都不得报 Low）；**A11** 收窄手段恰好四类 + 载荷与种类一致 |
| `RetrievalEmptyReasonContractTests.cs` | **A12** 过滤性空 ≠ 真空（FilteredOut + "放宽哪个面" + 放宽面必须在生效面内 + 带过滤面报"确实不存在"⇒ 拒绝 + 裁决优先级成文） |
| `RetrievalDeduplicationContractTests.cs` | **A13** 跨 scope 去重先于过载判定（**首选主张先断言**：去重前 Low / 去重后不 Low；再去重后计数与 scope 元数据；嵌套/重叠 scope 检测 + 三重负面对照 + 结果构造拒绝未去重序列）；**A14** 双视图各带命中原因摘要 |
| `RetrievalFilterContractTests.cs` | **A15**（额外）过滤面正交且 AND 组合：**只关注类名称 ⇒ 签名文本命中的 `.ctor` 被过滤掉**（§2.3 实测动机）；目录/扩展名/置信度/命中层各面；空洞阀门（`None` 匹配域、空扩展名、关递归无目录）；规范化与回显；生效面顺序成文 |

**变异取红（M1~M9，每项均已复原且 `git hash-object` 与变异前逐位相同）**：M1→A01、M2→A02、M3→A04、M4→A05、M5→A09、M6→A09、M7→**A10③ 误报守卫**、M8→**A13③**、M9→A12；每次取红只有目标用例变红（97/98），复原后 98/98。

**运行**：`dotnet test Source\PuddingCodeIndexTests\PuddingCodeIndexTests.csproj`（无需宿主、不加载 Roslyn/MSBuild）。

> ⚠️ 复原文件时若用 `Copy-Item` 覆盖，PowerShell 会保留**备份文件的旧时间戳** ⇒ MSBuild 认为源文件比输出更旧而**跳过重编译**，于是"复原后跑测试"会读到上一次变异的 DLL（本刀实测踩到：`u4-2a-mut-M1-green*.txt`）。复原后必须刷新 `LastWriteTime`（或清 `bin/obj`）再判定绿。

