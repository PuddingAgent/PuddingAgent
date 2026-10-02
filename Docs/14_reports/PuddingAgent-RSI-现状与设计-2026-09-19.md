# PuddingAgent 的 RSI 能力：现状核查与建设设计

日期：2026-09-19 ｜ 关联：用户提出的「收集器 / 数据处理器 / 优化器」三环诉求

---

## 0. 结论速览

1. **「Harness-RSI」是真实术语，不是社区随口造的词。** 命名来源是 MetaRSI / RSI2 预印本（arXiv:2609.06396，2026-09-06，CosmosMind 等 30+ 作者，附开源实现 `CosmosMind-ai/RSI-Harness`）。它把 RSI 拆成三个算子：**Data-RSI**（放大既有能力并标定边界）/ **Harness-RSI**（**不碰权重**，直接编辑「五槽位脚手架 Genome」）/ **Model-RSI**（把能力内化进参数）。上位通行术语是 **harness engineering**（OpenAI 工程博客 2026-02-11、Martin Fowler、Lilian Weng 均已规模化使用），Harness-RSI 只是它的子集命名。截至检索时点，**未发现该词在 ACL/NeurIPS/ICLR 等正式会议中作为独立分类名被第三方使用**（该结论置信度 medium，受检索上限所限）。

2. **PuddingAgent 具备「闭环骨架」，但只覆盖五槽位中的 2 个，且缺最关键的一环——评估闸门。** 五槽位对照：

| Harness Genome 槽位 | PuddingAgent 能否自动改 | 载体 / 证据 |
|---|---|---|
| ① 系统提示词 | ❌ 不能 | `Agents.md` / `agents` / `soul` 文档只能由 Agent 手工编辑或人工改 |
| ② 持久化记忆 | ✅ 能 | `memory.auto_dream` + `memory.consolidate_session` + MemoryWiki 页 |
| ③ 内置工具 | ❌ 不能 | 工具是 C# 代码（`Tools/BuiltIns/**`），改进环读不到也写不了 |
| ④ 技能库 | ✅ 能 | `skill.extract_patterns` → `skill.improve` → `AgentSkillEvolutionStore` |
| ⑤ MCP / 挂载资源 | ❌ 不能 | 无自动挂载与调优通路 |

3. **最突出的单点缺口：收集器「只学成功，不挖失败」**，而外部当前最接近我们场景的方法（`Self-Harness`，arXiv:2606.09498）恰恰是「**失败挖掘 → harness 提案 → 回归测试**」三段闭环。我们缺的是首尾两端。

4. **如果只做一件事：先建 evaluator harness（可重放的评测 + 回归门禁）。** 否则任何自动改进都无法证明「没有变坏」，优化器只能凭感觉改——这正是当前技能库积累模式的根本问题。

---

## 1. 外部现状

### 1.1 五个分支（按方向分组）

