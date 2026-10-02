# Task Tracker/Watchdog Explore — Step 1 Checkpoint (checkpoint-v1)

- goalRunId `tg-cbdced5fef984688c568d8aa045cfe77`｜iteration 1｜objectiveVersion 1
- taskId `77883a50d4c8453cbd05c38ee1719f0e`｜assignmentId `19290b6f37ed4718b01bc94de439e946`｜**已认领**（v26 → v27，InProgress）
- planId `tp-6455158554163827b2d1899d06c3148f`｜stepNodeId `tn-99cd5f5c95da040ff3c837630fc00b05`（Explore, 1/5）
- 观测时 HEAD：`c3bca44f` 之后（worktree clean）

---

## 1. OBSERVE
- `lastVerdict` = **null**（首次迭代，无需推进既有 unmetCriteria）；`acceptanceContract` = null。
- 任务卡有 **6 条验收标准** ＋ 本次审计新增 5 项；父任务 `6f49d33e…`；关联卡 `4ed930e7…`（Repair 归属）。

## 2. PLAN
判据 = 卡片 6 条标准 + 审计新增项，**逐条对应到「已实现（含位置/测试名）」或「未实现（字面检索为零）」**。

## 3. ACT
只读取证：`file_search` ×2、定向 `search_grep` ×4、`file_read` ×2、`task_claim` ×1。未改代码、未运行测试。

## 4. VERIFY

### 4.1 主体已实现：`Source/PuddingPlatform/Services/Scheduling/TaskExecutionTracker.cs`（422 行）
类注释逐字：「Correlates Task, Assignment, Reservation, Goal, Iteration, command/run and outbox facts.
**This evaluator is deliberately read-only**: a later repair coordinator must re-read every fence before it may mutate authoritative state.」

关联的 canonical 事实（表）：`task_goal_bindings`(status=active) → `workspace_tasks` / `task_assignment_attempts` /
`goal_runs` / `agent_execution_reservations` / `task_plan_runs` / **`task_nodes`（depth=1，取首个非终态为 current WorkUnit）** /
`goal_iterations` / `goal_outbox`(Kind=Continuation) / `chat_execution_commands` / `execution_runs`。
「最新」口径：iteration 按 `ActivationEpoch↓, IterationNo↓, CreatedAtUtc↓`；outbox 按 `ActivationEpoch↓, CreatedAtUtc↓`；
run 按 `Attempt↓, FencingToken↓`。`LastProgressAtUtc` 在 `:232` / `:311` 赋值。
停滞判定：`IsOverdue(lastProgress, now) => lastProgress is not null && now - lastProgress > _options.TrackerStallThreshold`。

### 4.2 判定集合（逐字 reason，覆盖验收标准 1 的「可解释 state/reason」面）

| verdict | reasons |
|---|---|
**Healthy** | `iteration_running` |
**Waiting** | `goal_paused`、`continuation_pending`、`continuation_leased`、`execution_pending`、`settlement_projection_pending`、`next_iteration_pending` |
**Stalled** | `continuation_intent_missing`、`continuation_pending_overdue`、`continuation_lease_expired`、`continuation_dead_lettered`、`continuation_cancelled`、`command_terminal_iteration_open`、`run_terminal_iteration_open`、`iteration_terminal_fact_overdue`、`settlement_projection_overdue`、`next_iteration_intent_missing`、`iteration_terminal_goal_active` |
**Inconsistent** | `goal_phase_unsupported`、`accepted_iteration_missing`、`continuation_status_unknown`、`execution_command_missing`、`iteration_status_unknown` |

⇒ **每条判定都携带 (verdict, reason)**，且 `Waiting` 与 `Stalled` 明确分离（对应标准 2 的「无进展/等待可区分」）。

### 4.3 测试覆盖（`Source/PuddingPlatformTests/Services/Scheduling/TaskExecutionTrackerTests.cs`，逐字名）
- 状态系列：`PendingContinuation_IsWaitingAndReadOnly`、`ExpiredReservation_IsStalled`、
  `TerminalCommandWithOpenIteration_IsStalled`、`AssignmentFenceMismatch_IsInconsistent`、
  `TerminalTaskWithActiveBinding_RequiresCleanup`、`BoundExecutionPlan_ProjectsCurrentWorkUnit`、
  `MissingBoundExecutionPlan_IsInconsistent`
