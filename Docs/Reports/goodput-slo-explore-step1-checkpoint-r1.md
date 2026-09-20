# Goodput SLO — Step 1 Explore Checkpoint（checkpoint-v1）

- goalRunId `tg-9ec9d27543747ec6849a52aae10d9f31`｜task `0b16740022f84b58a9532a87f1bc5509`（已认领 v21→22）｜stepNode `tn-619c1dff9dafa61598a18756a13a376c`（**Explore**, seq 1/5）
- step objective 逐字：*"Collect canonical repository and runtime evidence before deciding changes."*
- 上轮 verdict：`blocked / work_unit_budget_exhausted` —— **根因是委派工具假失败**（见 §6），不是证据问题。

---

## 1. 证据来源（两条独立链）

| 来源 | 产物 | 可核查性 |
|---|---|---|
① 委派子代理（仓库证据） | `Docs/Reports/goodput-slo-explore-step1-evidence-r1.md`（100 行，Q1–Q7 逐条带 `file:line` + 覆盖限定） | 我抽查其 Q1 引用：`Source/PuddingCore/Tasks/WorkspaceTaskModels.cs:319 TaskProgressed` —— **逐字命中** |
② 我的只读运行时探针（临时件，`temp/` gitignored） | `temp/db-probe-goodput-step1.py`、`temp/db-probe-goodput-step1b.py`（`file:…?mode=ro`） | 逐字命令 + 输出见 §3/§4 |

## 2. 七问裁定（**每条落到「卡片的哪条验收项是否可度量」**）

| # | 结论 | 对卡片验收项的意义 |
|---|---|---|
Q1 | `progressed/awaiting_external/no_ready_work/recovered/failed` **不存在**；现状由四套既有词汇表达（结算处置 `GoalVerificationContracts.cs:221-231`、Goal 事件 `GoalEventTypes.cs:14-30`、调度决策码 `TaskSchedulerDecisionCodes.cs:12-26`、健康裁决 `TaskExecutionTrackingContracts.cs:10-16`） | 「**全部心跳可解释**」**当前无度量面** —— 我自己 6 轮迭代亲历的实际词汇只有 `outcome=continue\|blocked` + `blockerCode` + `stepsPassed` |
Q2 | 续行/结算是**固定 5s 轮询**（`GoalRunOptions.cs:25`；`GoalContinuationWorker.cs:55`、`GoalSettlementWorker.cs:31`），无事件驱动唤醒；等待事实可观测（outbox lease/`lease_expired_recovered`、Deferred 重排 `GoalContinuationWorker.cs:250-258`、熔断 `GoalSettlementStore.cs:1517`） | 「**等待无忙轮询**」需先定义口径；已有观测面可用 |
Q3 | 明细表 `TokenUsageEvents` 有 token 与成本列（含 `CacheMissTokens`/`CacheHitRate`/`InputCost`/`OutputCost`/`TotalCost`）；迭代级 `goal_iterations.input_tokens/output_tokens` | 「每 verified task 输入/miss/输出/费用」**token 与 miss 有数据、task 维度没有**（见 Q5/Q7） |
Q4 | **缺价被静默记 0 成本**（价格默认 `0m`，无 `unknown_price` 标记列；`TokenUsageRecorder.cs:160-190`、`LlmGatewayUsageRecorder.cs:54-56`）；`usage` 为 null 时是跳过而非记 0；未知归因只写内存标记、**不落库** | 「**0usage/缺价/未知归因不被当节省**」**当前被违反**，且已有运行时可量化证据（§3） |
Q5 | 已有**只读端到端对账器** `TaskExecutionTracker.cs:21-130`（Task→Assignment→GoalRun→Reservation→PlanRun→TaskNode→Iteration→Outbox→ExecutionCommand→ExecutionRun）；但 **ExecutionRun 无 provider/model 列** | 「任务与真实 worker/assignment/Run 可对账」**部分成立**；「**→模型**」这一跳缺边（只能靠 `goal_runs.route_snapshot_json` 或 session 关联） |
Q6 | 四类时间账（active work/LLM wait/tool wait/queue）在 Platform 层**零命中**；Runtime 侧只有 `rate_limit_wait_ms` 与 `stream_first_chunk_wait_ms`；但已存在可派生的时间戳列（`goal_outbox.due_at_utc/completed_at_utc`、`goal_iterations.started_at_utc/settled_at_utc`、`task_assignment_attempts.active_at_utc/released_at_utc`） | 报告口径**未实现**，但**可从现有列派生**（低成本路径） |
Q7 | **无 cost per verified unit、无质量前后对比聚合**；现有聚合只有「日×来源×provider×模型」与 per-iteration 计数；成本感知路由是**声明式**（用声明价与声明质量分排序） | 卡片三项 SLO（重复失败目标 −50%、P95 不恶化、首轮开销 −30%）**当前均无度量面** |

## 3. 运行时证据（逐字，探针 1）

```
TokenUsageEvents: rows = 97168
列：Id, SourceType, SourceId, WorkspaceId, SessionId, ProviderId, ModelId, OccurredAtUtc, YearMonth,
    PromptTokens, CompletionTokens, TotalTokens, CacheHitTokens, CacheMissTokens, CacheEligibleTokens,
    CacheHitRate, InputCost, OutputCost, CacheHitCost, TotalCost, RawUsageJson, Prefix*, TurnRound,
    ToolCallCount, ToolNames, SubAgentId, CreatedAtUtc, ParentSessionId, *Entropy, *Tokens(细粒度)
zero_or_null_cost = 7725        # 97168 的 7.95%
cache_miss_zero_or_null = 0     # CacheMissTokens 无缺失
llm_gateway_usage_events: rows = 104968（snake_case 列，含 source_id/operation）
llm_usage_daily_aggregates: rows = 250
```
**诚实更正**：探针 1 对该表用 PascalCase 查列 ⇒ 那两条 `ERR: no such column` 是**我的脚本问题**，不是数据问题。