| # | 工作 | 机构/作者 | 年份 | 一句话贡献 | 来源 |
|---|---|---|---|---|---|
| 1 | Gödel Machine（源头） | J. Schmidhuber | 2003/2006 | 首个形式化的「自我改写 + 效用证明」自指机器 | 见 awesome-rsi 索引 |
| 2 | Darwin Gödel Machine | Sakana AI + UBC | 2025-05 | 用经验测试替代形式证明；coding agent 改写自身代码并维护进化 archive；SWE-bench 20%→50% | arXiv:2505.22954 |
| 3 | Gödel Agent | 北大 X. Yin 等（ACL 2025） | 2024-10 | 不预定义例程，运行时动态读写自身逻辑 | arXiv:2410.04444 |
| 4 | SICA（Self-Improving Coding Agent） | M. Robeyns 等 | 2025-04 | agent 迭代修改自身代码库；SWE-Bench Verified 子集 17%→53% | arXiv:2504.15228 |
| 5 | **Self-Harness: Harnesses That Improve Themselves** | H. Zhang 等 | 2026-06 | **失败挖掘 → harness 提案 → 回归测试** 三段闭环 | arXiv:2606.09498 |
| 6 | **MetaRSI / RSI2** | CosmosMind 等 30+ | 2026-09 | 把 RSI 拆成 Data/Harness/Model-RSI，用两级 policy 调度「改哪里、按什么顺序改」 | arXiv:2609.06396 |
| 7 | RSIAgent | — | 2026 | 免训练的多 agent harness 自改进；评测用 MLE-bench / SWE-bench Verified / OSWorld 2.0 | 见索引 |
| 8 | STOP（Self-Taught Optimizer） | — | 2023-10 | 把 **scaffolding 程序本身**作为递归改进对象 | arXiv 摘要 |
| 9 | ADAS / Meta Agent Search | — | 2024-08（ICLR 2025） | 把自改进扩展为代码空间内的 agent 架构搜索 | arXiv:2310.02304 |
| 10 | The Last AI Built by Humans | — | 2026-09-10 | RSI 立场/路线图论文，提出自主度五级分级 | arXiv:2609.11873 |
| 11 | Harness engineering | OpenAI 工程 / Martin Fowler | 2026-02 | 把「模型外围系统」工程化：编排、工具、上下文、产物、评测 | openai.com/index/harness-engineering/ |
| 12 | Self-Harness / Meta-Harness / RHO 三连 | — | 2026-03 ~ 06 | 三条独立路线：提案式、搜索 harness 代码、仅用历史轨迹自监督 | 见索引 |

> 更早的自教/自奖励支流（STOP、Self-Rewarding LM、SEAL、Absolute Zero、R-Zero 等）在文献中已被归入 F5；本轮未逐篇核对摘要，引用时需回原文。

### 1.2 优化对象阶梯（Optimization Ladder）

社区（Weng 博客 + Awesome-Harness-Self-Improvement）表述一致：

```
L0 提示词  →  L1 上下文/记忆  →  L2 工作流/编排  →  L3 harness 代码  →  L4 harness + 权重联合
```

**PuddingAgent 今天位于 L0 之下**：②记忆 与 ④技能库 有自动通路，但 **L0 提示词、L2 工作流、L3 harness 代码**都没有自动通路。

### 1.3 三条硬性工程共识

1. **必须有回归门禁**：任何自改都要在 held-out 评测上验证不退化，否则等于让系统自己给自己打分。
2. **必须可回滚**：变更要版本化 + 一键回退。
3. **必须限幅**：一次只改一处、有签名/审计、人类审批闸门。

### 1.4 已知失败模式

reward hacking、能力退化（catastrophic forgetting / diversity collapse）、评测集污染与过拟合、自我强化错误、不可逆自我修改。**这与我们自己的实测吻合**：技能库已积累大量带 `self-evaluated` / `dedup-reviewed` 标签的技能，但**没有任何基线对比证明它们让任务完成率变好**。

### 1.5 检索局限（诚实标注）

- 各工作的「量化收益」（DGM 20%→50%、SICA 17%→53%）来自一手页面摘要，**未核对论文正文与实验设置** ⇒ 置信度 medium。
- 开源项目**活跃度未能核实**：GitHub REST API 返回 403，触发同指纹熔断，子代理会话被终止。需用网页或 `git ls-remote` 补核。

---

## 2. PuddingAgent 现状：代码级核查

### 2.1 已有的后台学习机制

`Source/PuddingCore/Platform/SubconsciousDtos.cs:32` 定义了四个 job type：

| JobType | 常量 | 触发方式 | 产物 result kind |
|---|---|---|---|
| `memory.consolidate_session` | `MemoryConsolidateSession` | **hook 驱动**：会话压缩后（`Services/Hooks/SessionCompressedMemoryMaintenanceHook.cs:106`） | `memory_maintenance_plan.dry_run` |
| `memory.auto_dream` | `AutoDream` | **周期性**（`AutoDreamIntervalSeconds`） | `memory.auto_dream.v1` |
| `skill.extract_patterns` | `ExtractPatterns` | **周期性**（`PatternExtractionIntervalSeconds`） | `skill.pattern_extraction.v1` |
| `skill.improve` | `ImproveSkills` | **周期性**（`SkillImprovementIntervalSeconds`） | `skill.improvement.v1` |

