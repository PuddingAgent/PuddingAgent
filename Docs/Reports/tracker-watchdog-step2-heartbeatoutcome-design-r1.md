# step2 设计草案：`HeartbeatOutcome` 投影 + 标准 6 可测量口径（r1）

- Goal `tg-cbdced5fef984688c568d8aa045cfe77`｜Task `77883a50d4c8453cbd05c38ee1719f0e`｜iteration 3
- step 节点 `tn-99cd5f5c95da040ff3c837630fc00b05`（Explore 1/5）

---

## 1. 权威规格（**引审计报告逐字**）

`Docs/Reports/PuddingAgent-Autonomy-Audit-2026-09-12/01-自主工作轨迹与自改进审计.md:200-205`：

> ### A02 / P0：心跳必须交付进度回执，检查点由事实生成
> - 建立 `HeartbeatOutcome`（**可作为现有 event payload 而非新孤立状态机**）：heartbeatId、command/turn、
>   selectedTask/WorkUnit、before/after progressFingerprint、artifact/test/commit refs、outcome、waitHandle、
>   nextEligibleAt、cost。outcome 至少区分 **progressed、verified_completed、waiting_external、
>   no_eligible_work、maintenance_only、failed/stalled**。

同文件 `:273` 给出**落点约束**（逐字）：
`TaskExecutionTracker.cs | 真实 progress、wait/stall 对账、HeartbeatOutcome 投影 | 不创建另一套 Task/Goal 状态机`

⇒ **设计原则：`HeartbeatOutcome` 是对现有 canonical 事实的「投影/回执」，不是新状态机。**

## 2. 字段 → canonical 来源映射（每个字段都必须有事实来源，禁止模型猜测）

| 字段 | canonical 来源（表/列） |
|---|---|
`heartbeatId` | 心跳投递消息 id（`metadata.message_id`） |
`command` / `turn` | `chat_execution_commands.command_id` / `execution_runs`（经 `goal_iterations.command_id` 关联） |
`selectedTask` | `task_goal_bindings.task_id`（status=active） |
`selectedWorkUnit` | `task_nodes`（`depth=1` 首个非终态）→ 现由 `TaskExecutionTracker` 投影为 `WorkUnitKind/WorkUnitStatus` |
`before/after progressFingerprint` | `task_nodes.progress_fingerprint`、`goal_iterations.progress_fingerprint`、`goal_runs.last_progress_fingerprint`（**唯一写入点**见 `GoalSettlementStore.cs:1529` 注释） |
`artifact / test / commit refs` | `goal_check_records`（bounded:build / bounded:test 的 status+failure_code）、`task_nodes.result_artifact_ref`、git commit sha |
`outcome` | **派生**：由 `TaskExecutionTrackingVerdict` + `Code` + 本轮前后 fingerprint 变化共同决定（见 §3） |
`waitHandle` | `goal_outbox`（Kind=Continuation 的 status/lease）+ `agent_execution_reservations` lease |
`nextEligibleAt` | 等待对象的到期时间（outbox `LeaseUntilUtc`、reservation `LeaseUntilUtc`）；有可执行工作时按 ≤5 分钟 |
`cost` | `goal_iterations` / `execution_runs` 的 token/cost 账本（与 `task_nodes.max_cost` 对比） |

## 3. `outcome` 六分类 → 现有 verdict/reason 的映射（**发现两处缺口**）

| outcome（审计要求） | 现有可判定来源 | 状态 |
|---|---|---|
`progressed` | verdict=Healthy/… 且 `progressFingerprint` **发生变化** | ✅ 可派生（需引入 before/after 比较） |
`verified_completed` | verdict 进入终态且 `goal_check_records` 全通过（`failure_code=null`） | ✅ 可派生 |
`waiting_external` | verdict=Waiting（`goal_paused`/`continuation_pending`/`continuation_leased`/`execution_pending`/`settlement_projection_pending`/`next_iteration_pending`） | ✅ 已有 6 个 reason |
`failed/stalled` | verdict=Stalled（11 个 reason）/Inconsistent（5 个 reason） | ✅ 已有 |
**`no_eligible_work`** | **不在 Tracker verdict 内**；事实源在 **Scheduler 侧** `task_scheduler_intent_outcomes.reason_code`（`not_opted_in`/`preferred_busy`/`task_not_yet_eligible`/`status_backlog`…） | ⚠️ **缺口：需跨层投影** |
**`maintenance_only`** | **无对应事实源**（本轮只做了记忆/文档固化、未推进业务对象） | ⚠️ **缺口：需定义判定口径** |

