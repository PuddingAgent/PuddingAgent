# Scheduler Explore — Step 1 Checkpoint (checkpoint-v1)

- goalRunId: `tg-2efcdf7d329d8387632a67bcbfbdbe2b`｜objectiveVersion: 1｜iteration: 1
- taskId: `3bd2a4b0ef5f4bff8f175fb7655927ad`｜assignmentId: `d800b31c76644659b9d647a3c97428b0`
- stepNodeId: `tn-3ced31d2fa84e6c2fcc073eae9f6c2fd`（kind=Explore, sequenceNo=1/5）
- observedAtUtc: 2026-09-20T09:31–09:35Z｜repo HEAD: `2bc81fcc2a521c91c9a91aa3cd12789f09f04d83`（branch master, worktree clean）
- 证据文件: `Docs/Reports/scheduler-explore-evidence-r1.md`（301 行）

---

## 1. OBSERVE

- **goal 状态**：task 初始 `Assigned` v55 → 本次已 claim 为 `InProgress` v56（`task.accepted`）。`lastVerdict = null`（无上一轮未满足项需优先处理）。
- **acceptanceContract**：`null` ⇒ 本轮不产出 `meta.goal_contract_proposal`。
- **blockers（本轮开始时）**：无阻塞。任务卡自带「当前问题」章节，但其时效性未经验证 → 构成本轮主要勘察目标。
- **预算**：maxRounds 25 / maxToolCalls 60 / maxDuration 1800s；本轮到结算时用量见 §5。

## 2. PLAN

本迭代推进 **step 1（Explore）**：目标是「在决定改动前收集权威仓库与运行时证据」。判定标准 = 任务卡「当前问题」四条 + 「修改方案」六条逐项有 file:line 或库级证据支撑，能区分 **已落地 / 未落地 / 口径过时** 三类。

## 3. ACT

| 动作 | 方式 | 产出 |
|---|---|---|
| 代码/文档证据 | 1 个只读子代理（`deepseek/deepseek-flash`，工具限 file_read/file_search/search_grep/code_outline/list_dir/file_write），10 问结构化任务书 | `Docs/Reports/scheduler-explore-evidence-r1.md` |
| 运行时/生产库证据 | 自建只读探针 `temp/db-probe-sched-iter1.py`（`file:///…?mode=ro`） | §4.2 表行数与 Backlog 就绪度 |
| 配置实测 | 直读 `Source/PuddingAgent/appsettings.json` | §4.3 |
| 交叉核实 | 子代理由 `.git/refs/heads/master` 推得 HEAD vs 本机 `git rev-parse` | 一致 ✅ |

## 4. VERIFY（可复核证据）

### 4.1 任务卡「当前问题」逐条判定

| # | 任务卡断言 | 判定 | 证据 |
|---|---|---|---|
| P1 | `TaskAutoDispatchEvaluator` 只查 Ready/Deferred，Backlog 永不进候选 | ✅ **成立** | `TaskAutoDispatchEvaluator.cs:196`（SQL `status IN (Ready, Deferred)`）、`:242-245`（EF 同口径 + 排除容器母卡） |
| P2 | 候选**必须已填** `preferredAgentId` | ❌ **推翻** | `:190-205`/`:238-246` 候选过滤无 preferred 条件；`:355-360` 仅取 `hasPreferred` 用于硬亲和排序；`:371-384` 仅当 `routedAgents.Length==0` 才 Denied |
| P3 | `TaskAutoDispatch`/`TaskBoundGoals`/`GoalRuns`/`Continuation` 默认全部关闭 | ⚠️ **半成立（口径过时）** | 代码默认确实全关（`TaskAutoDispatchEvaluator.cs:17` Enabled=false、`:35` Mode=shadow、`:87` EventDriven=false）；**但仓库实测配置已全开**，见 §4.3 |
| P4 | 生产库四类表均为 0 | ❌ **推翻** | 实测非 0，见 §4.2 |
| P5 | `TaskDispatcher` 仅处理手工 outbox、发普通 Task message，非 Auto scheduler | ✅ **成立** | `TaskDispatcher.cs:34/56/85/166`；唯一写入点 `TaskCommandService.cs:361` 且 `:348 Origin=OriginTaskManual`；自动启动走 `TaskAutoDispatchStarter.cs:127 → TaskGoalDispatchTransactionStore.cs:47`（Serializable） |
| P6 | `TaskAutoDispatchWorker` 已有 bounded recovery scan + atomic StartGoalFromTask | ⚠️ **半成立** | recovery scan ✅（`TaskAutoDispatchWorker.cs:72-75` trigger=`recovery_scan`）；**无 `StartGoalFromTask` 方法**，真实入口 `TaskAutoDispatchStarter → TaskGoalDispatchTransactionStore.StartAsync`（`:45-47` Serializable 事务） |

