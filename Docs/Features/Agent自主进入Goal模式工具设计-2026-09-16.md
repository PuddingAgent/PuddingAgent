# Agent 自主进入 Goal 模式：`task_goal_start` 工具设计

> **2026-09-17 上位设计更新**：受授权Task启动入口保留，启动后的Goal生命周期、合同和Task适配以 [Goal模式简化设计](Goal目标驱动执行与分层验证闭环设计-2026-09-15.md) 为准。不得为本工具另建续行循环或绕过任务权限；本文历史代码位置与实现状态需按当前源码复核。

- 日期：2026-09-16
- 状态：**设计定稿**（待实现）
- 触发者需求（原话）：「你可以升级一个工具，让你自己可以自主的进入 Goal 模式。**而不是绕过 admin 的 API 的授权机制**。」
- 前置事实：本文所有 file:line 均来自本轮只读侦察（`temp` 外的原文证据见 `.pudding/context-tool-results/.../smart_explore.txt`，侦察结论已逐条复核）

---

## 1. 问题陈述（为什么现在做不到）

看板卡要跑成**任务绑定 Goal**，全链路只有一个入口：

```
调度扫描 TaskAutoDispatchScanRunner
  → TaskAutoDispatchStarter.DispatchDetailedAsync(...)
  → StartGoalFromTaskCommand
  → TaskGoalDispatchTransactionStore.StartAsync()
```

`StartGoalFromTaskCommand` 在**全仓唯一生产者**就是 `TaskAutoDispatchStarter`（`Services/Scheduling/TaskAutoDispatchStarter.cs:127`）。
而这条链的**唯一闸门**是 `workspace_tasks.auto_dispatch_enabled = 1`：

| 位置 | 证据 |
|---|---|
| `Services/Scheduling/TaskAutoDispatchEvaluator.cs:197` | 候选 SQL `AND auto_dispatch_enabled = 1` |
| `Services/Scheduling/TaskAutoDispatchEvaluator.cs:239` | `&& entity.AutoDispatchEnabled` |
| `Services/Goals/TaskGoalDispatchTransactionStore.cs:80` | `\|\| !task.AutoDispatchEnabled` ⇒ 拒绝 |
| `Services/Scheduling/TaskSchedulingCoordinator.cs:203` | `_ when !task.AutoDispatchEnabled` ⇒ 拒绝裁决 |

⇒ **结论**：Agent 侧没有任何工具能进入 Goal 模式；`manage_tasks` 的 schema 既不含 `auto_dispatch_enabled`，也无 scan 动作（实测「透传该字段被静默忽略」）。

**修法**：新增一个**正规工具**，复用平台既有的评估/派发与授权校验，**不新增 HTTP 端点、不读 admin token、不改 `[Authorize]`**。

---

## 2. 设计原则（不可违反）

1. **不绕过 admin 授权**：不伪造角色、不读管理凭据、不新增匿名/提权端点；工具自身的授权语义由**任务级所有权 + 调度器闸门**表达。
2. **复用 canonical 路径**：只调用 `TaskAutoDispatchEvaluator` + `TaskAutoDispatchStarter` + `TaskCommandService`，**不复制围栏逻辑、不新建第二条启动路径**。
3. **fail-closed**：任一前置校验不通过 ⇒ **不创建 Goal**，返回结构化拒绝码与原因。
4. **幂等**：同一 task 已有活跃任务绑定 Goal ⇒ 返回既有 `goalRunId`，不重复创建。
5. **不扩大影响面**：只派**这一张卡**（`maxStartsOverride: 1`），绝不触发全量扫描（否则会顺带启动其它卡）。
6. **不抬高预算**：`iteration_budget` 只能取「用户请求」与「配置 `TaskBoundGoals.GoalIterationBudget`」的**较小值**。

---

## 3. 三层落点

| 层 | 文件（新增） | 内容 |
|---|---|---|
| **PuddingCore** | `Source/PuddingCore/Tasks/TaskGoalLaunchContracts.cs` | `ITaskGoalLaunchService` 端口 + `TaskGoalLaunchRequest` / `TaskGoalLaunchResult` + `TaskGoalLaunchCodes` 错误码常量 |
| **PuddingPlatform** | `Source/PuddingPlatform/Services/Goals/TaskGoalLaunchService.cs` | 实现：前置校验 → 开启开关 → 评估候选 → 派发 |
| **PuddingRuntime** | `Source/PuddingRuntime/Services/TaskTools/TaskGoalStartTool.cs` | 工具 `task_goal_start`（`ToolCategory.Orchestration`，`ToolPermissionLevel.Low`） |
| **Host** | 组合根**追加一行**注册 | `AddSingleton<ITaskGoalLaunchService, TaskGoalLaunchService>()`。**该行由父级统一追加**（见 §7 双写纪律） |