⇒ 设计要点：`HeartbeatOutcome` 的 `outcome` **必须允许由 Tracker + Scheduler 两侧事实共同决定**，
否则会把「没有可做的任务」误分类成 `failed_stalled`，或把「只写了纪要」误报成 `progressed`。

## 4.「工具成功或写纪要**不得**刷新业务 `lastProgressAt`」的判定口径

`LastProgressAtUtc` 现由 `TaskExecutionTracker` 从 canonical 事实推导（`:232`/`:311`）。判定规则（建议逐字落地）：

```
业务进度推进 ⇔ 以下任一为真
  a) task_nodes.progress_fingerprint 变化（对应 current WorkUnit）
  b) goal_iterations.progress_fingerprint / goal_runs.last_progress_fingerprint 变化
  c) task_nodes.status / goal_iterations.status / execution_runs.status 进入新的 canonical 终态
  d) result_artifact_ref / goal_check_records 新增有效产物

下列**不构成**推进（即使工具调用成功）：
  × 写入文档/纪要到 Docs/ 或 memory/（无 fingerprint 变化）
  × 仅 goal_update / save_memory / sleep
  × 检索、只读查询、git status/log
```
⇒ 与审计原话一致：「**20:34 确实有修改构建，不应因最终纪要格式误判无效**」——
即**纪要格式不参与判定**，只看 canonical 事实增量。

## 5. 验收标准 6（false running / false failed / false completed 各 <0.5%）的**可测量口径**

**定义（分子/分母必须都写明）**

| 误判 | 定义 | 可判定事实 |
|---|---|---|
false running | 在 tick T 被判 `Healthy(iteration_running)`，但**同一 T** 其 reservation / execution_run 已终态或 lease 已过期 | `execution_runs.status`、`agent_execution_reservations.LeaseUntilUtc` |
false failed | Task/Goal 被判 `Failed`，但其后**无新 evidence** 又成功续行，或事实显示从未处于终态 | `workspace_tasks.status`、`goal_runs.status/TerminalAtUtc`、`goal_outbox` |
false completed | Task 判 `Completed` 而 binding 仍 `active` / 仍有非终态 work unit | `task_goal_bindings.status`、`task_nodes.status` |

**分母**：每个 tick 的「在途任务数 × tick 数」（现基线 `task_scheduler_scan_runs=1709`、`decisions=911`）。
**数据源**：`TaskExecutionTracker` 逐 tick 的 decisions（**⚠️ 待查：当前是否有落库表**；若无落库，则标准 6 无法离线度量 ⇒ 本身就是缺口）。
**已有可用基线**：`intents=543`、`intent_outcomes=517`（517 distinct，重复结算/重复启动断言全 0）。

## 6. 缺口清单（供 step3+ 排期）
1. `HeartbeatOutcome` **未实现**（6 工程检索无该类型；审计要求的字段无载体）。
2. `TaskExecutionTrackingDecision` **无 `NextAction`** ⇒ 标准 1 只满足 state/reason 两项。
3. `no_eligible_work` / `maintenance_only` **无判定口径**（跨 Tracker/Scheduler 层的投影缺失）。
4. **tracker decisions 是否落库待查** —— 直接决定标准 6 能否度量。
5. `task_nodes.progress_fingerprint` 两 plan 全 NULL（疑未填充；`goal_iterations`/`goal_runs` 对应列未查）。
6. `soft_stalled`（2 tick）/`replanning`（3 tick、一次 checkpoint+replan、无新证据禁止重复）**未以该命名实现**。

## 7. 诚实限定
- 本文是**设计草案（Plan）**，不是实现，也**不是**产品验收；产品验收须在真实运行中度量。
- `HeartbeatOutcome` 字段映射基于静态检索与既有实测数据；**未运行测试、未改产品代码**。
- 「发出者定位」为**负结果**：已验证 6 个可能执行 work-unit 预算的工程（Runtime/Platform/Core/Agent/Host/Controller）
  均无该字面量；**未穷举其余工程**，故只能收敛为「当前源码中无产生它的活代码」。