调度器：`Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs`（`:207-209` 判定周期性类型，`:233-248` 分发，`:706-727` 注册间隔）。
手动触发：`Source/PuddingRuntime/Tools/BuiltIns/Management/SubconsciousTriggerTool.cs`（`:56` `improve_skills` → `:129` `_orchestrator.ImproveSkillsAsync`）。

**已经具备的队列语义（比预期成熟）**：durable job queue + lease + retry + idempotency key；结果信封带
- status：`accepted` / `rejected` / **`quarantined`** / `completed`
- decision：`accept_for_execution` / `reject_complete` / `retry_later` / `defer_for_recheck` / `execution_completed`

👉 `quarantined` 与 `defer_for_recheck` 说明**「隔离待复核」的概念已经存在**，只是缺一个能真正裁决的评估器。

### 2.2 三环现状

**收集器**：`Source/PuddingRuntime/Services/Skills/ConversationSkillEvolutionTrajectorySource.cs`
- 从 canonical 会话事件库读：`GetRecentSuccessfulCommandsAsync` + 事件 `ToolCallRequested / ToolCallCompleted / ToolCallFailed`
- 有排除机制：`IsExcludedFromLearning(command.MetadataJson)`
- 要求链条 `steps.Count >= 2`（至少两次工具调用才成料）
- ⚠️ **`GetRecentSuccessfulAsync` 只取成功命令**；链条一旦失败即被丢弃 ⇒ **失败样本系统性缺失**

**诊断面（未被接入）**：`Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs:36`
可产出 tool_stats / slowest_tools / cache_health / sub_agent_stats / compaction_stats / latency_breakdown / token_breakdown / entropy_probe / context_health。
但它是**拉取式工具**：只有 Agent 主动调用才产生数据，**没有任何自动入库、没有时间序列、不喂给改进环**。用户列的「缓存命中率、任务成功率、工具错误率」目前**不在收集器视野内**。

**数据处理器**：`_orchestrator` 的 `ExtractPatternsAsync` / `ImproveSkillsAsync`（LLM 驱动的模式抽取与技能生成）。归因能力：**未知/待核** —— 从代码路径看是「把成功轨迹喂给 LLM 让它提技能」，没有独立的归因/诊断层。

**优化器**：`AgentSkillEvolutionStore`（写 SKILL.md）+ 记忆库（AutoDream / MemoryWiki 页）。

### 2.3 缺口清单

| # | 缺口 | 影响 | 优先级 |
|---|---|---|---|
| G1 | **收集器只学成功，无失败挖掘** | 最该改的错误行为永远进不了改进环 | P0 |
| G2 | **诊断数据未入库**（日志/命中率/错误率是拉取式） | 无法做趋势、无法定位退化、无法证明改进 | P0 |
| G3 | **无 evaluator harness / 回归门禁** | 自改无法验证，只能凭感觉；存在自我强化错误风险 | P0 |
| G4 | 优化面仅覆盖 2/5 槽位 | 提示词与工作流的改进仍需人工 | P1 |
| G5 | 无统一变更审计与一键回滚 | 改坏了难以定位与回退 | P1 |
| G6 | 无 A/B / canary 机制 | 无法在小流量上验证再全量 | P2 |
| G7 | 无元级调度 policy（改哪里、按什么顺序） | 改动可能互相冲突、收益递减 | P2 |

---

## 3. 设计：收集器 / 数据处理器 / 优化器（+ 一个闸门）

### 3.1 收集器（Collector）

**产出物：`ImprovementSignal`（统一的信号记录）**，落到独立表（不改会话事件表）。

信号分类（对应用户列的维度）：

