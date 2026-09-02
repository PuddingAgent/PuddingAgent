# 任务调度器与 Goal 用户控制面设计

> 状态：Implemented in source（外部部署与产品态 smoke 待完成）  
> 日期：2026-08-31  
> 上位裁决：ADR-072、ADR-074  
> 产品入口：Admin Chat Header → 任务看板 → 调度中心；Admin Chat Header → Goal

## 1. 现象与根因

2026-08-31 16:09–16:34 的产品日志证明 `TaskAutoDispatch` 正以 `authoritative` 模式每五分钟扫描：工作区 `default` 有 3 个 Agent，2 个 Idle、1 个 Busy，但每轮 `candidates=0 / eligible=0 / started=0`。同一时刻数据库中 34 个 Backlog 和唯一 Ready Task 的 `auto_dispatch_enabled` 都为 false。

因此问题不是“HostedService 没启动”，而是控制面断裂：

1. 后端 Task DTO 已具备 `autoDispatchEnabled/taskType/requiredCapabilityIds/provider/model/fallback`，Web 看板类型和编辑表单没有暴露，用户不能把任务纳入候选集；
2. 调度器只有后台日志和 evaluate API，没有用户可见的运行状态、暂停/恢复、立即扫描、立即修复和策略入口；
3. `GoalBanner` 只显示已有 Goal，未提供创建 Goal 的入口；终态 Goal 也无法直接新建；
4. “Delivery ACK”与“Execution claimed”容易被混淆。`assignment_execution_missing` 必须呈现为可恢复的执行所有权故障，而不是假装任务仍在运行。

## 2. 页面结构

任务看板 Header 保持主要动作只有三个：

1. **调度中心**：打开右侧 Drawer，管理工作区自动调度；
2. **刷新**：刷新五列 Task snapshot；
3. **新建任务**：进入 Task 编辑抽屉。

调度中心 Drawer 分为四段：

1. **运行状态**：Disabled / Paused / Shadow / Authoritative / Faulted，最近与下次扫描时间、错误；
2. **本轮事实**：Idle/Busy/Unknown Agent，候选/可派发/启动/修复数量；
3. **人工干预**：暂停/恢复、立即扫描、立即修复、刷新；
4. **策略**：启用开关、Shadow/Authoritative、扫描周期、候选上限、每轮启动上限；同时只读展示 idle grace、stall threshold、Goal/Task-bound 前置门禁。

Task 编辑抽屉增加“调度”字段组：是否纳入自动调度、任务类型、能力、Provider/Model、首选 Agent 与 fallback、执行窗口。Task 卡显示“自动”标记，详情抽屉展示完整路由事实。

Chat Header 的 Goal 入口采用紧凑状态按钮：无 Goal 时显示“Goal”并可开始；Active 显示暂停/停止；Paused/Blocked 显示恢复/停止；终态显示新建 Goal。停止映射为服务端 `cancel` 可审计终态，不物理删除历史。

## 3. 按钮和状态矩阵

### 3.1 调度器

| 动作 | Disabled | Paused | Shadow | Authoritative | 语义 |
|---|---:|---:|---:|---:|---|
| 刷新 | 是 | 是 | 是 | 是 | 只读刷新权威状态 |
| 启用/保存策略 | 是 | 是 | 是 | 是 | CAS 更新配置；非法组合 fail closed |
| 暂停自动调度 | 否 | 否 | 是 | 是 | 阻止新自动 Assignment/Goal；不强杀已运行 Goal |
| 恢复自动调度 | 否 | 是 | 否 | 否 | 恢复新的自动准入；不重放终态事实 |
| 立即扫描 | 否 | 是 | 是 | 是 | 用户显式触发一轮；仍经过依赖、Availability、窗口和事务 Fence |
| 立即修复 | 是 | 是 | 是 | 是 | Track → deterministic repair → rebuild availability；不绕过状态机 |

暂停是 admission containment，不等于暂停每个 Goal。要暂停或停止已运行 Goal，必须使用对应 Goal 控件。

### 3.2 Goal

| Goal 状态 | 开始/新建 | 暂停 | 恢复 | 停止 |
|---|---:|---:|---:|---:|
| 无 Goal | 是 | 否 | 否 | 否 |
| Active | 否 | 是 | 否 | 是 |
| Paused / Blocked | 否 | 否 | 是 | 是 |
| Completed / Failed / Cancelled / BudgetExhausted | 是 | 否 | 否 | 否 |

