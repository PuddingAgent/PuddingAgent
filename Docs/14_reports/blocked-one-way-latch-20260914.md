# Blocked 单向闩锁：修复器只写 Blocked，worker 侧永不可恢复

- 日期：2026-09-14 03:10 BJT
- 类型：平台可靠性缺陷（代码级审计 + 生产事件链取证）
- 缺陷卡：`e2c35d6eeae244c191ed508ccd85b6fe`（P0）
- 关联卡：`bf7ef5f5e2d04d23ac3aed224ee539c3`（打通 Blocked 逃生通道 / 16 张僵死卡）、`813ad427c0d54fd6a67e9bd39b03d4c4`（Blocked+active assignment 不可上报，已修复 commit `5bf1792`）

## 一、结论

卡一旦进入「`status=Blocked` + `ActiveAssignmentId=null` + `attempt.ReleasedAtUtc!=null`」这一组合态：

- **worker 侧（`task_claim` / `task_update`）无法恢复**——上下文缺失时的反查必须命中归属当前 Agent 的 active assignment，而该字段已被清空；
- **任何自动路径也无法选中它**——派发扫描只取 `Ready|Deferred`，Tracker 候选要求 `ActiveAssignmentId == attempt.AttemptId`；
- **唯一通道是管理面** `manage_tasks` `resume` / `requeue`（或等价 Admin API）。

即「Blocked 是单向门」，与设计文档 §8「Blocked 逃生通道」相冲突。

## 二、代码证据（file:line）

### 2.1 修复器只写 Blocked，从不 re-arm

`Source/PuddingPlatform/Services/Scheduling/TaskExecutionRepairCoordinator.cs`

| 行 | 行为 |
|---|---|
| `:201-205` | `if (CanTransition(task.Status, Blocked)) task.Status = Blocked; else if (task.Status is not (Blocked or NeedsReview)) return RollbackFalse;` |
| `:206-218` | 写 `BlockerKind = "assignment_execution_missing" \| "delivery_terminal_without_execution"` + `BlockerReason` |
| `:219` | `task.ActiveAssignmentId = null;` |
| `:220-222` | `Version++` / `UpdatedAtUtc` / `UpdatedBy = "task-execution-repair"` |
| `:223-226` | `assignment.Status = Failed; assignment.ReleasedAtUtc = now;`（`:227-236` 仅释放 `GoalRunId == null` 的 legacy reservation） |
| `:237-245 / :260-271` | 追加 `TaskBlocked` 事件（EventId 前缀 `tracker-legacy-blocked-*`） |

判定源：`Scheduling/LegacyTaskExecutionProbe.cs:78-84` → `legacy_assignment_execution_missing`。
该路径**不写** `TaskGoalBinding` / `GoalRun` / `GoalOutbox` / 新 assignment，也**不把卡放回 Ready**。

### 2.2 终态后不再被任何扫描选中

- `Services/Scheduling/TaskExecutionTracker.cs:176-177`：`&& attempt.ReleasedAtUtc == null && task.ActiveAssignmentId == attempt.AttemptId` → 上述终态不再匹配。
- `Services/Scheduling/TaskAutoDispatchEvaluator.cs:196`：`AND status IN (Ready, Deferred)` → Blocked 永不再进派发线。

### 2.3 worker 侧无路径

- `Source/PuddingRuntime/Services/TaskTools/TaskClaimTool.cs:47`：`allowBlockedRecovery: false`
- `Source/PuddingRuntime/Services/TaskTools/TaskUpdateTool.cs:47`：`allowBlockedRecovery: true`
- 两者共用 `TaskToolModels.cs:303-310` 状态门槛：`statusOk = allowBlockedRecovery ? InProgress|Blocked : Assigned|InProgress`
- Blocked 态仅 `disposition = todo` 可 → Ready（`TaskAgentCommandService.cs:360-380`，`:369 case Todo: task.ActiveAssignmentId = null`）
- `ActiveAssignmentId` 为空 → 反查必然失败（`task.not_found` / `task.state_conflict`）

### 2.4 生产配置使修复器处于活跃状态

