# task_claim/task_update 反查失败归因丢失：四类根因被压成同一句 `task.active_context_missing`

- **类型**：平台缺陷报告（诊断可解释性）+ 最小修复规格
- **日期**：2026-09-13 04:5x BJT · **报告人**：默认助手（`default.global-general-assistant.6a8`）
- **复现次数**：3 次（跨 3 个不同 run 形态），并已造成**连续三轮错误归因**（已写入 goal.md 与 Memory，需按本报告校正）
- **关联**：缺陷族「重启恢复链断裂 / lease_lost / 父 Turn 已 completed 而子 Agent 仍 running」；前一环 **3f8df399**（已修：反查重建）、**2d5a2ebe**（已修：移除注入快照的第一重 CAS 互斥）

---

## 1. 现象（3 次实测，均为真实工具返回）

| # | run 形态 | 调用 | 返回 |
|---|---|---|---|
| R1 | subagent-result turn | `task_update(task_id/assignment_id 均完整, expected_version 已给)` | `{"code":"task.active_context_missing","message":"task_claim/task_update requires an Active Task Runtime Context; no task was dispatched to this run."}` |
| R2 | heartbeat run（同一 assignment 此前在**被派发 run 内 claim 成功**） | 同上 | 同上 |
| R3 | heartbeat run（本轮） | `task_get(06898d5dfe004c69ab6d5baf18b2674a)` | `{"code":"task.not_found"}`（该卡**确实存在**，见 `Docs/Reports/PuddingAgent-Autonomy-Audit-2026-09-12/02-任务看板登记与实施顺序.md:30`，且在 `manage_tasks` 全库视图中可见/Blocked） |

R3 的 `task.not_found` 是 `task_get` 的 **mine 信息隐藏**（`TaskGetTool.cs:56`），行为正确；
R1/R2 的 `active_context_missing` **文案是错的**——它断言了「没有任务被派发给本 run」，而本轮 run 确实带了 goal_payload（含 task/assignment）。

## 2. 代码定位（精确行）

反查重建入口与唯一实现：

| 位置 | 内容 |
|---|---|
| `Source/PuddingRuntime/Services/TaskTools/TaskClaimTool.cs:46` | `TaskToolGuard.ValidateActiveTaskOrRebuildAsync(..., requireInProgress: false)` |
| `Source/PuddingRuntime/Services/TaskTools/TaskUpdateTool.cs:46` | 同上，`requireInProgress: true` |
| `Source/PuddingRuntime/Services/TaskTools/TaskToolModels.cs:218` | `ValidateActiveTaskOrRebuildAsync` 定义（`:209-303`） |
| `TaskToolModels.cs:167-197` | `ValidateActiveTask`（第一重守卫）：`context.ActiveTask is null` → 报 `TaskActiveContextMissing` |

**问题点：反查失败时把具体原因吞掉，原样返回 `error`**（该 `error` 就是 `:173` 那句"no task was dispatched to this run"）。四条静默路径：

| 行 | 触发条件 | 真实原因 | 对外可见语义（错误） |
|---|---|---|---|
| `TaskToolModels.cs:236` | `taskId`/`assignmentId` 为空 | 入参不完整 | `active_context_missing` + "no task was dispatched" |
| `TaskToolModels.cs:241` | `service.GetAsync(...)` 返回 null | **mine 过滤：任务不存在，或存在但归属其他 Agent（信息隐藏，不区分）** | 同上 |
| `TaskToolModels.cs:263` | `assignment.AgentId != context.AgentInstanceId` | 归属校验双保险不通过 | 同上 |
| （对比）`:256` / `:274` / `:284` | assignment 过期 / 状态门槛 / 版本 CAS | — | ✅ 分别精确返回 `assignment.stale` / `task.state_conflict` / `task.version_conflict` |

即：**同一函数内，三类失败给了精确归因，另外三条（含最常见的"非 mine"）却被压成"平台没给我 context"**。

## 3. 为什么这是缺陷（不是"可接受的保守"）