**工具不需要改组合根即可被发现**：工具靠 `AddPuddingToolsFromAssembly` 反射扫描自动注册（`manage_tasks` 在 Host 里就没有显式注册行），因此**工具可见性无需改组合根，也无需改 agent 模板白名单**（既有证据：`todo_*` 三个工具重启后立即可用，未动 `agent-template-presets`）。

**分层纪律**：`PuddingRuntime.csproj` 的 ProjectReference **不含 PuddingPlatform** ⇒ 工具**只能**依赖 Core 端口（与 `manage_tasks` 注入 `IWorkspaceTaskAdminService`、`todo_write` 注入 `ITodoStore` 同构）。`ITaskAutoDispatchStarter` 是 **Platform 类型，工具层不可见**，这正是必须新开 Core 端口的原因。

---

## 4. 工具契约

```
task_goal_start(
  task_id:          string   （必填）
  expected_version: int?     （可选，CAS；不符 ⇒ task.version_conflict）
  iteration_budget: int?     （可选，clamp 规则见 §2.6）
  reason:           string?  （可选，写入事件溯源）
)
```

- 身份与作用域**全部取自运行时上下文**，不接受调用方传入：`context.WorkspaceId`、`context.AgentInstanceId`、`context.SessionId`
  （取证：`ManageTasksTool.cs:52-53` 用 `context.WorkspaceId` / `context.AgentInstanceId`；`ActiveTaskRuntimeContext`（`Source/PuddingCore/Tasks/ActiveTaskRuntimeContext.cs:13-52`）**本身不含 ConversationId**，而 Starter 需要 `ConversationId` ⇒ 由 `context.SessionId` 提供）。

**返回**（`TaskGoalLaunchResult`）：

```
Started: bool
Code:    string            （见 §5 码表）
GoalRunId?, AssignmentId?, ReservationId?, TaskPlanId?, TaskVersion?
Message: string            （人类可读，中文）
```

---

## 5. 校验顺序与拒绝码（fail-closed）

| # | 校验 | 不通过时 Code |
|---|---|---|
| 1 | workspace 匹配、任务存在 | `task.not_found` |
| 2 | `expected_version` CAS（若提供） | `task.version_conflict` |
| 3 | 状态 ∈ {Backlog, Ready}（拒绝 Completed/Cancelled/Archived/NeedsReview/InProgress） | `task_not_dispatchable` |
| 4 | **授权**：`ActiveAssignmentId` 为空 **或** 该 assignment 的 `AgentId == 自己` | `task_held_by_other_agent` |
| 5 | **路由尊重**：`PreferredAgentId` 为空 **或** == 自己 | `task_preferred_agent_mismatch` |
| 6 | 依赖满足（复用 store 既有判定 `DependenciesStillSatisfiedAsync`，`TaskGoalDispatchTransactionStore.cs:530`） | `dependencies_unmet` |
| 7 | **调度器闸门**：`TaskAutoDispatchOptions.Enabled == true`、`Mode` ∈ authoritative 家族、workspace ∉ `PausedWorkspaceIds` | `scheduler_disabled` / `scheduler_not_authoritative` / `scheduler_paused` |
| 8 | 幂等：该 task 已有活跃任务绑定 Goal | **`already_running`（Started=true，返回既有 GoalRunId）** |
| 9 | 评估后候选非 Eligible | 直接透传 `TaskAutoDispatchCandidateDecision.Verdict` 对应码（如 `agent_busy`/`agent_unavailable`/`window_refused`），**不创建 Goal** |
| 10 | 派发被拒 | 透传 `TaskAutoDispatchStartOutcome.Code` |

> **第 7 条是本设计的关键风险点**：`mode`/`PausedWorkspaceIds` 闸门位于**调用方**（`TaskAutoDispatchScanRunner.RunAsync:39-42` / `TaskSchedulingCoordinator`），**不在 `TaskAutoDispatchStarter` 内部**。若只调用 Starter 而不自检第 7 条，就等于**绕过了调度器的全局开关**。必须显式校验。

---

## 6. 执行序列（实现步骤）

