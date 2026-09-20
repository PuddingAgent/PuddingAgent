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

---

## ⑩ step4 根因定位：`progress_fingerprint` 全 NULL 的**成因在 verifier 产出侧**

### 10.1 写入点存在，且条件守卫是根因
`Source/PuddingPlatform/Services/Goals/GoalSettlementStore.cs` 的 `ApplyProgressAccounting`，
其文档注释**逐字**声明：

> **P0-2（ADR-092 §7）：结算事务内的进度记账 —— `GoalRunEntity` 三个连续计数器与
> `last_progress_fingerprint` 的唯一写入点。**语义（逐条对应任务书 A）：
> ① 等待族（ComputeDisposition=wait…）整轮跳过：合法等待不是无进展；
> ② **指纹轴：新指纹缺失=证据不足（不动）**；与上次相同 ⇒ `ConsecutiveNoProgress++`；变化 ⇒ 归零并记录新指纹；
> ③ 阻塞轴：repair/stop 轮携带阻塞码 ⇒ 与上次相同 ++、不同归零；无阻塞码 ⇒ 归零；
> ④ 基础设施轴：repair 轮且阻塞码属 infra 族 ⇒ `ConsecutiveInfraFailures++`；成功（advance/complete）一次归零。

实现（逐字）：
```csharp
var nextFingerprint = decision.ProgressFingerprint;
if (!string.IsNullOrWhiteSpace(nextFingerprint)) {          // ← 守卫
    fingerprintChanged = !string.Equals(goal.LastProgressFingerprint, nextFingerprint, StringComparison.Ordinal);
    if (fingerprintChanged) { goal.ConsecutiveNoProgress = 0; goal.LastProgressFingerprint = nextFingerprint; }
    else { goal.ConsecutiveNoProgress++; }
}
```
⇒ **`last_progress_fingerprint` 只在 `decision.ProgressFingerprint` 非空时才写**，
而 DB 实测该列 **0/24 全 NULL** ⇒ **`GoalVerificationDecision.ProgressFingerprint` 从未被提供**。
**根因定位在「verifier 不产出指纹」侧，而不是存储侧。**

### 10.2 连带结论：熔断已实现，但**指纹轴从未生效**
- 熔断 blocker code 已定义：`private const string NoProgressCircuitOpenBlockerCode = "no_progress_circuit_open";`
- 三轴计数器记账（`ConsecutiveNoProgress` / `ConsecutiveSameBlocker` / `ConsecutiveInfraFailures`）
  都在结算事务内实现。
⇒ ADR-092 §7 的「无新进度」判定**当前实际只有「阻塞轴 + infra 轴」在工作**，指纹轴因源值恒空而空转。
⇒ **实现 `HeartbeatOutcome.before/after progressFingerprint` 的最小路径 = 让 verifier 产出 `ProgressFingerprint`**，
而非新建机制（符合审计 :273「不创建另一套状态机」）。

### 10.3 对 ① 的**复核**：标准 6 确认不可度量（结论加强）
`task_scheduler_scan_runs` 的**全部列**（逐字）：
`scan_id, workspace_id, trigger, mode, policy_revision, host_boot_id, status, started_at_utc, completed_at_utc,
duration_ms, availability_refreshed, idle_agents, busy_agents, unknown_agents, backlog, candidates, eligible,
started, tracked, repaired, decision_codes_json, repair_codes_json, error_code, error_summary`

⇒ **没有任何 verdict 分布列**（无 healthy/waiting/stalled/inconsistent/cleanup_required 计数）。
`TaskAutoDispatchScanSummary` 内确实**算了**这些计数（`TaskAutoDispatchScanRunner.RepairAsync` 逐字构造
`Healthy/Waiting/Stalled/Inconsistent/CleanupRequired` 计数），但**落库时被丢弃**。
⇒ **标准 6 既无逐条判定、也无聚合分布来源** —— ① 的结论**成立并加强**。

### 10.4 顺带：**五分钟节拍有运行时实证**（对标准 2/审计项有利）
连续三行 `started_at_utc`：`11:15:31.239` → `11:20:31.369` → `11:25:31.481`，**间隔恰 300 秒**；
`trigger=recovery_scan`、`mode=authoritative`、`tracked=1`、`repaired=0`、`repair_codes_json={}`。
全表 `tracked` 合计 635、单轮最大 2（共 1731 轮）⇒ 在途任务极少，repair 未触发。

### 10.5 `EvaluateCoreAsync` 的既有执行顺序（逐字注释）
> `// Ownership reconciliation must run before availability and selection.`

顺序：`tracking`（Tracker 判定）→ `repair`（**仅 authoritative**）→ availability 刷新 → backlog refinement →
candidate decisions → **决策持久化（写点①②）+ `next_eligible_at_utc` 写回（写点③）**，
其注释为「**决策持久化是观测能力，落库失败不拖垮扫描轮，记日志后继续**」。
⇒ **候选/refinement 决策有落库，但 Tracker 判定没有** ⇒ 与 ① 一致。

## ⑪ 本轮诚实限定
- 全部为**只读**（DB 只读连接 + 源码阅读）；**未改产品代码、未运行测试**。
- 10.3/10.4 为**全表或连续行**观测，无抽样偏差；10.1 为源码逐字 + 全表计数。
- 「verifier 不产出指纹」是**从「守卫 + 列全空」推得的因**；尚未读 verifier 实现本体，故命名为
  **待直接验证的根因**（下一步：读 `ConservativeGoalIterationVerifier` / `GoalVerificationDecision` 的产出点）。