`resume` 不重置 Iteration；`stop` 使用服务端 `cancel`；终态 Goal 只能新建，不能恢复额度。

## 4. 服务端控制合同

调度控制必须继续使用服务端权威状态，不在浏览器维护第二套状态机：

```text
GET  /api/workspaces/{workspaceId}/task-scheduling/auto-dispatch/status
PUT  /api/workspaces/{workspaceId}/task-scheduling/auto-dispatch/policy
POST /api/workspaces/{workspaceId}/task-scheduling/auto-dispatch/actions/pause
POST /api/workspaces/{workspaceId}/task-scheduling/auto-dispatch/actions/resume
POST /api/workspaces/{workspaceId}/task-scheduling/auto-dispatch/actions/scan
POST /api/workspaces/{workspaceId}/task-scheduling/auto-dispatch/actions/repair
```

策略更新使用 `expectedRevision`。运行状态至少返回配置 revision、effective state、前置门禁、最近一次 scan/repair 摘要、最后错误和 next scan。策略仍属于现有 `TaskAutoDispatch` 配置，发布包 `appsettings.json` 仅提供安全默认，用户修改原子写入 `<DataRoot>/config/system.json` 的 `taskAutoDispatch` 并热加载；Task 的执行偏好继续只来自 `WorkspaceTask.executionWindow`，不得新增平行的工作区峰谷策略。

## 5. 安全与恢复不变量

1. UI 不直接写数据库、不推断 Agent Idle、不伪造 window override；
2. Pause/Disabled 阻止新的自动启动，但不让进行中的 Tool Call 在未知副作用点被强杀；
3. 手工 Scan 仍必须经过 route/plan/availability/window/version/fencing；
4. Repair 只处理 Tracker 给出的确定性 `CleanupRequired`；未知/不一致状态继续 fail closed；
5. `assignment_execution_missing` 的恢复顺序是释放旧所有权并标记 Blocked，再由用户 Resume/Requeue 创建新的 fenced 尝试；
6. Goal 和 Task 控件使用现有结构化命令 API，Web/Connector/Desktop 不各自实现状态机；
7. 源码/测试通过只表示 `ready-for-external-deploy`；新构建必须由进程外控制器部署，再用新产品会话验证功能。

## 6. 验收

- 创建或编辑 Task 时能显式开关自动调度，并保存/回读完整路由字段；
- 调度中心能解释为什么有 Backlog 却无候选，并显示候选决策码；
- Pause 后不再创建新的自动 Assignment/Goal，Resume 后新事件或恢复扫描可继续；
- Scan/Repair 返回结构化摘要，失败显示稳定错误而非仅提示“请求失败”；
- Goal 无状态、Active、Paused/Blocked、终态四组按钮矩阵均有组件测试；
- `assignment_execution_missing` Task 可从卡片/详情进入恢复或重新排队，不再只能看红字；
- 前端测试、Platform 聚焦测试、`dotnet build PuddingRuntime --no-restore`、`git diff --check` 通过；
- 新 Core 部署后的日志出现新版控制面与扫描摘要，浏览器完成真实点击 smoke。

## 7. 2026-08-31 源码交付证据

- Task 看板新增“调度中心”，并在创建/编辑、卡片和详情中暴露 `autoDispatchEnabled` 与结构化路由字段；
- 调度控制 API 已实现 status、CAS policy、pause/resume、scan、repair；策略原子写回 `<DataRoot>/config/system.json` 的 `taskAutoDispatch` 并由 Options hot reload 生效；
- 周期恢复扫描与用户立即扫描复用 `TaskAutoDispatchScanRunner`，统一执行 `track → repair → availability → backlog refinement → evaluate → fenced start`；
- Event bridge、Coordinator 和 Starter 改为读取动态配置；暂停工作区不会继续消费 intent 或创建新 Goal；
- Goal Header 已覆盖无 Goal、Active、Paused/Blocked、终态的开始/暂停/恢复/停止/新建矩阵；
- 前端 5 个聚焦套件 46/46、Platform 调度/TaskDispatcher/Goal 聚焦测试 90/90、Admin production build、PuddingHost build 通过；
- 当前浏览器仍由旧 Desktop/Core 提供资源，未看到“调度中心”属于未部署证据，不计为新代码失败。最终状态仍需进程外部署新 Core/前端后完成真实点击和新日志 smoke。