1. **归因方向被反转**：真实原因多是我自己的卡不在 my-assignment 范围内（如 P2 卡处于 Blocked、assignment 不属于我），却被读成"平台注入链断裂"。我第一次写下的结论就是"心跳 run 无 Active Task Context ⇒ 平台缺陷"，并已固化进 goal.md 与 Memory，形成**决策链污染**（后续三轮都在围绕错误前提做规避设计）。
2. **直接违反 P1 Goodput SLO 卡的验收**：`0b16740022f84b58a9532a87f1bc5509` 明文要求「**全部心跳可解释**」；当前这 4 类心跳失败无法区分，此项不可验收。
3. **可辨识性缺口**：`:241` 的"任务不存在"与"任务归属他人"合并是**有意的信息隐藏**（正确，必须保留）；但"**反查根本未被尝试**"（`:236` 入参不完整）与"**反查被尝试并失败**"（`:241`/`:263`）**不是**安全边界，只是丢字段，可以且应当区分。
4. 前一环 `TaskActiveTaskFourChainE2ETests.cs:207` 的注记已承认同类问题（"原为笼统 active_context_missing"）并修了状态门槛一条——本报告是同一问题的**剩余面收口**。

## 4. 最小修复规格（建议，未实施）

**不改变**：错误 `code`（`task.active_context_missing` 保持不变）、信息隐藏策略、CAS 语义、反查触发条件。**只增加诊断字段**。

在 `TaskToolErrors.BuildErrorJson`（`Source/PuddingCore/Tasks/TaskAgentCommandContracts.cs:216`，映射表 `:219-240`）已有的可选 `version`/`status` 参数旁，新增可选诊断对象：

```json
{
  "code": "task.active_context_missing",
  "message": "task_claim/task_update requires an Active Task Runtime Context; no task was dispatched to this run.",
  "task_id": "...",
  "context_rebuild": { "attempted": true, "stage": "lookup", "outcome": "not_visible" }
}
```

取值（**保守、不泄露归属**）：

| stage | outcome | 含义 |
|---|---|---|
| `inputs` | `incomplete` | 入参缺失，反查未开始 |
| `injected` | `mismatch` | 注入上下文存在但入参不匹配（现有 `state_conflict` 分支，可统一带上） |
| `lookup` | `not_visible` | 反查命中 mine 过滤（**不区分不存在/非 mine**——维持信息隐藏） |
| `ownership` | `agent_mismatch` | assignment.AgentId ≠ 当前实例 |
| `—` | `ok` | 反查成功（此时不会有 error，仅用于日志） |

实施点：`TaskToolModels.cs:236 / :241 / :263` 三处 `return (error, null)` 改为带诊断的 `BuildErrorJson`；`:221`（`context.ActiveTask is not null` 的真实调用错误）保持原样不带 rebuild 字段（语义上未尝试反查）。

## 5. 测试计划（4 正向 + 1 负向）

1. 入参不完整 → `code=task.active_context_missing` 且 `context_rebuild={inputs,incomplete}`。
2. `GetAsync` 返回 null（非 mine） → `{lookup,not_visible}`。
3. `assignment.AgentId` 不匹配 → `{ownership,agent_mismatch}`。
4. 正常重建路径回归：`code` 与成功行为不变（既有 `TaskToolsTests.cs:570/830/993/1011`、`TaskActiveTaskFourChainE2ETests` 全绿）。
5. **负向安全断言**：三类失败响应体中**不得**出现其他 Agent 实例 id、任务标题、assignment 所有者等归属信息（防止"可解释性"顺手破坏信息隐藏）。

## 6. 需要我（Agent 侧）同步校正的记录

- goal.md / Memory 中"**心跳 run 无 Active Task Context**"的结论**过强**，应改为：
  - 正确表述：**task 工具在非派发 run（或派发 run 中该卡不属于本 Agent）时会拒绝，且拒绝原因目前不可区分**；心跳轮只做取证/落盘/提交仍是**工程上正确的规避策略**，但依据从"平台缺陷"改为"归因不可区分 + Blocked 卡无 mine assignment"。
- R3 的 `task_get` → `task.not_found` 是**设计内的信息隐藏**，不应计入缺陷。

## 7. 非目标

- 不改 mine 信息隐藏策略（`TaskGetTool`/`Platform` 裁决）。
- 不恢复 `expected_version` 与注入快照的比对（缺陷 2d5a2ebe 已裁决移除）。
- 不扩大 `:241` 的可见信息（不得区分"不存在"与"非 mine"）。
