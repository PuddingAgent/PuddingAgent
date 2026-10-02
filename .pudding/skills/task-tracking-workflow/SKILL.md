# 任务跟踪工作流 (task-tracking-workflow)

## 来源与背景

- **问题**：Agent 执行任务时仅依赖 `goal.md` 记录进度，缺乏结构化、可查询、可在看板展示的任务状态跟踪。P0 任务看板系统（`workspace_tasks` 三表 + 前端五列 Board + `task_*`/`manage_tasks` 工具）已上线，Agent 应主动接入任务管理工具跟踪任务生命周期。
- **方案**：定义一套「任务生命周期 → 工具调用」的标准流程，让 Agent 在每个阶段（盘点/认领/推进/阻塞/完成）用对应工具上报状态，使任务进度对用户和看板实时可见。
- **原则**：自然语言说"完成"不改变 Task，只有任务管理工具生效；每个原子任务开工前认领、收尾时闭环。
- **工具**：执行者视角 `task_list` / `task_get` / `task_claim` / `task_update`；管理者视角 `manage_tasks`。

## 工具全景

| 工具 | 视角 | 作用 | 关键参数 |
|------|------|------|----------|
| `task_list` | 执行者 | 盘点名下任务（mine 范围）| `status` / `board_column` / `priority` / `limit` / `cursor` |
| `task_get` | 执行者 | 读取任务详情 + 近期事件 | `task_id` / `assignment_id` / `events_limit` |
| `task_claim` | 执行者 | 认领任务（Assigned→InProgress）| `task_id` / `assignment_id` / `expected_version` |
| `task_update` | 执行者 | 提交 disposition 推进状态 | `disposition` + 各 disposition 必填字段 |
| `manage_tasks` | 管理者 | 跨 Agent 完整 CRUD + 命令 | `action`（list/create/get/update/delete/assign/run_now/cancel/reopen/archive/mark_failed/resume/requeue）|

**视角区分（关键）**：
- `task_list`/`task_get`/`task_claim`/`task_update` = **执行者视角**，只处理「派发给自己的任务」，受 Active Task Context 守卫。
- `manage_tasks` = **管理者/协调者视角**，无 mine 限制，可创建任务、跨 Agent 查看整板、分配任务、执行状态命令。

## 任务生命周期 → 工具映射

| 阶段 | 工具调用 | 动作/参数 | 触发时机 |
|------|----------|-----------|----------|
| ① 盘点 | `task_list` | 无参或按 `status`/`board_column` 过滤 | 心跳唤醒 / 会话开始时 |
| ② 认领 | `task_claim` | `task_id`/`assignment_id`/`expected_version` = Active Task Context 注入值 | 开始执行已 Assigned 的任务时 |
| ③ 推进 | `task_update` | `disposition=progress` + `progress_summary` 或 `next_action`（+`progress_percent`）| 关键节点 / 每个推进步骤后 |
| ④ 阻塞 | `task_update` | `disposition=blocked` + `reason`（必填）| 遇到无法自主推进的阻塞时 |
| ⑤ 完成 | `task_update` | `disposition=completed` + `result_summary`（+`artifacts`）| 任务交付并验收通过后 |
| ⑥ 创建/派发 | `manage_tasks` | `action=create` / `action=assign` | 需要拆解新任务或分配给他方时 |

### disposition 语义速查（`task_update`）

| disposition | 必填字段 | 含义 |
|-------------|----------|------|
| `accept` | — | 接受任务（确认承接）|
| `progress` | `progress_summary` 或 `next_action`（至少其一）| 汇报进展 |
| `todo` | — | 记录待办 |
| `blocked` | `reason` | 标记阻塞 |
| `needs_approval` | `reason` | 请求审批 |
| `rejected` | `reason` | 拒绝任务 |
| `completed` | `result_summary` | 标记完成 |

## 与 goal.md 的分工

| 维度 | goal.md | 任务管理工具 |
|------|---------|--------------|
| 定位 | 长期目标、上下文、决策记录 | 结构化任务状态跟踪 |
| 结构 | 自由文本 + 时间戳 | 数据库字段（12 态 / 五列 / 优先级 / 版本）|
| 可见性 | 仅 Agent 私有 | 前端五列 Board 实时可见 |
| 查询 | 读文件 | `task_list` 过滤 / 看板浏览 |
| 用途 | 恢复上下文、记录决策 | 跟踪任务进度、跨 Agent 协调 |

**分工原则**：goal.md 记"为什么/背景/决策"，任务工具记"做什么/进度/状态"。两者互补，不重复。

## 触发时机（何时用）

1. **心跳唤醒**：先 `task_list` 盘点名下任务（`board_column=InProgress` 或 `status=Assigned`），有任务则推进，无任务则待命。
2. **收到新派发**：检测到 Active Task Context 注入后，先 `task_claim` 认领，再开始执行。
3. **每个推进步骤后**：完成一个原子步骤 → `task_update progress`（记录进度摘要 + 下一步）。
4. **遇到阻塞**：立即 `task_update blocked`（写清 reason），不要静默等待。
5. **任务完成**：验收通过后 → `task_update completed`（写 result_summary + 产物标识）。
6. **需要拆解/派发/跨 Agent 协调**：`manage_tasks`（create 新任务 / assign 分配 / 查看整板）。

## 关键纪律与坑

1. **自然语言 ≠ 工具**：在对话里说"我完成了"不会改变 Task，必须调用 `task_update` 才生效。
2. **Active Task Context 守卫**：`task_claim`/`task_update` 的 `task_id`/`assignment_id`/`expected_version` 必须等于运行时注入的 Active Task Context 值，否则返回 `task.active_context_missing` / `task.state_conflict`。
3. **版本冲突**：迟到调用（任务已被重派/闭合）返回 `assignment.stale` / `state_conflict` / `version_conflict`——此时重新 `task_list` 同步最新状态，不要盲目重试。
4. **状态机约束**：显式状态迁移走 `CanTransition` 校验；「待办→进行中」须经 `Reserved→Assigned` 中间态，不能直接跳（`task_update` 的硬推 `status` 会绕过 assignment 记录，真实"进行中"应由 `task_claim`/`accept` 触发）。
5. **管理者视角的硬删限制**：`manage_tasks` 的 `delete` 仅能删无历史 Backlog 任务，否则返回 `task.cannot_hard_delete`。
6. **feature flag**：任务工具受 `WorkspaceTasks.Enabled` 控制，关闭时返回 `task.capability_missing`。
7. **workspace_id**：由运行时注入，不接受 Agent 指定。

## 质量门禁

- [ ] 心跳唤醒先 `task_list` 盘点名下任务
- [ ] 开始执行已 Assigned 任务前先 `task_claim`
- [ ] 每个原子步骤后 `task_update progress`（含摘要或下一步）
- [ ] 阻塞时 `task_update blocked`（含 reason）
- [ ] 完成后 `task_update completed`（含 result_summary）
- [ ] 跨 Agent 协调用 `manage_tasks`，不越权用执行者视角工具
- [ ] 与 goal.md 不重复记录（goal 记决策，任务工具记状态）

## 历史参考

- v1.0.0（2026-08-17 首次创建，基于 P0 任务看板系统闭环 + TB-06/TB-09 工具，e3a328 会话）
