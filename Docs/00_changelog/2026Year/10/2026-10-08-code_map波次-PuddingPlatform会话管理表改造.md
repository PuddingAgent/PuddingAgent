---
title: 2026-10-08 PuddingPlatform code_map 会话管理表列式改造（波次 1-2/15 表：error 118 → 101）
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: active
description: "把 Source/PuddingPlatform/code_map.md 的「会话管理」表（8 行）从 2 列改造成规范 v2 的 5 列 schema：用途压回一条职责命题，关键符号取自源码实测的类型与 ctor 依赖，关联只写真实存在的同工程路径，约束只保留「不可从代码推断且删掉会导致误改」的两条并压到 ≤160 字符。检查器 error 118 → 113 → 101、warn 50 → 48 → 46（波次 1-2）；本文件归并记录该目标文件的全部波次，文件名保留首波次命名。目的不是清数字，而是把该表从正文堆积改回可导航索引；本文件是 15 张表中的第 1 张。"
categories: [docs, code-map, condensation]
tags: [code-map, spec-v2, schema, condensation]
related_docs: [Docs/10_conventions/code-map-规范-v2.md, Docs/00_changelog/2026Year/10/2026-10-07-code_map规范v2与自检.md, Docs/00_changelog/2026Year/10/2026-10-08-code_map波次1-PuddingCodeIndex契约表列式改造.md]
related_files: [Source/PuddingPlatform/code_map.md, Tools/Docs/code_map_check.py]
slug: changelog-code-map-puddingplatform-session-table-2026-10-08
draft: false
---

# 2026-10-08 PuddingPlatform code_map 波次 1：会话管理表 → 规范 v2 五列

## 目标与范围

`Source/PuddingPlatform/code_map.md` 是全仓最大的病灶：**44,578 B / 268 行 / 15 张表 / 181 条目行**，检查器 **error 118（field-too-long 100 + line-too-long 18）、warn 50（anti-pattern 48 + header-repeat 1 + missing-fingerprint 1）**。
本轮只做**第一张表「会话管理」（L9–L16，8 行）**：宁可一次做透一张，也不做「全表改成 5 列但语义没重写」的形式改造。

## 做法（每格都由模型读源码后手写）

| 列 | 本轮怎么填 |
|---|---|
用途（F） | 压回**一条职责命题**（最短 6 字符，最长 21），不再罗列动作 |
关键符号（A） | 取自**源码实测**：`temp/hb31-evid.txt` 由脚本按 `^\s*(public\|internal)\s+…(class\|interface\|record\|struct\|enum)` 提取类型名、并按 ctor 签名提取注入的接口；**未出现在磁盘上的符号一律不写** |
关联（R） | 只写**同工程内真实存在的相对路径**，由脚本逐个 `os.path.exists` 断言 |
约束（S） | 只写满足 §2 双问的（不可从代码推断 **且** 删掉会导致误改），上限 160；答不上来就写 `—`（8 行里有 3 行是 `—`） |

## 数字（检查器 `tool_sha256=2d1ae2a1…`，实跑）

| 指标 | 改造前 | 改造后 |
|---|---|---|
error | 118 | **113** |
├ field-too-long | 100 | 96 |
└ line-too-long | 18 | 17 |
warn | 50 | **48**（anti-pattern 48 → 46） |
用途列（该表） | 4–583 字符 | **6–21 字符** |
表内最长行 | 661 字符 | 298 字符 |
文件字节 / 行数 / 条目行 | 44578 / 268 / 181 | 44721 / **268** / **181**（行数与条目数不变） |

`gate` 仍为 FAIL —— 该文件还有 14 张表未改造，这是**如实记录**而非达标。

## 剥离了什么（诚实披露，避免"静默丢信息"）

§2 要求 S ≤160 字符，故长单元格必须丢弃内容。本轮丢弃的部分**均为演进叙事、旧行为对比与实现叙述**，其权威来源是源码与对应 ADR：

- L10（原 583 字符）：剥离 `S4 / 2026-09-19 / 2026-10-02` 阶段标注、"旧行为每 256 条重扫一次边界"的对比、相位日志名（`live-only` / `replay-from-zero` / `replay-after`）、waiters 的 linked CTS 实现叙述；**保留**两条不可推断约束（订阅起点语义、追赶读取规模有界口径）。
- L11（原 211 字符）：剥离"S4 剩余项，2026-09-19"与"同时消除旧判据假阳性"的历史对比；**保留**判据本身与"不得改回旧判据"的反例。
- L13：`durable` 一词未逐字保留，其含义由 S 覆盖（注入状态需持久化）。
- L15 / L16：原本只有 4–6 字符描述，本轮**只补关键符号/关联**，未新增论断。

## 写前断言（两次真实取红，均未写入任何内容）

1. **锚点不唯一**：`| 文件 | 用途 |` 在全文件重复 **14 次**；改用「首个数据行」后仍有 **2 处**命中 —— 由此发现下面这条缺陷。
2. **行宽越界**：第 2 行 313 字符 > `MAX_LINE_LEN=300`（各格均未越列上限），压 S 到 112 字符后为 298。

