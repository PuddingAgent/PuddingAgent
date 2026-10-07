---
title: "code_map 规范 v2（浓缩优先 / 机器可校验）"
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: active
description: 定义 code_map.md 的字段 schema、硬上限、角色分层、反模式词表与陈旧判据，目标是把 code_map 从「正文堆积」压回「可导航索引」。
categories: [docs, conventions]
tags: [code-map, conventions, index, condensation, machine-checkable]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-07-code_map规范v2与自检.md]
related_files: [code_map.md, Tools/Docs/code_map_check.py]
slug: code-map-spec-v2
draft: false
---

# code_map 规范 v2

> 立法理由（实测基线，2026-10-07）：全仓 **40 个活跃 `code_map.md`，385,559 B / 3,029 行**，其中 **≈50.7% 是 Markdown 表格行**；
> 最坏症状是 **单行 3,521 B**、**根 `code_map.md` §4 存在一个 98 行代码围栏（占该文件 34%）**、**同一表头 `| 文件 | 用途 |` 重复 98 次**。
> 成因链：*「按文件一行」模板 × 无字数上限 → 单元格越写越长 → 索引退化为正文*。
> 本规范借鉴 **AOCI-CODE**（`aoci-spec/aoci-code`，AI-Oriented Code Indexing）的**省略式压缩 + 硬上限 + 分档配额 + 角色分层**纪律，
> 但**保留 Markdown 表格与相对链接**（本仓 code_map 的可导航性依赖链接，不整体改用其 `文件名[标签]: F|R|A|S` 纯文本行式）。
> 借鉴来源、原文依据与限制见 `temp/aoci-code-research-2026-10-07.md`（临时件，不入库）。

---

## 1. 目标与判据

**目标**：`code_map.md` 是**索引**，不是正文、不是路线图、不是变更日志。
读者（人或 Agent）应能在**读完之后**知道「该去哪个文件看」，而不是「这个文件里有什么代码」。

**完成判据（缺一不算达标）**

| # | 判据 | 证据形态 |
|---|---|---|
C1 | 每个活跃 `code_map.md` 通过 `python Tools/Docs/code_map_check.py --check <path>` | 退出码 0 + 违规清单为空 |
C2 | 无「正文级」内容（代码围栏 0 个、单行 ≤ 上限） | 检查器汇总中的 `fences=0` 与 `max_line_len` |
C3 | L1/L2 分工不越界（见 §3） | 检查器的 `layering_violation` 计数为 0 |
C4 | 头部携带**源指纹 + 最近整理日期**（见 §5） | 检查器可解析出 `source_fingerprint` |

---

## 2. 条目 schema（每对象一行）

L2（子工程）与 L1（根）的项目表均使用**固定列序**（列序不得变动，便于差分）：

| 列 | 语义 | 必填 | 硬上限 |
|---|---|---|---|
`文件 / 目录` | 仓库相对路径或 `Source/<Proj>/` ，**必须**是相对链接或反引号路径 | 是 | 120 字符 |
`用途`（F） | **一条**职责命题。**不是清单**（不得罗列 3 个以上并列动作） | 是 | **80 字符** |
`关键符号`（A） | 外部稳定依赖的入口（类/方法/端点）**最多 5 项**。**不是所有方法/字段的列表** | 否 | **100 字符 且 ≤5 项** |
`关联`（R） | 强关联对象，**只能是规范化身份**（仓库相对路径 / 符号名 / 端点）。**禁止散文** | 否 | **100 字符 且 ≤5 项** |
`约束`（S） | **不可从代码推断**且**删掉会导致误改**的约束（两个问题都答"是"才写） | 否 | **160 字符** |

**全局硬上限（机器契约，唯一真源在 `Tools/Docs/code_map_check.py` 的常量表）**