```
1. 校验 §5 第 1–7 条（任一失败即返回，不产生任何写入）
2. 幂等检查（第 8 条）：命中即返回既有 GoalRunId
3. 确保 auto_dispatch_enabled = true
     入口：TaskAdminUpdateRequest.AutoDispatchEnabled
           → WorkspaceTaskAdminService.UpdateTaskAsync
           → TaskCommandService.PatchAsync(..., autoDispatchEnabled)
     （取证：WorkspaceTaskAdminService.cs:65 / :199、TaskCommandService.cs:50/:91/:165-166）
     说明：这是**任务级 opt-in**，是 canonical 派发的前置条件，故必须设置；
           设置动作走既有服务，不直改数据库。
4. 评估：TaskAutoDispatchEvaluator.EvaluateAsync(workspaceId, limit, ct)
           取 TaskId == 目标卡的决策；非 Eligible ⇒ 返回第 9 条
5. 派发：ITaskAutoDispatchStarter.DispatchDetailedAsync(
             [该决策], maxStartsOverride: 1, ct)          ← 只派这一张
     签名取证：TaskAutoDispatchStarter.cs:31-35
     结果类型：TaskAutoDispatchStartOutcome { TaskId, Started, Code, AgentId?, AssignmentId?, GoalRunId? }（:9-17）
     Starter 依赖：IExecutionWindowResolver / ITaskGoalDispatchTransactionStore /
                   IOptionsMonitor<TaskAutoDispatchOptions> / IOptions<TaskBoundGoalOptions> /
                   TimeProvider / ILogger（:45-54）
     围栏（Starter 内，勿绕过）：:76-100 必需字段齐全（否则 incomplete_candidate）
                               :103-121 二次 window fence（否则记 window.Code）
                               :123-145 transactionStore.StartAsync + 幂等键 task-goal:{ws}:{task}:{version}
6. 组装 TaskGoalLaunchResult 返回
```

---

## 7. 双写纪律（本轮特有风险）

同一时刻存在另一个在途工作流（**TD-1b**：TODO 按 Agent 隔离 + 约束分级），其文件域为
`TodoContracts.cs` / `TodoListEntity.cs` / `TodoStore.cs` / TODO 建表 / `PuddingRuntime/Services/TodoTools/*` / TODO 测试。

| 规则 | 说明 |
|---|---|
| **禁止触碰** | `Services/TodoTools/*`、`Todo*.cs`、TODO 建表 bootstrap、TODO 测试文件 |
| **组合根注册行由父级追加** | 实现子代理**不得**编辑 Host 组合根（TD-1b 正在该文件加 `ITodoStore` 注册）。实现子代理需在交付说明里给出**精确待追加行**，由父级在 TD-1b 落地后统一补 |
| **不修无关编译错误** | 若构建因 Todo 域在途改动失败 ⇒ **停下报告**，不要"顺手修" |
| **提交权归父级** | 子代理不 commit / 不 push |

---

## 8. 验收标准

| # | 标准 |
|---|---|
| 1 | 构建：`dotnet build` 相关工程 0 错误 |
| 2 | Platform 聚焦测试 exit 0，且**必须覆盖**：跨 Agent 持有 assignment ⇒ 拒绝；`PreferredAgentId` 指向他人 ⇒ 拒绝；调度器 shadow/paused ⇒ 拒绝；非可派发状态 ⇒ 拒绝；已有活跃 Goal ⇒ 幂等返回；成功路径 ⇒ 断言 `auto_dispatch_enabled` 被置 true 且 `DispatchDetailedAsync` 以 `maxStartsOverride: 1` 被调用一次 |
| 3 | Runtime 工具测试：参数校验、缺 `task_id`、错误码映射 |
| 4 | 重启后工具出现在运行时工具目录（`task_goal_start`） |
| 5 | **实测一条链路**：对 `f1d45a15` 调用该工具 ⇒ 产生任务绑定 Goal（`goal_payload.task` 非 null），并核对 `2b60040d`（ActiveTask 元数据注入）是否复现 |
| 6 | 文档同步：`Source/code_map.md` 增补三条新落点；本文档状态改为「已实现」 |

---

## 9. 明确**不做**的事

- ❌ 不给 `manage_tasks` 加裸开关 `auto_dispatch_enabled`（等于提供一个绕过 §5 全部校验的 enrollment 旋钮）；
- ❌ 不新增 HTTP 端点、不改 `[Authorize(Roles="admin")]`、不读 admin token；
- ❌ 不修改 `TaskAutoDispatchEvaluator` / `TaskAutoDispatchStarter` / `TaskGoalDispatchTransactionStore` 的围栏语义；
- ❌ 不做 promote（TODO → 看板）——见 TODO 设计的既有裁决。

---

## 10. 实测候选（供第 5 条验收使用）

`f1d45a1501f04b62bc25e6c2afedf8f0` —「[PuddingAgent 自进化] 终端 runner/cmd-pwsh 语义怪癖与 file_patch 反序列化 bug 修复」（p0 / Ready / v7，`preferred_agent_id` 已锁定本 Agent）。