## 4. 运行时证据（探针 2）—— **"缺价被当 0"被量化证实**

```
zero_cost_by_model（TotalCost=0/NULL 的 7725 条分解）：
  qwen/qwen3.8-max 3921 · opencode/deepseek-v4-flash 1005 · opencode/deepseek-v4-pro 753
  qwen/qwen3.8-max-preview 655 · opencode/glm-5.2 609 · fastrouter/gpt-5.6-sol 435
  opencode/ox-alpha-free 142 · fastrouter/gpt-6-astra 104 · opencode/glm-5.3 97 · opencode/qwen3.8-max 4
⇒ qwen/* 与 opencode/* 共 7186 条 = 零成本记录的 93%，而这两个 provider **不在当前注册池**
   （注册池仅 bigmodel / deepseek / fastrouter / volcengine-ark）⇒ **无价格档案 ⇒ 成本静默回落 0**
⇒ fastrouter/* 的 539 条是**真 0 价**（免费档）—— 若不分解，两者会被混为一谈

positive_cost_by_model（条数, 累计成本）：
  deepseek/deepseek-v4-flash 30735, 198.5069 · deepseek/deepseek-v4-pro 26752, 774.1288
  deepseek/deepseek-flash 14875, 250.6148 · bigmodel/glm-5.3-flash 12173, **1211.073**
  deepseek/deepseek-v4-flash-vision-exp 4355, 34.7008 · bigmodel/glm-5.3 546, 45.4519 · moonshot/kimi-k3 11, 4.5706
⇒ 可量化替代卡片里"GLM 约 90% 计费"的粗略说法：glm-5.3-flash 每行成本 ≈ 0.0995，
   是 deepseek-v4-pro（0.0289）的 **3.4×**、deepseek-v4-flash（0.0065）的 **15.4×**
   （**注意**：每行成本受单行 token 量影响，非严格同口径；同口径需按 token 归一）

source_type_dist：agent_llm 93124 · subconscious_memory 3178 · sub_agent:workspace-task-agent 782
                  agent_compaction_llm 86 · sub_agent:general-assistant 2
latest_rows → SourceId 形态 = `{sessionId}:{32-hex}:{round}`（例 `206a9b48…:e01cde26…:9`），SessionId=会话
⇒ **归因粒度 = (session, 32-hex id, round)，没有 taskId/goalRunId/workUnitId**
```

## 5. 结论（**证据支持的四条硬事实**）
1. 卡片三项核心 SLO（重复失败 −50%、P95 不恶化、首轮开销 −30%）**当前都没有度量面**（Q7 + Q6）。
2. 「每 verified task 的费用」**当前不可直接对账**：token/费用有列，但**唯一归因粒度是 session+hash+round**（§4）。
3. 「缺价不被当节省」**当前不成立**，且已量化：93% 的零成本记录来自无价格档案的 provider（§4）。
4. 「任务与 worker/assignment/Run 对账」**部分成立**：对账器存在，但缺「→模型」这一跳（Q5）。

## 6. 新登记平台缺陷（**本轮的实际阻塞**）
- 现象：`spawn_sub_agent` 返回 **FAILED**，错误为 `search_grep: Directory '…\Source\PuddingNetwork' not found`
  ⇒ 子代理默认检索根指向**不存在的目录**；而**子代理实际执行成功并写出了交付物**
  （`Docs/Reports/goodput-slo-explore-step1-evidence-r1.md` 已存在且内容完整）。
- 危害：① 假失败误报 ⇒ 父代理可能**重复派发**（重复劳动、预算浪费，上一轮 iteration 即因此报 `work_unit_budget_exhausted`）；
  ② 未显式指定 `directory` 的工具调用会**硬失败**。
- 缓解（本轮已用）：委派不通时**不空转**，改由父代理自己做**受限探索**（限定目录 grep + 窗口读）。

## 7. 可复用资产（**避免重造**）
`Docs/Reports/tracker-watchdog-step2-heartbeatoutcome-design-r1.md` 已有 **`HeartbeatOutcome` 投影设计**
（heartbeatId、command/turn、before/after progressFingerprint、evidence、outcome、waitHandle、nextEligibleAt；
原则：**是既有 canonical 事实的投影，不是新状态机**）⇒ 本卡「心跳可解释」应**复用该设计**，不要另起一套。

## 8. 下一步（step2 Plan）必须解决的 3 个调查项
1. `SourceId` 中的 **32-hex id** 到底是什么（agentId / commandId / turnId？）—— 决定 task 级成本对账的桥接路径。
2. 「忙轮询」的**可测口径**与阈值（例如单位时间 busy-iteration 占比、`ConversationBusyRetryDelay` 命中率）。
3. 「缺价」需要新增什么标记（如 `pricing_status` 列或事件码）才能让 0 成本与 0 价可区分。

## 9. 诚实限定
- 全部为**只读**取证：未改产品代码、未改数据库、未提交除本文档与证据文件外的任何内容；探针为临时件。
- 委派子代理的结论我只**抽查了 1 处**引用（逐字命中）；其余按其自带的 file:line 引用保留，**未逐条复核**。
- grep 的"未命中"均附覆盖限定；**未命中 ≠ 功能不存在**。
- 运行时代价数字是**单机单库**样本，不代表生产全量。