| 类别 | 来源 | 现状 |
|---|---|---|
| 失败类 | `ToolCallFailed` 事件、`turn.failed`、审批被拒/超时、工具返回错误码 | 事件有，**未采集** |
| 效率类 | 缓存命中率、token 消耗、轮数、工具调用次数、wall-clock、p95 延迟 | 仅诊断工具可拉 |
| 结果类 | 任务完成率/失败率、重试次数、人工打断、卡在 Blocked | 看板有数据，**未入信号流** |
| 行为类 | 重复无效动作、越界尝试、同一错误重试、SKILL 未命中、子代理预算耗尽 | 部分散在日志 |
| 质量类 | 用户显式纠正、`/resume` 触发的 lease_lost、被拒的审批工单 | 分散 |

**关键设计决定：把「失败挖掘」作为一等公民。** 复用现有 `quarantined` / `defer_for_recheck` 语义——失败样本先进隔离区，人工或评估器裁决后再决定是否转成改进项，避免「从噪声里学错」。

### 3.2 数据处理器（Analyzer / Diagnoser）

分两层，**不要合并**：

- **确定性层（无 LLM）**：聚合与趋势 —— 按 tool/agent/task_type 维度算成功率、错误分布、成本分布、P50/P95；检测环比退化（今天比上周差多少）。这层要能用 SQL/代码复现，不能靠 LLM 叙述。
- **归因层（LLM）**：拿确定性层的产物 + 少量代表性轨迹做**根因假设**，输出结构化 `root_cause + evidence_refs + confidence + proposed_fix_scope`。要求每条结论必须挂证据引用（会话 id / 事件 id / 日志路径），禁止无证据叙述。

**避免的坑**：不要让归因 LLM 直接读全量轨迹（成本失控且会挑出轶事证据）；先降维再让它读样本。

### 3.3 优化器（Optimizer）

按槽位分通道，**每次只开一条通道、只改一处**：

| 通道 | 产物 | 当前能力 |
|---|---|---|
| 记忆 | memory 条目 / wiki 页 | ✅ 已有 |
| 技能 | SKILL.md | ✅ 已有 |
| 提示词 | `agents.md` / `soul` / 预设 `agentsPrompt` | ❌ 需新建（**必须挂闸门**） |
| 工作流 | 流程文档 / 切片纪律 / SKILL 内的步骤 | ❌ 需新建 |
| 工具与 harness 代码 | C# / 前端代码 | ❌ 建议**永远保留人类审批**（最危险的一槽） |

### 3.4 评估闸门（Evaluator Gate）—— 最关键的一环

**没有它，前三环都是空转。** 最小可用形态：

1. **回放集（replay set）**：把历史真实会话冻结成可重放用例（输入 + 期望性质，不是逐字期望输出）。现有资产可直接复用：看板卡的验收标准、测试套件、`excludedFromLearning=true` 的基准轨迹（`Tools/Diagnostics/run_benchmarks.py` 已在做类似事）。
2. **评分器**：任务成功/失败、工具错误率、轮数与成本、是否违反不变量。
3. **门禁**：改进提案必须在回放集上**不退化**才允许转正；退化的一律回退并记录。
4. **限幅**：单次改动有界（一处/一类），带签名与 diff，可一键回退。

### 3.5 安全边界（必须写进实现）

- 权重与训练**不在范围内**（我们是 Harness-RSI 而非 Model-RSI）；
- 工具与代码槽位的自动修改**永不放行**，只产出提案 + 人工审批；
- 自我修改**不得放宽安全不变量**（审批闸门、急停、角色工具白名单、进程终止）；
- 评测集与优化器的**可见性隔离**：优化器不能读到评测集的判分逻辑，防过拟合。

---

## 4. PuddingAgent 需要提供的基础设施（回答「第 4 点」）

用户已明确：第三步骤**不一定代码实现**，可以是 SKILL / 流程 / 负责诊断自身的 LLM 或 Agent。因此基础设施的定义是「**让这些非代码形态也能跑起来的最小支撑**」。

