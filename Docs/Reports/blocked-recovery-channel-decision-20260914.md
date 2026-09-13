# Blocked + active assignment 的 canonical 恢复通道：设计裁定

- 日期：2026-09-14
- 范围：`Source/PuddingRuntime/Services/TaskTools/`（重建守卫）；`Source/PuddingCore/Tasks/TaskStateMachine.cs` 为权威状态机，本次**未改**
- 关联卡：`813ad427c0d54fd6a67e9bd39b03d4c4`（本裁定）、`bf7ef5f5e2d04d23ac3aed224ee539c3`（16 张僵死卡回填）、`3bd2a4b0ef5f4bff8f175fb7655927ad`（原卡，前提已证伪）
- 取证报告：`Docs/Reports/scheduler-kernel-current-state-20260913.md`

## 1. 问题（可复现）

任务卡处于 `Blocked` 且**仍持有 active assignment**（归属该卡所属 Task-bound Goal 的 Agent）时，该 goal run 调用 `task_update` 被拒：

```json
{"error":{"code":"task.active_context_missing","message":"task_claim/task_update requires an Active Task Runtime Context; no task was dispatched to this run."}}
```

原因是服务端上下文重建兜底（缺陷 3f8df399 引入）要求「任务状态须 Assigned/InProgress + 版本 CAS 匹配」，`Blocked` 不在白名单。后果：任何被 tracker / 启动链复阻塞的卡，其 goal run 都进入「活着但无法上报」的僵死态，唯一逃逸是管理者视图 `manage_tasks action=resume` 手工改状态 —— 与 P0 Scheduler 内核「唯一启动路径 + 禁止回退手工/Heartbeat」的目标直接冲突。

## 2. 代码级事实

| 事实 | 位置 | 结论 |
|---|---|---|
| `Todo` 允许 `InProgress｜Blocked｜NeedsReview → Ready` | `TaskStateMachine.TryInterpretDisposition` | **Blocked→Ready 本就是合法转移** |
| `Progress` 仅 `InProgress → InProgress`；`Blocked`/`NeedsApproval` 仅 `Assigned｜InProgress`；`Completed` 仅 `InProgress` | 同上 | Blocked 下只有 `todo` 合法，其余非法 |
| `Resume` 允许 `Blocked → Ready` | `TaskStateMachine.TryApplyCommand` | 管理面已具备逃逸语义 |
| mine 过滤只要求 `ActiveAssignmentId != null && attempt.AgentId == caller`，**不按状态过滤** | `TaskAgentCommandService.GetAsync` | 反查可见性不受 Blocked 影响 |
| 服务端只做 active assignment / 版本 CAS / `TryInterpretDisposition` 三重校验 | `TaskAgentCommandService.ApplyDispositionAsync` | 服务端已是唯一权威 |
| 重建守卫 ④ 状态门槛：claim 要求 `Assigned｜InProgress`；update 要求 `InProgress` | `TaskToolGuard.ValidateActiveTaskOrRebuildAsync` | **唯一缺口在此** |

## 3. 裁定

**允许（有界）**：Blocked 且持有 active assignment 的卡，其所属 goal run 应当被允许发起 canonical 恢复上报，但**只放宽「能否重建上下文」，不放宽「什么 disposition 合法」**。

- 重建门槛（update 路径）＝ `InProgress` **或** `Blocked`，且必须同时满足：active assignment 存在、`assignment.AssignmentId` 与入参一致、`assignment.AgentId == 当前 Agent`、`task.Version == expected_version`。
- 合法 disposition 仍由服务端状态机裁决（fail closed）：Blocked 下仅 `todo`（→ `Ready` 并释放 active assignment，即 canonical resume 语义）合法；`progress` / `completed` / `blocked` / `needs_approval` / `rejected` 仍返回 `task.state_conflict`。
- **不放宽 claim**：`task_claim` 仍是 `Assigned｜InProgress`，Blocked 卡不可被认领。

依据：
1. 状态机已把 `Blocked → Ready` 定义为合法转换，缺的只是「谁有权发起」这一客户端门槛；
2. 服务端已是唯一权威，放宽客户端门槛**不会**引入新的非法状态（非法 disposition 依旧被服务端拒绝，且拒绝前不落库）；
3. 原设计使 Blocked 成为单向死胡同，与 P0 目标（禁止回退手工/Heartbeat 的自动恢复）冲突。

## 4. 实施与验证

- 代码：`TaskToolModels.cs`（④ 门槛 + 裁定注释；参数 `requireInProgress` → `allowBlockedRecovery`）、`TaskUpdateTool.cs`（调用点 + 工具描述口径）、`TaskClaimTool.cs`（调用点）。
- 测试：`Source/PuddingRuntimeTests/Services/TaskE2E/TaskActiveTaskFourChainE2ETests.cs` 新增 T12–T15：
  - T12 Blocked + active assignment + CAS 匹配 → 重建成功，`todo` 使卡回到 `Ready`（v3）并释放 active assignment；
  - T13 Blocked 下 `progress` → `task.state_conflict`，DB 零变化；
  - T14 Blocked 下陈旧 `expected_version` → `task.version_conflict`，DB 零变化；
  - T15 Blocked 卡 `task_claim` → `task.state_conflict`，DB 零变化。
- 验证：`dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter "FullyQualifiedName~Task"` → **123/123 通过，0 失败**；`requireInProgress` 全仓零引用；`git grep` 旧符号空输出。
- 代码映射与索引：`Source/PuddingRuntime/code_map.md`（任务工具三条目）。

## 5. 遗留（不在本裁定内）

1. Blocked 下是否需要 `progress` 的合法语义（「续跑语义」）——当前 fail closed：goal run 若要续跑必须先 `todo` 回 `Ready` 重新排队。
2. 16 张「`Blocked` + `BoardColumn=InProgress` 且无恢复路径」僵死卡的逐张分类回填 → 卡 `bf7ef5f5`。
3. 方案文档 §8 Blocked UI 与 `task_scheduler_scan_runs` Goodput API 的落地口径 → 卡 `bf7ef5f5`。
4. 本改动需在新制品上做产品验收（本轮未重启进程，避免静默中断运行中的会话与子代理）。