### 4.2 运行时证据（`D:\data\databases\pudding_platform.db`，只读）

表总数 84；调度/目标相关 15 张表行数：

```
task_scheduler_scan_runs        1709     task_scheduler_intents            543
task_scheduler_decisions         911     task_scheduler_intent_outcomes    515
goal_runs                         22     goal_outbox                        43
goal_iterations                   43     goal_verifications                 42
task_goal_bindings                17     agent_execution_reservations       17
agent_availability_projection      8     task_dispatch_outbox               76
task_assignment_attempts          93     goal_acceptance_contracts          10
goal_check_records                20
```

⇒ P4 被推翻：**四类表全部有数据**，`task_scheduler_intents/outcomes` 亦已存在（对应卡内「剩余施工 commit 1」的 durable 表）。

`workspace_tasks` status 分布：`0=195 1=5 6=1 8=15 11=51`（合计 267）。

Backlog（status=0，n=195）就绪度：
- 缺 `acceptance_criteria`：**22**
- 缺 `preferred_agent_id`：**159**
- 类型分布：`general 179`｜implementation 8｜security 2｜research 2｜defect 2｜improvement 1｜frontend 1
- 优先级：p1 88｜p2 47｜p0 42｜p3 18

⇒ 验收标准 1 的「**34 个 Backlog**」口径已过时，实际 **195**。

### 4.3 配置实测（`Source/PuddingAgent/appsettings.json`）

| 节 | 行 | 实测值 |
|---|---|---|
| `GoalRuns` | :16 | `Enabled=true`, `ContinuationEnabled=true`, `DefaultMaxIterations=256`, `ContinuationScanInterval=00:00:05` |
| `TaskBoundGoals` | :26 | `Enabled=true`, `GoalIterationBudget=32`, `ReservationLease=02:00:00` |
| `TaskAutoDispatch` | :31 | `Enabled=true`, `EventDrivenEnabled=true`, **`Mode="authoritative"`**, `WorkspaceIds=["default"]`, `ScanInterval=00:05:00`, `MaxStartsPerScan=2`, `CandidateLimit=100` |
| `TaskTypeRoutes` | :39-62 | 7 类：implementation / test / deployment / research / review / documentation / operations —— **无 `general` 条目** |

### 4.4 「修改方案」六条现状

1. 唯一 `TaskSchedulingCoordinator` + durable intent：**部分已存在** — `Source/PuddingPlatform/Services/Scheduling/TaskSchedulingCoordinator.cs`（另有测试 `PuddingPlatformTests/Services/Scheduling/TaskSchedulingCoordinatorTests.cs`）；`task_scheduler_intents`/`_intent_outcomes` 表与 store 已存在；**`scheduler_outbox` NOT_FOUND**（同角色由 `task_scheduler_intents` 承担）。
2. Backlog Refinement：**已有通道** — `TaskAutoDispatchScanRunner.cs:276-286 PromoteBacklogAsync` → `TaskBacklogRefinementStore.cs:68`（置 Ready）。
3/4. 评分冻结、Availability/Lease/Fence：**持久化已就位** — `agent_availability_projection`、`agent_execution_reservations`（含 `fencing_token`/`lease_until_utc`）、`task_dependencies`、`goal_runs.activation_epoch`。
5. 唯一启动路径 Serializable 事务：**已落地** — `TaskGoalDispatchTransactionStore.cs:45-47`；类注释自述 "no network, model, tool or message send occurs inside the transaction"。
6. staged mode 三件套：**已存在并已铺开** — `NormalizeMode`/`IsAuthoritativeMode`/`EffectiveMaxStartsPerScan` 定义在 `TaskAutoDispatchEvaluator.cs`（options 类），调用点覆盖 scanner / coordinator / control / Goal 启动门 / 事件桥。