### 4.1 可直接复用（已存在，不必重建）

- durable job queue + lease + retry + idempotency（`SubconsciousWorkerService` 背后的队列）
- 结果信封与决策语义（`SubconsciousJobResultDecisions/Statuses`，含 `quarantined` / `defer_for_recheck`）
- 手动触发入口（`SubconsciousTriggerTool`）
- 轨迹来源抽象（`ISkillEvolutionTrajectorySource`）与学习排除标记（`excludeFromLearning`）
- 技能与记忆的读写存储（`AgentSkillEvolutionStore`、memory books）
- 诊断指标计算（`AgentDiagnosticsTool` 背后的统计能力）
- 体检/开销监控能力（`agent_diagnostics`、子代理花费）

### 4.2 需要新建（最小集，按依赖排序）

1. **信号落库 + 查询接口**：`ImprovementSignal` 表 + 写入点（在工具调用/回合终态/审批结果处埋点）+ 按维度聚合的查询。**这是三环的地基。**
2. **评估器接口（可替换实现）**：`ISelfImprovementEvaluator`，最小实现 = 回放集 + 评分器 + 通过/退化判定。允许先用手写的 SKILL/流程当实现，后续再代码化。
3. **提案与闸门**：`ImprovementProposal`（目标槽位 / diff / 证据 / 预期收益）+ 审批与回滚台账。
4. **调度入口**：一个 `subconscious_trigger` 新 action（如 `improve_harness`）让 Agent 自己能发起一轮「收集→诊断→提案→评估」。
5. **可观测**：每轮改进的输入样本数、提案数、通过/退回数、回退数 —— 否则这个系统自己也变成黑箱。

### 4.3 明确**不**需要

- 不需要新建分析型数据库或引入向量库：现有 SQLite + 全文索引足够支撑 P0/P1。
- 不需要改模型或做训练。
- 不需要新建一套 Agent 框架：复用现有 subagent 机制承载「诊断 Agent」「提案 Agent」。

---

## 5. 分阶段落地（建议顺序与验收）

| 阶段 | 内容 | 验收（可验证） |
|---|---|---|
| **P0** | 信号落库（G1+G2）：失败/效率/结果三类信号入库，含聚合查询 | 能回答「过去 7 天 shell 工具失败率 top5 原因」「缓存命中率周环比」「按 task_type 的成功率」 |
| **P1** | evaluator harness（G3）：回放集 + 评分器 + 退化判定 | 拿一次真实技能改动做对照：闸门能判出「无退化」或「退化」，且判定可复现 |
| **P2** | 提案与闸门（G5）：提案结构 + 审批 + 回滚 | 每个被采纳的改进都有 proposal id、diff、评估结论、回退路径 |
| **P3** | 优化面扩到提示词/工作流（G4） | 至少一条提示词类改进走完「信号→诊断→提案→评估→采纳」全链路 |
| **P4** | 元级调度与 canary（G6+G7） | 能按收益/风险排序候选改进；小流量验证后再全量 |

---

## 6. 一句话回答用户三个问题

1. **RSI / Harness-RSI**：是真实且活跃的方向；Harness-RSI 特指「不碰权重、改五槽位脚手架」，命名来自 2026-09 的 MetaRSI 预印本，上位术语是 harness engineering。
2. **PuddingAgent 是否具备产生下一次自我改进的机制**：**具备骨架，不足以闭环**。有调度器、队列、技能与记忆两条自动通道、隔离语义；缺失败采集、缺诊断入库、**最缺评估闸门**。
3. **三环**：收集器要补失败与效率信号并落库；数据处理器拆成「确定性聚合」+「带证据的 LLM 归因」两层；优化器按槽位分通道、每次只改一处；**并且必须在优化器之外独立建一个评估闸门**。基础设施不必新建大件——主要是在现有 `SubconsciousWorkerService` 生态上补「信号表 + 评估器接口 + 提案/回滚台账」三样。
