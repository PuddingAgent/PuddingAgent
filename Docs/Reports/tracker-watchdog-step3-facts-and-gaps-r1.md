# step3 事实取证：Tracker 落库、fingerprint 与 nextAction 落点（r1）

- Goal `tg-cbdced5fef984688c568d8aa045cfe77`｜Task `77883a50d4c8453cbd05c38ee1719f0e`｜iteration 4
- step 节点 `tn-99cd5f5c95da040ff3c837630fc00b05`（Explore 1/5）

---

## ① Tracker 判定**未落库** ⇒ 验收标准 6 当前**不可离线度量**

检索方式（表名 + 列名两路）：
- 表名含 `track/decision/heartbeat/progress/health` ⇒ 全库**仅** `task_scheduler_decisions`（**派发器**决策日志，非 Tracker 判定）。
- 列名含 `verdict/reason_code/tracking/next_action/blocker` 的表（**逐字列出，全部为其它语义**）：
  `agent_availability_projection.reason_code`、`goal_runs.{consecutive_same_blocker, last_next_action}`、
  `goal_verifications.{verdict, next_action, blocker_code, blocker_message}`、`task_evaluations.verdict`、
  `task_scheduler_intent_outcomes.reason_code`、`workspace_tasks.{blocker_kind, blocker_reason}`。

⇒ **不存在 `TaskExecutionTrackingDecision` 的持久化载体**（只有返回值 + 内存 repair 输入）。
⇒ **验收标准 6（false running/failed/completed 各 <0.5%）在现结构下无法离线度量** ——
   要知道"某 tick 判了什么"，必须有判定落库表或等价 event 流。
**限定**：以**表名/列名**检索为据；未排除以 JSON 载荷形式承载的可能。

## ② 五分钟对账**接线点已存在**
`Source/PuddingPlatform/Services/Scheduling/TaskAutoDispatchScanRunner.cs`
- `:18-19` 构造注入 `ITaskExecutionTracker executionTracker` + `ITaskExecutionRepairCoordinator executionRepairCoordinator`；
- `:117` 与 `:233` 两处调用 `executionTracker.EvaluateAsync(workspaceId, limit, ct)`。

⇒ 「与五分钟 Scheduler 同步」**接线成立**；缺的是**判定结果的持久化**（见 ①）与 `HeartbeatOutcome` 回执（审计 A02）。

## ③ **`progress_fingerprint` 三个列 100% 为 NULL**（本卡最关键的前置缺口）

| 表 | 列 | 行数 | 非空 |
|---|---|---|---|
`goal_runs` | `last_progress_fingerprint` | 24 | **0** |
`goal_iterations` | `progress_fingerprint` | 110 | **0** |
`task_nodes` | `progress_fingerprint` | 114 | **0** |

卡片要求「`last_progress_at` **仅在 fingerprint/artifact/terminal 变化时推进**」，
而 **fingerprint 从未被写入** ⇒ 该判定面**事实上退化为「terminal + 时间」两条腿**。

⇒ 直接推论：审计 A02 要求的 `HeartbeatOutcome.before/after progressFingerprint` **当前没有数据源**。
**必须先实现 fingerprint 的计算与写入（含唯一写入点），`HeartbeatOutcome` 才可能成立。**

## ④ `nextAction` 面：**存在于 Goal 层，但取值为常量占位**（标准 1 的落点修正）

| 载体 | 列 | 已填 | 取值 |
|---|---|---|---|
`goal_verifications` | `next_action` | **107 / 109** | 几乎全部为常量 `"Resolve the blocker, then explicitly resume the Goal."` |
`goal_runs` | `last_next_action` | **22 / 24** | 同上（同一句常量） |

⇒ 结论修正：标准 1 的 nextAction 面**不是"完全没有"**，而是「**形式存在、实质不可操作**」——
所有阻塞场景给出同一句话，无法区分"下一步该做什么"。
⇒ 正确落点：按 `verdict + reason` **派生可区分**的动作（例如 `continuation_pending → 等 outbox 到点`、
`run_terminal_iteration_open → settle 该 iteration`、`continuation_lease_expired → 回收 lease`）。

## ⑤ 计数器载体**已就位**（`goal_runs` 列，逐字）
`consecutive_no_progress`、`consecutive_same_blocker`、`consecutive_infra_failures`、
`last_progress_fingerprint`、`last_next_action`、`active_elapsed_ms`、`total_tool_calls`、
`input_tokens`/`output_tokens`/`cost`、`resume_policy`、`activation_epoch`、`activation_boot_id`、`aggregate_version`。

⇒ 卡片要求的「2 个 tick 无新进度 → `soft_stalled`」「3 个 tick 允许一次 checkpoint+replan」
「同 failure family 无新证据 → circuit breaker」**只缺判定逻辑与状态命名，不缺存储**。
（样本观测：`consecutive_same_blocker = 0`；`resume_policy` 等列未展开核对。）

## ⑥ 顺带观测（非本卡结论）
`goal_verifications` 共 109 行，`verdict` 分布：**`blocked` 107 / `complete` 2**。
与已登记的 `objective_evidence` 缺陷（`14c02e8b`）和计划版本循环缺陷（`5413ce1b`）方向一致，
但**不构成它们的独立证明**（多数 unmet 可能是真实未达标）。

## ⑦ `task_evaluations` 语义澄清（排除误判）
列为 `evaluation_id/task_id/workspace_id/verdict/score/comment/task_version_observed/
supersedes_evaluation_id/evaluator_type/evaluator_id/evaluator_display_name/created_at_utc`，
`verdict` 分布 `needs_changes 7 / accepted 3`（共 10 行）
⇒ 这是**评审（evaluator）评估**表，**不是** Tracker 判定表。

## ⑧ 结论与对步骤的影响
| 项 | 结论 |
|---|---|
标准 1（state/reason/nextAction） | state+reason **已实现**；nextAction **有载体但为常量占位** ⇒ 需改为按 reason 派生 |
标准 2（可区分/不误杀） | verdict 分离 Waiting/Stalled **已实现**；长命令与僵死区分**未取证** |
标准 3（重试预算/token<5%） | 计数器载体已在 `goal_runs`；预算判定逻辑**未取证** |
标准 5（UI） | **未取证** |
标准 6（false 各<0.5%） | **前置缺口：判定未落库 ⇒ 不可度量** |
审计 A02（HeartbeatOutcome） | **未实现**；且 **fingerprint 无数据源** 是它的前置 |
审计「回放 8 次心跳分类正确」 | 需 ①的落库 + ③的 fingerprint 才能做 |

## ⑨ 诚实限定
- 本文全部为**只读事实**（DB 只读连接 + 源码检索）；**未改产品代码、未运行测试**。
- ①②⑦ 的结论以**表名/列名**检索为据；③⑤⑥ 为**全表计数**，无抽样偏差。
- 「未实现」「不可度量」均写明**验证范围**，不外推为整个系统级断言。