⇒ **任务卡整体为「施工前快照」，其描述的绝大部分能力已在后续提交中落地。**

## 5. 进度指纹

| 项 | 值 |
|---|---|
| repo HEAD | `2bc81fcc2a521c91c9a91aa3cd12789f09f04d83`（worktree clean） |
| 证据文件 | `Docs/Reports/scheduler-explore-evidence-r1.md`｜301 行｜15814 chars |
| 本 checkpoint | `Docs/Reports/scheduler-explore-checkpoint-step1-r1.md` |
| 关键库计数指纹 | scan_runs=1709 / decisions=911 / intents=543 / outcomes=515 / goal_runs=22 / bindings=17 |
| 配置指纹 | `Mode=authoritative`, `EventDrivenEnabled=true`, `MaxStartsPerScan=2`, `ContinuationScanInterval=00:00:05` |
| 工具用量 | 调用 7 次（含 1 次同步子代理），远低于 60 上限 |

## 6. 终端判定（本 step）

**Step 1（Explore）= COMPLETE**：任务卡断言已逐条取证，四项被推翻、两项半成立，能力清单与持久化现状有 file:line 与库级双重证据。
**Task 整体 = NOT COMPLETE**：step 2–5（Plan / Change / Test / Review）未开始。本判定为自述，终态由服务端 verifier 裁决。

## 7. 交给 step 2（Plan）的待决问题（本轮未验证，禁止假设）

1. **`general` 类型为何无执行计划**：`TaskExecutionPlanCompiler.SupportedTaskTypes` 含 `general`，但 `TaskTypeRoutes` **无** `general` 条目；`TaskAutoDispatchEvaluator.cs:319-320` 先 `TryGetValue` 再 `TryCompile`，缺失路由时的行为未实测。**这直接决定 179/195 张 Backlog（general）能否进入 Ready** —— 与验收标准 1 强相关。需判定：(a) 为 general 定义可编译工作单元集合，还是 (b) 结构化拒绝 + 稳定 reason code。
2. **「剩余施工两个 commit」是否已实质完成**：`task_scheduler_intent_outcomes` 与三件套复用均已就位，需逐条比对设计文档 §5–§6/§11.2–§11.3/§12 与本仓现状（doc:464 行，§5=147 / §6=202 / §11.2=350 / §11.3=360 / §12=398）。
3. **本任务为何在 09:30:27Z 被 Reserved/Assigned**（事件 seq 54/55）：自动调度 vs 手工派发未定性；`task_scheduler_decisions`(911) 中应有对应记录，需回放核对 —— 这是验收标准 5「可回放对账」的现成样本。
4. **验收标准口径需重定**：标准 1「34 个」→ 195；标准 4「authoritative-single」→ 实测已 `authoritative`。口径变更需确认后再据以验收。

## 8. 诚实限定

- 子代理无 shell，其 HEAD 取自 `.git/HEAD` + `.git/refs/heads/master`（未排除 packed-refs），已由本机 `git rev-parse` 交叉核实一致。
- 表行数为**当前时刻快照**，非审计期基线。
- `Source/PuddingAgent/appsettings.json` 今日 00:27:10Z 曾变更，§4.3 反映**当前工作区**值。
- 未运行构建、未跑测试、未修改任何 `Source/**`。