最终锚点：唯一章节标题 `## 会话管理` → +2 定位表头，并断言表头/分隔行/首行身份三者一致；写后 `readback_match=True`、文件仍为纯 LF（`cr_after=0`）。

## 发现的既有缺陷（本轮不修，登记待决）

1. **同一对象重复登记**：`Services/SessionStateManager.cs` 分别在 L9（会话管理）与 **L84（子代理 & 诊断）**各占一行，且用途描述分化（前者"会话状态管理"，后者"会话/子代理持久状态查询"）。按 §2「每对象一行」这是重复条目；处置（合并为一行 / 在 L84 改指向子代理专用文件）需与"子代理 & 诊断"表的改造一并裁定。
2. **`header-repeat` 结构性 warn**：该文件 15 个 concern 分区共用同一表头（14 次 > 上限 8），而 §2 又要求固定列序 —— 二者冲突。要么合并分区，要么登记"接受该 warn"的理由，留待后续波次裁定。
3. **缺 §5 源指纹**：指纹描述的是**源码**而非本文档，与本轮文档改造无耦合；留待专门一波用检查器自身的 `glob_fingerprint` 实测后再写（不手算、不改数字凑绿）。

## 波次 2：消息网关表（14 行）

范围 `## 消息网关`（L36–L49，14 行，含 2 个目录行与 2 个多文件行）。

| 指标 | 波次 1 后 | 波次 2 后 |
|---|---|---|
error | 113 | **101**（field-too-long 96 → 86、line-too-long 17 → 15） |
warn | 48 | **46** |
该表最长行 | 573 | **290** |
字节 | 44721 | 45204 |
行数 / 条目行 | 268 / 181 | **268 / 181（不变）** |
改造区间违规 | — | **L36..L49 = 0**（对报告 JSON 独立复核） |

目录行（`Services/MessageGateway/`、`Services/Conversation/`）的关键符号取自**磁盘实测的 6 / 8 个 .cs 文件**（脚本逐目录枚举并提取类型）；多文件行 `MessageFabricStore.cs` 的路径**补全为真实路径**（见下）；被引用接口 `IConversationEventStore` / `ICommittedEventSignal` 实测声明在 `Source/PuddingCore/Platform/`，故关联列只写符号名（不写跨工程路径，避免 L2 越界）。

### 写前断言取红（第二轮，均未写入）

1. `关键符号` 格 101 > 100（该行原本想把 `POST /api/v1/conversations/{conversationId}/turns/{turnId}/steering` 塞进关键符号）⇒ 改为列 `ConversationTurnsController` / `SteeringHttpRequest` / `SubmitTurnHttpRequest`，**端点从索引中移除**（它可由控制器的路由属性直接读出，属"可从代码推断"，不满足 S 的双问）。
2. 三行超 `MAX_LINE_LEN`（307 / 327 / 304）⇒ 通过把关联列换成**符号名**（`MessageQueueProjectionService` / `ConversationNotificationStore`）与收敛用途措辞压到 290 以内。
3. token 零丢失检查报出 `MessageFabricStore.cs` 与 `PuddingPlatformTests/Services/RsiTrajectoryDataAccessTests.cs` 两项差异 —— 前者是**修正**（原单元格写的是裸文件名，缺 `Services/MessageFabric/` 前缀，实测真实路径存在），后者是**有意丢弃**（测试工程路径属测试侧索引，非本 L2 条目范围）。两项均已显式登记为例外，不是静默丢失。

### 本轮剥离内容（披露）

- L45：剥离"（18KB）"、"2026-10-02"、以及旧 `MIN(sequence), MAX(sequence)` 的行为对比与"该方法在 SSE 回放/轮询里被反复调用"的频率叙述（前者是叙事，后者可由调用方读出）；**保留**索引端点与"不得改回 `MIN/MAX`"的约束。
- L46：剥离"照抄 `SkillEvolutionDataAccess` 模式"的同类引用、测试文件路径与 `EXPLAIN QUERY PLAN` 断言细节；**保留**索引不足以给出完全有序、第二排序键走 `TEMP B-TREE`、代价受行数上界约束、limit 放大到千行级必须重评。
- L36：去掉 `🔑` 标记与"（FeishuImageArtifactProjection 等）"式举例，改为在关键符号列列出实测类型。

## 遗留（后续波次）

- 其余 **13 张表 / 159 行**：对话 & 聊天 9、Agent 管理 6、认证与当前用户 6、子代理 & 诊断 8、任务系统 20、Agent Availability 与自动派发 25、Goal 持久控制面 16、外部访问令牌 19、安全审批 2、持久化 18、多媒体 6、提供商配置 6、Token 计量 18。
- 既有缺陷待裁定（波次 1 登记，仍未处理）：①`Services/SessionStateManager.cs` 在 L9 与 L84 重复登记；②同表头重复 14 次 > 上限 8 与固定列序冲突；③缺 §5 源指纹（待专用一波用 `glob_fingerprint` 实测后写）。
- 本轮新增登记：`Services/MessageFabric/MessageFabricStore.cs` 的**裸文件名路径**已完成修正；同表的 `Services/MessageFabric/` 与 `Services/Conversation/` 目录行未展开为逐文件条目（是否值得展开需按 §4 成本判据裁定）。