| 常量 | 值 | 说明 |
|---|---|---|
`MAX_LINE_LEN` | **300** | 任何一行（含表格行）不得超过 |
`MAX_ENTRY_ROWS` | **400** | 单个 code_map 的条目行上限，超出即需拆分到 L2 / 角色分层 |
`MAX_FENCES` | **0** | **禁止任何代码围栏**（` ``` `）——索引不是正文 |
`MAX_TABLE_HEADER_REPEAT` | **8** | 同一表头重复次数上限（超出说明该拆表） |
`MAX_FIELD_LEN` | 见上表 | 按列名匹配 |

> ⚠️ **上限是天花板，不是目标**。写不满就不要填满；为凑字数而扩写属于反模式（§6）。
> ⚠️ **超限必须报错，严禁静默截断**。任何工具**不得**自动改写、压缩、截断或重排语义字段；
> 只允许 **read / 统计 / 校验 / 报告**。修复由**模型阅读代码后手写**。
> 依据：AOCI `aoci-object-fras-v2.txt` §1 规定程序 *must not … generate, prefill, summarize, compress, truncate, delete, reorder, rewrite, or complete F/R/A/S*。

---

## 3. 分层职责（L1 / L2 边界）

| 层 | 位置 | **只**放什么 | **禁止**放什么 |
|---|---|---|---|
**L1 根** | `{repo}/code_map.md` | ① 怎么用（≤15 行）② 项目定位 ③ **子项目索引（链接）** ④ 跨项目关键调用链（**以表格表达，每步一行**）⑤ 测试工程索引 ⑥ 架构文档索引 ⑦ 运行时目录与构建入口 | ❌ 单个项目内部的类/方法细节 ❌ **任何代码围栏** ❌ 逐文件清单 |
**L2 子** | `Source/<Proj>/code_map.md` | ① 一句话定位 + 技术栈 ② 入口与配置 ③ 核心功能（**每文件一行**，用 §2 schema）④ 该项目自己的约束/坑 | ❌ 跨项目调用链 ❌ 文档索引 ❌ 运行时目录 ❌ **任何代码围栏** |

**判定规则（机器可查）**：若 L1 中某行引用了 `Source/<Proj>/` 之下的具体文件名 **且** 该行承载了该文件的职责描述 ⇒ `layering_violation`（该细节属于 L2）。

---

## 4. 角色分层（决定"什么值得占一行"）

每个被 code_map 覆盖的对象必须归入下列之一。**默认最外层**：不确定时归 `observe`，而不是 `index`。

| 角色 | 含义 | 是否占用条目行 |
|---|---|---|
`index` | 产品代码、入口、契约、对外可依赖的稳定面 | ✅ 需要一行语义条目 |
`observe` | 生成物、测试夹具/基准语料、大资源、供应商冻结件 —— **只登记路径**，不写语义 | ❌ 不占条目行（可在附录列路径） |
`exclude` | `bin/` `obj/` `node_modules/` `.pudding/` `temp/` `dist/` `dist-dev/` `external/references/` | ❌ 不读不写 |

**成本判据**：一条条目约 **100~250 token**。当某对象的语义价值低于这个成本（例如"再来一个 markdown 夹具"），它属于 `observe`。
依据：AOCI `AGENTS.md` *"at roughly 110 tokens per Entry, indexing them would more than double the Whole-Index and buy nothing"*。

---

## 5. 陈旧判据（把"该更新了"变成可判定谓词）

每个 `code_map.md` **头部必须**携带一行机器可解析的元数据：

```
> 源指纹: <path-glob>=<sha256 前 12 位>[, …] · 条目数: <N> · 最近整理: YYYY-MM-DD
```

- **源指纹**：对该 code_map 覆盖范围的源码文件（按 §4 的 `index`+`observe` 集合，排除 `exclude`）逐文件内容哈希后聚合（文件数 + 排序后拼接的 sha256 前 12 位）。
  **不得**用 mtime（本仓实测：技能/文档目录 mtime 会被批量重写，mtime 不可作时代或新鲜度代理）。
- **触发整理**：满足任一即需 refresh —— ① 源指纹不符 ② 自上次整理以来的**语义变更数 ≥ 30** ③ 检查器报 `stale`。
- **整理时**：必须**重新读源码**生成条目；**禁止**用脚本"更新一下数字"。

---

## 6. 反模式词表（机器可执行，检查器必须实现）

| 类别 | 词/形态 | 处置 |
|---|---|---|
演进叙事 | `本次`、`新增了`、`修复了`、`改为`、`原来是`、`此前是`、`之前是`、`现在改`、`已迁移`、`TODO` | ⚠️ warning（历史应从 `Docs/00_changelog/` 读） |
过度宣称 | `零缺陷`、`彻底解决`、`完全杜绝`、`100%`、`O(1)`、`确定性检索`、`单一真源`、`保证不` | ⚠️ warning |
清单型 F | `用途` 单元格内 `、`/`,` 分隔的并列短语 **≥3** 个 | ⚠️ warning（应改写成一条命题） |
复述型 S | `约束` 与 `用途` 的字符 Jaccard ≥ 0.5 | ⚠️ warning（零熵） |
空值写法 | `none`、`N/A`、`无`、`-` 之外的占位 | ℹ️ info（空字段统一写 `—`） |
路线图 | `未来`、`计划`、`将支持`、`下一版` | ⚠️ warning（路线图属设计文档，不属索引） |
| 逐文件免检旁路 | 为"每次构建都变字节"的对象加特例跳过 | ⚠️ warn（**不由机器判定**：从 code_map 本身看不出脚本是否加了旁路 ⇒ 归入 `anti-pattern` 的 warn 级，只作 review 检查项；正确处置是改为 `observe`，见 §4） |

---

## 7. 自检与门禁

```
python Tools/Docs/code_map_check.py --check                 # 全仓，非零退出即门禁不过
python Tools/Docs/code_map_check.py --check --paths code_map.md
python Tools/Docs/code_map_check.py --report out.json       # 机器可读报告
```

| 规则 ID | 等级 | 判据 |
|---|---|---|
`line-too-long` | error | 任一行 > `MAX_LINE_LEN` |
`field-too-long` | error | 任一单元格 > 该列 `MAX_FIELD_LEN` |
`field-too-many-items` | error | `关键符号` / `关联` > 5 项 |
`fence-forbidden` | error | 出现代码围栏 |
`entries-exceed` | error | 条目行 > `MAX_ENTRY_ROWS` |
`header-repeat` | warn | 同表头重复 > `MAX_TABLE_HEADER_REPEAT` |
`layering-violation` | **warn** | 违反 §3 边界（**启发式，非可判定**：L1 行首单元格匹配 `Source/<Proj>/<file>.<ext>` 即提示该文件细节应在 L2；由于 L1 的跨项目调用链表也会引用文件路径，本规则**故意定为 warn**，不阻断门禁） |
`missing-fingerprint` | warn | 缺少 §5 头部元数据 |
`stale-fingerprint` | warn | 源指纹与磁盘不符 |
`anti-pattern` | warn | 命中 §6 词表 |
`link-broken` | error | 相对链接目标不存在 |

**门禁口径**：`error` 计数必须为 0；`warn` 允许存在但必须在报告中逐条列出（不许折叠）。
**禁止**为让检查器变绿而调高上限或加白名单 —— 调参属于规范变更，须走本文件的修订流程。

---

## 8. 修订流程

1. 上限/常量的唯一真源是 `Tools/Docs/code_map_check.py` 的 `LIMITS` 常量表；本文件的表格是**它的解释**，冲突时以代码为准。
2. 调整上限必须：给出**实测分布**（当前中位数/分位数）与**信息损失评估**，并在 changelog 记录理由。
3. 首次落地时，上限按「先杀掉最坏 1%（>1KB 单行、代码围栏），再据实测分布收紧」的次序推进 —— **不追求一步到位**。

---

## 9. 未纳入（诚实标注）

- **未采用** AOCI 的五维紧凑标签（`[CG9L]`）与 `F|R|A|S` 纯文本行式：本仓 code_map 的核心价值之一是 Markdown **相对链接**（人可直接跳转），改用纯文本会失去该导航能力。二者的"浓缩纪律"已吸收，**格式未照搬**。
- **未采用** 其 MCP/CLI 治理面（Baseline 批次、Attestation、two-question probe）：本仓无对应宿主集成，v2 只保留**可离线判定**的源指纹。
- 上限数值（300/80/100/160/400/8）是**首版工程取值**，尚未据本仓真实分布校准 —— 见 §8 第 3 条，属**待校准**项，不是"最优值"。