- **Legacy 系列（对应卡片"legacy claim 必须 join canonical execution_runs.status"与"禁止有 id 即永久 Healthy"）**：
  `LegacyTerminalRun_WithinGraceWaitsForSettlement`、`LegacyOrphanedClaim_IsNotHealthyAndIsReleasedAfterGrace`、
  `LegacyTerminalRun_ReleasesOwnershipWithoutInventingTaskSuccess`、
  `LegacyTerminalRun_RepairRejectsNewAttemptAfterEvaluation`、`LegacyRepair_RejectsChangedTaskVersion`、
  `LegacyCommandClaim_WithMultipleAttemptsUsesLatestAttempt`、
  `LegacyTerminalRun_WithRetryPendingDoesNotReleaseAssignment`、
  `RepairCoordinator_ReleasesDeliveredLegacyAssignmentWithoutExecutionClaim`、
  `RepairCoordinator_ReleasesTerminalLegacyDeliveryWithoutWaitingForThreshold`、
  `RepairCoordinator_RecoversExpiredContinuationLease`、`RepairCoordinator_CleansTerminalBindingAndAssignment`、
  `RepairCoordinator_CleansBlockedGoalBindingAndReleasesAgentOwnership`
  ⇒ 标准 4 的恢复测试面（crash/lease expiry/late terminal/cancel-complete race）**已显著覆盖**。

### 4.4 缺口（**字面检索为零** ⇒ 未以该命名实现；不排除换名实现，故第 2 步须用状态枚举逐项核对）
| 缺口 | 证据 |
|---|---|
**`HeartbeatOutcome` 不存在** | 在 `Source/PuddingRuntime` 与 `Source/PuddingPlatform` 检索均**零命中** ⇒ 审计新增项（trigger/command/turn/task/workunit、before/after progressFingerprint、evidence、outcome、waitHandle、nextEligibleAt）**未实现** |
**`SoftStalled` / `Replanning` 字面零命中** | 卡片状态表中的 `soft_stalled`/`replanning` 未以该命名出现；实际判定集为 Healthy/Waiting/Stalled/Inconsistent |
标准 3（重试预算、token 占比 <5%） | 未取得代码或测量证据 |
标准 5（UI 展示 canonical progress/WorkUnit/等待原因/最近证据/重试预算） | 未取证 |
标准 6（false running/failed/completed 各 <0.5%） | 未取得测量口径 |
标准 1 的 **nextAction** 面 | `TaskExecutionTrackingDecision`（`Source/PuddingCore/Scheduling/TaskExecutionTrackingContracts.cs:19`）**字段未读** ⇒ 待核 |

### 4.5 可直接复用的运行时基线（本会话既有只读实测）
`task_scheduler_scan_runs=1709`、`decisions=911`、`intents=543`、`intent_outcomes=517`（517 distinct intent，重复结算/重复启动三项断言全 0）；
reason 分布 12 类（`not_opted_in` 433 / `completed` 27 / `availability_refreshed` 18 / `status_blocked` 16 / …）；
outcome 分布 5 类；`agent_availability_projection` 8 行（state/reason/idle_since）；`preferred_busy` 自锁已量化。
⇒ 可作为标准 1/6 与「回放 8 次心跳分类正确」的**基线数据**。

## 5. 终端判定
- **Step 1（Explore）= COMPLETE**：已给出「已实现（位置+测试名）/ 未实现（零命中）/ 待核」三类结论。
- **Task = NOT COMPLETE**：step 2–5 未开始。**自述判定，终局由服务端 verifier 裁决。**

## 6. 诚实限定
- **字面检索零命中 ≠ 功能不存在**（可能换名/换粒度实现），故 §4.4 两条标为「未以该命名实现，待第 2 步逐项核对」。
- 本轮未运行测试套件、未改任何代码；测试名来自静态检索，不等于「当前全绿」。