`Source/PuddingAgent/appsettings.json:31-35`：`EventDrivenEnabled=true`、`Enabled=true`、`Mode=authoritative`。
（C# 默认见 `TaskAutoDispatchEvaluator.cs:12-17` `Enabled` 注释 "Master switch. Default false"、`:31 Mode = "shadow"`；修复仅在 authoritative 生效：`TaskAutoDispatchScanRunner.cs:39`、`:119-120`。）
→ 修复器**会**执行，且只会把卡推向不可恢复态，不会 re-arm。

## 三、现场取证（卡 `3bd2a4b0ef5f4bff8f175fb7655927ad`，P0 统一 Scheduler 内核）

事件链（UTC，`task_events`）：

| seq | 时间 | 事件 | assignment |
|---|---|---|---|
| 40 | 09-13T02:17:49 | `task.blocked`（`tracker-legacy-blocked-*`，blocker_kind=assignment_execution_missing） | `7291d4ec…` |
| 41 | 09-13T12:21:12 | `task.ready` | — |
| 42/43 | 09-13T12:28:08 | `task.reserved` + `task.assigned` | `dd4cc49901ed435fbb9c17a4c3b84faa` |
| 44 | 09-13T12:28:16 | `task.accepted` | `dd4cc499…` |
| 45 | 09-13T12:29:50 | `task.blocked`（`tgb-*`，GoalSettlementStore 路径） | `dd4cc499…` |
| 46 | 09-13T19:12:18 | `task.ready`（本次管理面 resume 产生） | — |

- 认领后**仅 102 秒**即被打回 Blocked（seq44→seq45）。
- Agent 侧交叉验证：`task_get(3bd2a4b0)` → `task.not_found`；`task_list(mine)` 不含任何非 Completed 卡 → 与「`ActiveAssignmentId` 已释放」一致。

## 四、已执行的处置（可回滚）

1. `manage_tasks action=resume`（任务 `3bd2a4b0`）：`Blocked v45` → `Ready` / `board_column=Todo`，新事件 seq46 `task.ready` @ 2026-09-13T19:12:18Z。
   目的：重新 arm 派发，让 2026-09-13 08:47 desktop-build 已加载的 token 轴修复（commit `71c53d9` / `0ac3b57` / `898e943`）获得首个真实 run（此前从未被真实执行验证）。
2. 新增缺陷卡 `e2c35d6eeae244c191ed508ccd85b6fe`（P0），收录本报告全部证据与修复方向。

## 五、修复方向（候选，未实施）

- **(a) 修复器 re-arm**：`TaskExecutionRepairCoordinator` 在「legacy + 无 `TaskGoalBinding`」时应回到 `Ready`（或 `Deferred` + `nextEligibleAtUtc`），而不是单向闩锁；仅 `needs_input` 类才挂起并停止扫描。
- **(b) worker 受控逃生**：`Blocked + ActiveAssignmentId==null` 时允许 requeue 语义（受策略审批约束），避免「只能靠管理者手工改状态」。
- **(c) 定时 sweep**：统计「`Blocked` + `ActiveAssignmentId==null` + 无 binding」的僵死卡并自动 requeue（当前 6 张 Blocked + 多张 NeedsReview 属同类）。

## 六、验收标准

1. 模拟 seq42→seq45（ready/reserved/assigned/accepted/blocked）序列后，卡能被自动或命令式恢复到 Ready（可重试）或明确挂起（需人工），**不依赖管理者手工改状态**。
2. re-arm 路径幂等：不产生重复 GoalRun / Assignment / Reservation（含 SQLite 重启恢复 + 双 worker 竞争测试）。
3. `Status` 为唯一事实源、`BoardColumn` 为投影；`Blocked + InProgress` 组合不再出现，并有双口径一致性校验测试。
4. `task_get` / `task_update` 在「Blocked 且无 active assignment」下的行为契约化并测试覆盖。

## 七、回归验证点（下轮心跳）

- `3bd2a4b0` 是否出现 `task.reserved` + `task.assigned`，随后是否出现 `task.accepted` 且不再在 ~100 秒内被 `tgb-*` 置 Blocked。
- 若再次出现同一签名 → 坐实「assignment 已派发但无 canonical execution 认领」为独立 P0 平台缺陷（与 token 轴无关）。
