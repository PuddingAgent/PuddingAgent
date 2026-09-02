# ADR-074 Goal 持久目标、自主续行与自动压缩

> 状态：Proposed；目标架构决策，不表示已经实现
> 日期：2026-08-18；2026-08-21 增补 Task-bound Goal 与低峰自动派发裁决
> 决策范围：Goal 命令、多入口控制、持久目标状态、自主续行、证据验证、256 轮上限、上下文自动压缩、WorkspaceTask 绑定、Agent 可用性感知、低峰自动派发、恢复、安全和可观测性
> 完整设计：[Goal 持久目标、自主续行与自动压缩完整设计方案](../Features/Goal持久目标自主续行与自动压缩完整设计方案.md)
> 施工计划：[Task-bound Goal 与 Agent 状态感知自动派发代码级施工计划](../Features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md)
> 压缩边界：[ADR-042 上下文自动压缩与主动 Compact 命令](43ADR-042上下文自动压缩与主动Compact命令ADR.md)
> 执行边界：[ADR-059 Conversation 执行内核与可靠命令链路](60ADR-059Conversation执行内核与可靠命令链路ADR.md)
> 任务边界：[ADR-072 工作区 TODO、峰谷 Auto 派发与定时任务第一阶段](86ADR-072工作区TODO峰谷Auto派发与定时任务第一阶段ADR.md)
> 2026-08-28 实施注记：G2 durable continuation、G3 保守终态协调、Task-bound Goal 原子启动与 authoritative 扫描链已落源码；五分钟 Shadow、显式 opt-in、Backlog refinement、结构化路由、Execution Tracker、确定性 repair、版本化 WorkUnit 顺序推进和 round/tool/time/input/output/cost 硬预算已实现，authoritative 仍关闭。checkpoint/AwaitHandle 消费、blocked 重预约、provider/model 低峰价格档案、动态模型评分、完整 Verifier、Admin、新构建部署与七夜生产验收尚未通过，故本 ADR 仍为 Proposed。

## 1. 决策

Pudding 将 `/goal` 实现为 Conversation 之上的持久 `GoalRun` 控制面。它以已提交的 Turn 终态事件驱动下一次 Goal Iteration，复用现有 Conversation 命令链、Agent 执行内核和上下文压缩能力，不依赖 Heartbeat。

本 ADR 冻结以下决策：

1. Goal 是外层有限目标循环；现有 Agent Loop 是单个 Goal Iteration 内的执行循环。两者分别计数、分别限额，不得把 `AgentExecutionGuardrails.MaxRounds` 直接解释为 Goal 的 256 轮。
2. 单个 Goal 默认和硬上限均为 256 个 `accepted Goal Iteration`。用户可以设置更小值，不能设置更大值；resume 不重置已消费轮数。
3. `/goal`、`/goal status`、`/goal pause`、`/goal resume`、`/goal cancel` 等命令由 Core 的统一系统命令处理器解释。Admin Web、PuddingDesktop 内嵌网页和认证 Connector 只负责发送与展示，不各自运行 Goal 循环。
4. 每次续行都由持久 `goal_outbox` 意图、CAS、activation epoch、确定性幂等键和 session gate 保护。进程内 signal 只降低延迟，恢复扫描只修复遗漏，二者都不是 Heartbeat。
5. Goal Iteration 通过现有 `ConversationAcceptanceStore -> ChatExecutionCommand -> ChatExecutionWorker -> AgentExecutionService` 进入正常执行链，不建设第二套模型/工具执行器。
6. Agent 的 `DONE` 只是完成提议。完成必须经过确定性证据门禁与只读 Goal Verifier；只有 Coordinator 能以 CAS 提交 Goal 终态。
7. Goal 的 objective、预算、phase、当前进度和证据索引独立持久化，不从聊天文本或 compaction summary 反推。上下文压力继续由 `ContextWindowManager` 和 `CompactionService` 处理。
8. 用户直接消息、pause/cancel/replace、审批与安全策略优先于自动 continuation。Goal 不自动切换 Yolo，不提升工具权限，不绕过高风险操作审批。
9. Core 重启后保留 Goal 与证据，但默认把原 `active` Goal 投影为 `paused`/disarmed；只有显式 `/goal resume` 才生成新的 activation epoch 并恢复自主续行。
10. 任意入口看到的 Goal 状态都来自同一服务端投影和 canonical event stream；synthetic continuation 不伪装成真实用户消息。
11. 由 Auto Dispatcher 选中的 `WorkspaceTask` 不得只发送一条“请开始任务”消息。它必须在同一个短事务中建立 Assignment/Reservation、`task_goal_bindings`、Task-bound `GoalRun` 和首个 `goal_outbox`；Goal 持久续行是开放自动任务派发的前置门禁。
12. 一个自动任务在任一时刻最多绑定一个非终态 GoalRun，一个 Task-bound GoalRun 也只绑定一个 Task/Assignment。任务指令不得进入普通 Message Fabric 的多任务批量合并，不得丢失 `taskId/assignmentId/expectedTaskVersion/reservationFencingToken`。
13. `Task.executionWindow` 是任务自身的执行偏好权威；不新增工作区 `work-policy.json`。`IExecutionWindowResolver` 从 Agent 实际模型路由和现有 LLM 提供商/模型配置的价格时段解析 `currentWindow/nextEligibleAt/profileVersion`；没有可验证低价时段时，`off_peak_only` 必须 fail closed 并显示原因。
14. Agent Availability Sensor 必须由已提交的 Session/Turn/Tool/Approval/Message/Reservation 事实构建可持久投影。重启后从 `unknown` 重建；进程内字典、在线标识或最近一次心跳不得单独证明 `idle`。
15. Task Dispatch Coordinator 由 `task.ready`、`agent.availability.changed`、`execution_window.opened` 和事务提交后 signal 驱动；低频恢复扫描只修复已存在的 Ready/Deferred/Outbox/Expired Lease 事实。Heartbeat 只能作为原有周期检查的降级能力，不创建派发意图。
16. Task 完成必须由 Task-bound Goal 的终态 CAS 与 Task 结果/证据门禁一致提交。Agent 的自然语言“已完成”、Delivery ACK 或 Goal `DONE` 都不能单独使 Task 进入 Completed。
17. 自动派发不得从 Task 标题或正文猜测执行类型。Task 必须以结构化 `taskType/requiredCapabilityIds/requiredProviderId/requiredModelId` 声明约束；类型默认规则由版本化 `TaskTypeRoutes` 配置拥有，任务显式约束只能收紧、不能放宽类型规则。
18. `preferredAgentId` 只在 `allowAgentFallback=false` 时是独占约束。显式允许 fallback 后，Scheduler 可依稳定顺序尝试其它兼容 Agent，但每个候选仍必须通过模板能力、provider/model、Availability、idle grace 和 execution-window 全部门禁。
19. 自动启动命令必须携带 Agent 路由 SHA-256。唯一 Task→Goal 事务写入者在提交前重新读取当前 Agent 模板和 TaskTypeRoute 并重算；指纹不一致或候选不再兼容时返回 `agent_changed`，不得建立任何 Assignment、Reservation、Binding、Goal 或 Outbox。
20. Ready/Deferred 状态本身不授权后台执行。每个自动任务还必须持久化 `autoDispatchEnabled=true`；缺省值固定为 false，Evaluator 与最终事务都重复检查。Backlog refinement 只检查显式 opt-in 的任务；Shadow 仅输出决策，authoritative 必须由唯一 Store 重验 Task/Agent/Route 指纹并以 CAS + canonical `TaskReady` 提交。
21. 自动派发后的五分钟恢复扫描必须关联 Task、Assignment、Reservation、Binding、Goal、Iteration、ExecutionCommand/Run 与 outbox 的 canonical facts，输出健康、等待、卡住、不一致和待清理五类 verdict。该 Tracker 只读；任何 renew/retry/requeue/release/block 修复必须由独立唯一写入者重新校验版本、lease 和 fencing token 后提交，不能把 Watchdog 观察结果直接当命令。
22. `WorkspaceTask` 继续是任务台账权威，`task_plan_runs/task_nodes` 只保存该 Task 版本编译出的不可变执行快照。评估器与最终事务必须分别编译同一 `Explore/Plan/Change/Test/Review` WorkUnit DAG，并校验包含依赖、能力、冲突范围和预算的 SHA-256；漂移返回 `execution_plan_changed`。计划、WorkUnit、Assignment、Reservation、Binding、Goal 和首个 Outbox 必须同事务全成或全不成。AwaitHandle 只表达持久等待，不以轮询占用 Agent Loop。

## 2. 背景与问题

Pudding 已具备系统命令、Conversation 可靠受理、执行 Worker、Agent 内层循环、事件投影和自动压缩等基础能力，但尚无一个把这些能力组合成“持续推进直到完成或确实不能继续”的持久目标控制面。

如果仅让 Agent 等待 Heartbeat，会出现以下问题：

- Heartbeat 是周期唤醒，不是 Goal 状态机；延迟、峰谷禁用、重启和投递重复都会改变语义；
- “是否继续”依赖下一次定时消息，而不是刚刚提交的执行事实；
- 目标、预算、证据和阻塞原因没有独立权威状态；
- 网页、Desktop 与 Connector 很容易各自发明本地自动发送逻辑；
- 长目标可能在上下文溢出、重启或浏览器关闭后失去控制；
- Agent 自报完成容易提前停止，盲目续行又会形成无进展自激。

Goal 需要的是事件驱动、可恢复、有限预算、证据闭环，而不是更频繁的心跳。

工作区任务看板已经能表达 `anytime/off_peak_only` 偏好，但当前任务派发链仍以单次 Message Delivery 为主，Agent 可用性也主要依赖进程内状态。因此会出现“夜间 Agent 空闲，Backlog/Ready 任务却没有进入执行”，并错过 token 低价窗口。这不是增加心跳频率可以解决的问题：系统需要可持久的可用性感知、事件驱动的协调派发和能把任务继续做完的 GoalRun。

## 3. 术语与双层预算

| 术语 | 定义 | 是否计入 256 |
|------|------|--------------|
| GoalRun | 一次持久目标的完整生命周期 | 否 |
| Goal Iteration | Goal Driver 接受并投递的一次 synthetic continuation Turn | 是 |
| Agent Loop Round | 单个 Turn 内一次 LLM/Tool 循环 | 否，受独立内层预算限制 |
| Verifier Attempt | 对已提交证据的一次只读验证 | 否 |
| Context Compaction | 上下文 checkpoint/summary 的生成与提交 | 否 |
| Human Turn | 用户主动发出的普通消息 | 否 |
| Transparent Retry | LLM/网络底层不产生新受理 Turn 的重试 | 否 |

一个 Goal Iteration 在 synthetic Turn 被可靠受理并产生确定性的 `goal.iteration.accepted` 后消费预算。过期 outbox、CAS 失败、被用户抢占或尚未受理的投递不消费预算；受理后即使取消、执行失败或 Core 崩溃，也不回退计数。

建议默认值：

| 配置 | 默认值 | 硬边界/说明 |
|------|--------|---------------|
| `goal.max_iterations` | 256 | `1..256`，resume 不补充 |
| `goal.agent_loop_rounds_per_iteration` | 32 | 与通用 Agent 上限取更小值 |
| `goal.max_tool_calls_total` | 4096 | Goal 生命周期累计 |
| `goal.max_active_elapsed` | 24h | 只计 active 执行时间 |
| `goal.max_wall_clock_age` | 7d | 防止永久遗留 |
| `goal.same_blocker_threshold` | 3 | 相同软阻塞连续阈值 |
| `goal.no_progress_threshold` | 3 | 相同进度指纹连续阈值 |
| `goal.verifier_max_attempts` | 3 | 失败后 fail-closed pause |
| `goal.resume_after_restart` | false | V1 固定为 false |

Workspace 的 token、费用、工具和执行窗口策略仍可以给出更小限额；任何局部设置都不能扩大系统硬边界。

## 4. 命令合同

统一命令语法：

```text
/goal
/goal status
/goal <objective> [--rounds N]
/goal set <objective> [--rounds N]
/goal edit <objective>
/goal replace <objective> [--rounds N]
/goal pause [reason]
/goal resume
/goal cancel [reason]
/goal clear
```

合同如下：

- `objective` 是去除首尾空白后的 1–4000 个 Unicode 字符；服务端保存原文并在构造提示时以结构化字段/JSON 字符串注入，禁止拼接成新的系统指令段。
- `--rounds` 必须是 `1..256` 的整数。省略时为 256；越界返回明确错误，不静默截断。
- `/goal` 等价于 `/goal status`，不得在空参数时隐式创建目标。
- 同一 Conversation 同时最多一个非终态 Goal。`set` 遇到非终态 Goal 返回 conflict；改变目标必须显式 `edit` 或 `replace`。
- `edit` 保留 Goal identity、已消费预算和证据链，递增 revision/activation epoch；`replace` 结束旧 Goal 并创建新 Goal。
- `pause` 阻止新的 continuation；当前已进入不可安全中断工具调用的 Turn 允许 settle，迟到结果不能越过 epoch fence 再续行。
- `cancel` 是可审计终态；`clear` 只清除客户端“当前 Goal”指针/展示，不删除事件、Iteration、Verification 或 Artifact。
- `resume` 只能恢复允许恢复的 paused/blocked/budget 状态，并且不会把剩余预算重置为 256。

网页和客户端既可以把 slash 文本提交给现有系统命令 API，也可以使用结构化 Goal command API；两者必须调用同一应用服务、共享 `clientRequestId/externalMessageId` 幂等约束并产生相同事件。

## 5. 状态机

### 5.1 GoalRun phase

```text
                 +--------- pause/restart --------+
                 |                                v
created -> active <---------------------------- paused
             |  ^                                 |
             |  +----------- explicit resume -----+
             |
             +-> blocked -------- resume/edit ----+
             +-> budget_exhausted -- extend policy is forbidden in V1
             +-> completed
             +-> cancelled
             +-> failed
```

`completed`、`cancelled`、`failed` 是终态。`budget_exhausted` 在 V1 不允许通过 resume 重置轮数；只有尚有其他预算且阻塞条件已解除的 paused/blocked 才能恢复。Goal 因达到 256 个 accepted Iteration 进入 `budget_exhausted`。

### 5.2 activation 与执行状态分离

Goal phase 不承载正在排队/执行/验证的瞬时细节。`goal_iterations` 独立记录：

```text
reserved -> accepted -> executing -> settled -> verifying -> verified
   |            |           |           |            |
suppressed    cancelled    failed      failed       failed
```

`activationEpoch` 每次 create/edit/replace/resume/pause/cancel 和 session rebind 递增。Continuation、Iteration、Verification 写入都携带 epoch；旧 epoch 的迟到提交只能记审计事实，不能改变当前 Goal。

`aggregateVersion` 用于乐观并发，`bootId` 用于重启 disarm。版本、epoch 和 boot fence 不可互相替代。

## 6. 自主续行，而非 Heartbeat

### 6.1 正常链路

```text
Goal command commit
  -> goal_run + goal.created/activated + goal_outbox
  -> GoalContinuationWorker claim
  -> user/session/admission/CAS/epoch checks
  -> atomically accept synthetic Conversation Turn
  -> existing ChatExecutionWorker / AgentExecutionService
  -> canonical Turn settled event
  -> GoalSettlementWorker builds evidence capsule
  -> deterministic gates + read-only Goal Verifier
  -> GoalCoordinator CAS applies verdict
       continue -> new goal_outbox
       terminal -> goal terminal event
       pause/block -> wait for explicit authority/fact change
```

关键点：

- `goal_outbox` 是“应当续行”的持久意图；数据库 signal/channel 只负责立即唤醒 Worker；低频恢复扫描只认领已有意图，不能凭时间创建新意图。
- `GoalContinuationWorker` 不直接调用 LLM。它把 synthetic Turn 可靠受理到现有命令链，保持 session 单写者、事件、usage、tool 和错误语义一致。
- 下一次 continuation 只能在前一 Iteration 的 Turn 已 settle、Verifier 已提交 verdict、Goal 仍 active 且当前 Conversation idle 后创建。
- 每个 outbox 使用 `goalId + activationEpoch + iterationNumber` 作为确定性幂等键。

### 6.2 用户优先与竞态

用户普通消息优先于 Goal continuation：

1. 用户消息和 Goal continuation 竞争同一 Conversation session gate；
2. Worker claim 后、受理前再次检查是否有更早的用户 Acceptance；
3. 若用户消息已进入队列，continuation 退回 pending/deferred，不消费 Iteration；
4. Human Turn settle 后，其新事实进入 Goal evidence；若 Goal 仍 active，再重新验证是否继续；
5. 用户 pause/cancel/replace 通过 aggregate CAS 和 epoch fence 使旧续行失效。

现有“停止当前生成”动作对 Goal 的产品语义是：停止/取消当前可中断 Turn，并把 Goal 置为 paused，不能在停止后自动投递下一轮。

### 6.3 WorkAdmissionFence

Goal continuation 属于自动工作，必须在 reserve 和 accept 前两次经过 `WorkAdmissionFence`。用户直接消息不受 Goal 的峰谷调度限制。Fence 决定 deferred 时只延迟已有 outbox，并保留原因与 next eligible time；不得由 Heartbeat 重新发明 continuation。

### 6.4 Task-bound Goal 启动链

Auto Dispatcher 不向 Agent 发送普通自然语言提醒，而是向同一 Goal 应用服务提交内部结构化命令 `StartGoalFromTask`。该命令只能由受信 Task Dispatch Coordinator 创建，至少包含：

```text
workspaceId, taskId, assignmentId, expectedTaskVersion,
agentId, conversationId, reservationId, reservationFencingToken,
executionWindow, providerId, modelId, windowProfileVersion,
goalIterationBudget, causationId, correlationId, idempotencyKey
```

原子启动事务必须同时完成：

1. CAS 校验 Task 仍可派发、Assignment 仍当前、Agent Reservation 仍有效；
2. 校验 Availability 和 Execution Window 快照未过期；
3. 提交 `task_goal_bindings` 与 Task-bound `goal_runs`；
4. 写入 `task.goal.bound`、`goal.created`、`goal.activated` 与首个 `goal_outbox`；
5. 将 Task 置为 Assigned/Reserved 后的唯一合法中间态，不先向 Message Fabric 发送一条无 Goal 绑定的消息。

事务提交后，`GoalContinuationWorker` 按 6.1 进入现有 Conversation 执行链。Runtime 通过受信 `TaskGoalRuntimeContext` 同时看到 Task 验收条件和 Goal 轮次，不从消息文本反推绑定。

### 6.5 Availability Sensor 与 Dispatch Coordinator

| 组件 | 输入 | 输出 | 不负责 |
|------|------|------|--------|
| `AgentAvailabilityProjector` | 已提交的 Session/Turn/Tool/Approval/Message/Reservation 事件 | 持久 `unknown/offline/busy/reserved/waiting_approval/cooling/idle/frozen` 投影 | 不选任务，不启动 Goal |
| `IExecutionWindowResolver` | Task 偏好、Agent 模型路由、提供商/模型价格时段、`TimeProvider` | `allow/defer/unknown`、window key、profile version、next eligible time | 不保存另一份工作区策略 |
| `TaskGoalDispatchCoordinator` | Ready/Deferred Task、Availability、Window、Reservation | 确定性候选、原子 Task-bound Goal 启动、可计算的 defer 原因 | 不直接调 LLM，不批量合并任务 |

常规触发链为：

```text
task.ready
  | agent.availability.changed -> idle
  | execution_window.opened
  | reservation.expired
        -> TaskGoalDispatchCoordinator
        -> deterministic candidate query
        -> window + availability + user-queue fence
        -> Task/Agent reservation transaction
        -> StartGoalFromTask transaction
        -> goal_outbox signal
```

`idle` 是保守的充分条件，而不是简单的“当前没有 LLM 流”。至少要求：无活动 Turn/Tool/SubAgent 结算、无待处理用户消息、无待审批、无其他 Reservation、cooldown 已结束、projection 新鲜且不是 `unknown`。任一条件不满足都必须记录稳定 suppression code，但不制造空转的重试消息。

## 7. 证据与完成判定

### 7.1 完成不是模型自报

Agent 每轮输出 `DONE`、自然语言“已完成”或工具 `goal_complete_proposal` 只能生成 proposal。GoalCoordinator 必须同时满足：

1. Goal 未被暂停、取消或 epoch 失效；
2. 明确的验收条件均有可追溯证据；
3. 需要的命令、测试、文件、Artifact 或外部只读事实已经 settle；
4. 没有 pending tool、approval、sub-agent 或未收敛失败；
5. deterministic gate 允许完成；
6. Verifier 返回 `complete`；
7. Coordinator 以当前 aggregate version 原子提交 `goal.completed`。

### 7.2 Evidence capsule

Verifier 不读取无限 transcript，只接收有界、结构化、带来源的 evidence capsule：

- objective、约束和验收条件；
- 当前 Goal revision、Iteration、剩余预算与上一 verdict；
- 本轮 canonical message/tool/sub-agent/usage/error facts；
- 文件 diff、test result、command exit code、Artifact 引用及哈希；
- 未完成 action、approval、阻塞项和历史进度摘要；
- compaction coverage 与 evidence completeness 标记。

大输出保存为工作区 Artifact，capsule 只携带有界预览、定位信息和完整内容哈希。Verifier 运行在只读能力集：无文件写、无 Shell、无网络写、无工具调用、无子代理、无 Goal 写权限。

Verifier 返回严格结构：

```json
{
  "verdict": "continue | complete | blocked | needs_user | unsafe",
  "reason": "...",
  "evidenceRefs": ["..."],
  "unmetCriteria": ["..."],
  "nextAction": "...",
  "blockerFingerprint": "...",
  "progressFingerprint": "..."
}
```

结构错误或调用失败最多重试 3 次；仍失败时 Goal fail-closed 到 paused，并保留 Error ID，不能把验证失败当作 `continue` 或 `complete`。

### 7.3 阻塞与无进展

- 安全拒绝、权限不足、必须用户批准、缺少只有用户能提供的秘密/选择属于硬阻塞，立即 `needs_user`/blocked。
- 临时依赖失败等软阻塞只有在同一 `blockerFingerprint` 连续 3 个 accepted Goal Iteration 出现后才进入 blocked；此前可以尝试不同的恢复动作。
- 若连续 3 轮 `progressFingerprint`、workspace delta、test delta 和 unmet criteria 均无实质变化，打开 no-progress circuit，暂停并给出已尝试动作和所需输入。
- 相同命令的透明基础设施重试与新的 Goal Iteration 分开计数，禁止用 Goal 循环掩盖底层 retry storm。

## 8. 上下文自动压缩

Goal 不建设独立 compactor。每个 Iteration 进入现有 Agent 请求路径，由 `ContextWindowManager.TrimHistoryAsync` 根据统一阈值调用 `CompactionService`。ADR-042 继续拥有压缩算法、阈值、coverage、summary、checkpoint 和 `/compact` 语义。

本 ADR 追加 Goal 集成约束：

1. Goal objective、phase、budget、iteration、acceptance criteria、next action 和 evidence index 来自 Goal store，并作为动态 `goal-state` 层注入；它们不是 compaction summary 的唯一副本。
2. compaction 必须保留工具调用/结果配对、错误、审批、未完成承诺和 Artifact 引用；coverage 不完整时不提交替换历史。
3. 自动压缩不改变 Goal identity、iteration count、activation epoch 或证据权威。
4. Provider 报 context overflow 时只允许一次“强制 compact 后重试”；仍无法容纳 objective、system policy、goal-state 和必要证据时 pause，并报告需要缩小目标/证据。
5. 生产启用 Goal 前，必须证明 compaction 覆盖全部 eligible history，而不是只覆盖最近固定数量消息。
6. V1 若手工 `/compact` 仍创建 successor session，只允许在 Goal idle 时执行，并原子 rebind `currentConversationId`、递增 epoch、使旧 session continuation 失效；目标架构仍是同 session checkpoint。

## 9. 持久事实与事件

### 9.1 表

| 表 | 责任 |
|----|------|
| `goal_runs` | 当前聚合状态、objective、预算、version、epoch、conversation binding、policy snapshot |
| `goal_iterations` | 每次 accepted iteration、Command/Turn/Run 关联、计数、settlement |
| `goal_verifications` | verifier 输入摘要、verdict、证据引用、模型/策略版本、失败 |
| `goal_outbox` | continuation/verifier 的持久工作意图、lease、attempt、幂等键 |
| `task_goal_bindings` | Task/Assignment 与 GoalRun 的1:1 活动绑定、Task version、Reservation fencing token、绑定终态 |

Goal 当前状态和对应 `ConversationEvent` 必须在同一数据库事务提交。无需再建立一个与 ConversationEvents 竞争的 `goal_events` 日志；Goal 专表是聚合与明细事实，canonical 事件流负责跨域投影和 SSE。

### 9.2 最低事件集

```text
goal.created                    goal.edited
goal.activated                  goal.paused
goal.resumed                    goal.cancelled
goal.cleared                    goal.completed
goal.blocked                    goal.budget_exhausted
goal.failed                     goal.iteration.accepted
goal.iteration.started          goal.iteration.settled
goal.verification.requested     goal.verification.completed
goal.verification.failed        goal.continuation.requested
goal.continuation.dispatched    goal.continuation.suppressed
goal.progress.recorded          goal.circuit.opened
task.goal.bound                 task.goal.unbound
task.goal.completed             task.goal.blocked
```

所有事件至少包含 `eventId/conversationId/sessionId/goalId/goalRevision/activationEpoch/aggregateVersion/iterationNumber/causationId/correlationId/occurredAt`。SourceKind 增加 `goal`；投影不得通过 synthetic user 文本猜测 Goal 事件。

## 10. API 与多客户端

保留现有系统命令入口：

```text
POST /api/v1/conversations/{conversationId}/system-commands
```

并提供结构化控制/查询：

```text
GET  /api/v1/conversations/{conversationId}/goal
POST /api/v1/conversations/{conversationId}/goals/commands
GET  /api/v1/goals/{goalId}
GET  /api/v1/goals/{goalId}/iterations
GET  /api/v1/goals/{goalId}/verifications
```

写请求包含 `clientRequestId` 和可选 `expectedVersion`；冲突返回当前状态与 version。Connector 继续经过身份、会话绑定、命令白名单与幂等检查；未经授权的 Connector 不得用 `/goal` 建立后台执行权限。

Admin Web 和 Desktop/WebView 共享：

- Conversation 顶部 `GoalBanner`：objective、phase、`iteration/max`、剩余预算、当前动作、压缩状态；
- pause/resume/cancel/edit 控件；
- Iteration/Verification 时间线和证据链接；
- `Snapshot + Cursor SSE` 恢复，不依赖浏览器本地计时或轮询；
- synthetic Goal turn 使用专门的 Goal presentation，不显示成用户头像发言。

客户端关闭不影响已受理的 active Goal；产品同时必须清楚展示 active 状态和停止方式，不能在后台静默运行。

## 11. 权限、策略和安全

- Goal 创建时冻结 agent/model/tool capability、workspace quota/安全策略和调用者 authority snapshot；每次敏感操作仍使用执行时最新的更严格策略。
- Goal 不能自行扩大 max iterations、token/cost、工具、目录、网络或审批权限；策略变化只能收紧，扩大需用户显式新授权。
- `/goal` 不等于 `/yolo`。现有工具授权、审批、sandbox、路径和连接器限制继续有效。
- Goal prompt 必须把 objective 当作不可信用户数据；证据中的网页/工具内容同样不得提升为系统指令。
- Goal 不能因自己的 synthetic message 触发 `message_event`、Auto Task 或另一个 Goal，防止自动化回环。
- Task-bound Goal 只能继承 Task 和 Agent 当前已授权的 capability；Dispatcher 不得因为进入低价窗口而提升 Yolo、工具权限或破坏性操作权限。
- `off_peak_only` 是硬 admission 条件。显式 Run Now override 只能由用户命令产生，不能由 Coordinator、Agent 或 Goal 自行构造。
- clear/cancel 不物理删除审计、usage、Artifact 或错误证据；数据保留遵循现有 Workspace 策略。

## 12. 故障和恢复

| 故障 | 决定 |
|------|------|
| Worker 在 claim 前崩溃 | outbox 保持 pending，signal/恢复扫描再次认领 |
| accept 事务后崩溃 | 确定性 Command ID 保证不重复受理；执行 Worker恢复 |
| 运行中 pause/cancel | epoch 失效；安全 settle 后不再 continuation |
| Verifier 崩溃/格式错误 | 独立 attempt，最多 3 次，之后 fail-closed pause |
| Core 重启 | active Goal disarmed/paused；显式 resume 新 epoch |
| SSE 断线/客户端刷新 | 从 Snapshot + cursor 重建；服务端事实不变 |
| Compaction 失败 | 原历史保留；一次 overflow recovery 后仍失败则 pause |
| 预算耗尽 | 提交 `goal.budget_exhausted`，不可自动续行或重置 |
| stale late result | 仅记审计，CAS/epoch fence 禁止改变当前 Goal |
| Availability 投影过期/重建中 | 视为 `unknown`，不认领新任务 |
| 低价时段配置缺失/无效 | `off_peak_only` defer/fail closed，保留可操作诊断，不回退为 anytime |
| Task-bound Goal 启动事务崩溃 | Assignment/Reservation/Binding/Goal/Outbox 全成或全不成；恢复扫描按幂等键重试 |
| Goal 终态与 Task 终态竞态 | Coordinator 以 Task/Goal 双 version 和 fencing token CAS；迟到结果只记审计 |

## 13. 可观测性

最低结构化日志字段：

```text
goalId, goalRevision, activationEpoch, aggregateVersion,
conversationId, sessionId, iterationNumber, commandId, turnId, runId,
outboxId, verifierAttempt, verdict, evidenceRefs,
remainingIterations, remainingToolCalls, contextPressure,
compactionId, admissionDecision, suppressionReason, errorId
```

最低指标：

- `goal_active_total`、`goal_terminal_total{phase}`；
- `goal_iteration_total{result}`、`goal_iteration_duration_seconds`；
- `goal_continuation_lag_seconds`、`goal_outbox_pending`；
- `goal_verifier_total{verdict}`、`goal_verifier_failure_total`；
- `goal_no_progress_circuit_total`、`goal_blocker_total{kind}`；
- `goal_compaction_total{result}`、`goal_context_overflow_recovery_total`；
- `goal_budget_exhausted_total{dimension}`。
- `task_goal_dispatch_total{result,reason}`、`task_goal_binding_active_total`；
- `agent_availability_total{state}`、`agent_idle_without_runnable_task_seconds`；
- `execution_window_open_total{provider,model}`、`task_low_price_window_missed_total{reason}`。

审计页面必须能从 Goal 跳转到 Iteration、Turn、Run、Tool、SubAgent、Verification、Compaction 和 Artifact，且 replay 与 live 投影一致。

## 14. 被否决的方案

### 14.1 用 Heartbeat 周期性发送“继续”

否决。Heartbeat 没有目标聚合、证据门禁、精确预算和用户竞态语义；`heartbeat=0` 或高峰 defer 还会改变正确性。Goal 必须由已提交事件和 durable outbox 驱动。

### 14.2 把现有 Agent Loop 上限改成 256

否决。一个超长 Turn 不利于用户插入、持久恢复、逐轮验证、压缩边界和成本控制，也混淆 Goal Iteration 与 LLM round。

### 14.3 浏览器/客户端自动反复发送消息

否决。关闭页面、断网、多端并发和刷新都会导致丢失或重复；Connector 也无法共享相同语义。客户端只能发 command 和消费 projection。

### 14.4 Agent 自报 DONE 即完成

否决。DONE 只是提议；可执行目标必须有确定性证据和独立只读验证，Coordinator 是唯一终态写入者。

### 14.5 resume 重置 256 轮或允许无限续期

否决。它把有限预算变为可绕过的无限循环。改变目标或需要新预算应显式创建新的 Goal，并保留旧 Goal 终态关联。

### 14.6 每次 Goal Iteration 新建 Session

否决。默认保持同一 Conversation/Session，复用 canonical history 和 compaction；只有现存手工 compact successor 作为受控过渡。

### 14.7 Goal 自建压缩器或只保留最近 N 条

否决。压缩只有一个 Owner；固定截断不能证明早期未完成项、tool pair 和审批已覆盖。

### 14.8 低峰时向 Agent 发一条“请执行看板任务”提醒

否决。提醒没有 Task/Assignment/Goal 的原子绑定，无法证明任务已经接受、持续执行或以证据完成；它也会被普通消息批处理、心跳和会话队列时序改变语义。必须创建 Task-bound GoalRun。

### 14.9 为任务调度新增工作区 `work-policy.json`

否决。Task 已通过 `executionWindow` 表达自身偏好；另一份工作区文件会形成重复权威。低价时段属于模型/提供商价格档案，通过现有 LLM 配置及集中 Resolver 解析；Task 只保存选择和派发时快照。

### 14.10 把多个自动 Task 合并成一个普通 Message Delivery

否决。这会使只有被 claim 的一条 Delivery metadata 能进入 Active Task Context，破坏每个 Task 的 Assignment、Version、Fence Token、Goal 预算和终态归属。Task-bound Goal 指令必须单独受理。

## 15. 影响

### 15.1 正面影响

- `/goal 尽最大所能完成 X` 具有明确、可恢复、可审计的产品语义；
- 续行延迟由执行 settle 驱动，不受 Heartbeat 周期影响；
- Web、Desktop 和 Connector 使用同一命令与状态；
- 256 轮硬边界、证据验证和无进展熔断限制成本与自激风险；
- 复用 Conversation 执行链和 compaction，避免两套 Runtime 事实；
- 重启、取消、用户插入与迟到结果都有确定性处理。

### 15.2 代价与风险

- 新增 Goal 聚合、outbox、verifier 和投影，事务与恢复测试复杂度增加；
- 256 个外层 Iteration 仍可能产生高成本，必须同时执行 token/tool/time/cost 限额；
- Verifier 不是形式证明，需用 deterministic gates 和真实验收证据约束；
- 当前 compaction 若不能证明全历史覆盖，就不能安全开放长 Goal；
- active Goal 跨客户端存在“用户忘记正在运行”的产品风险，必须提供显著状态、通知和一键暂停。

## 16. 与现有文档的裁决关系

1. ADR-042 继续是压缩算法与 `/compact` 的权威；本 ADR 只规定 Goal 如何消费和约束压缩。
2. ADR-057/ADR-059 继续是 canonical Conversation 事件、可靠受理和执行命令链的权威；Goal 不另建执行总线。
3. ADR-072 的第一阶段手工 Task 闭环仍明确排除 Goal，本 ADR 不追溯改写其历史交付范围；但从 2026-08-21 起，“开启完整 Auto Dispatcher”的后续生产架构必须遵循本 ADR 的 Task-bound Goal、Availability Sensor 和 Dispatch Coordinator 裁决。
4. ADR-073 的总体产品施工优先级仍有效；本 ADR 不宣称 Goal 已进入已完成或当前 P0。
5. `deepseek-reference-architecture-master-plan-2026-08-14.md` 和工作区 TODO 设计中的 Goal 草案，若与本 ADR 在轮次、状态、Heartbeat、验证、恢复或多入口命令上冲突，以本 ADR 和配套完整设计为准。
6. Proposed ADR 和设计文件不是实现证据。只有通过第 17 节门禁后，才能把状态改为 Accepted/Implemented。

## 17. 实施门禁与验收

### G0：合同与数据库

- 命令 grammar、错误码、Goal/Iteration/Verification/outbox schema 和迁移评审通过；
- 256 外层轮次和内层 Agent round 字段在类型、日志、API、UI 中名称不同；
- aggregate CAS、epoch 和 boot fence 有状态机属性测试。

### G1：统一命令与多入口

- Web、Desktop/WebView 和授权 Connector 的同一命令产生相同 Goal aggregate/event；
- 重投同一 `clientRequestId/externalMessageId` 不创建第二个 Goal；
- 非授权 Connector、越界轮数和并发 set 返回确定性错误。

### G2：可靠续行

- Goal command 到 synthetic Turn 走现有 Acceptance/Execution 链；
- settle 后无需 Heartbeat 即投递下一 Iteration；
- crash at every transaction boundary 不丢不重，stale outbox 不消费轮数；
- 普通用户消息稳定抢占 continuation。

### G3：验证与终态

- DONE 无证据不能完成；具备证据的目标经 gates+Verifier 后原子完成；
- hard blocker、同 blocker 3 次、无进展 3 次、Verifier 失败 3 次均产生预期终态/暂停及证据；
- Agent/Verifier 无法直接写 completed。

### G4：压缩与长会话

- 超过自动阈值的 Goal 完成同 session compact-and-continue；
- coverage manifest 证明全部 eligible history、tool pair、未完成项和 Artifact refs 被处理；
- compaction 失败不替换原历史；overflow 只 compact-and-retry 一次；
- 若保留 successor session，idle rebind 和 stale suppression 通过故障注入。

### G5：恢复、安全与预算

- Core 重启后 active Goal 默认 paused，显式 resume 才继续；
- pause/cancel/stop、approval、权限收紧、WorkAdmissionFence、token/tool/time/cost 上限全部 fail closed；
- 第 256 个 accepted Iteration 后必然 `budget_exhausted`，第 257 个无法受理；resume 不能重置。

### G6：UI、投影与运维

- Snapshot+cursor 在刷新/SSE 重连后恢复 GoalBanner、Iteration 和 Verification；
- synthetic continuation 不冒充用户，不触发 message-event/Goal 回环；
- 日志、指标、Error ID 和审计深链齐全；
- Feature Flag 可以停止新的 Goal 与 continuation，同时保留读取、pause/cancel 和既有事实。

### G7：Task-bound Goal 与低峰自动派发

- `TaskAutoDispatch.Enabled` 不得在 `GoalRuns.Enabled` 和 `TaskBoundGoals.Enabled` 之前开启；组合根必须 fail fast；
- Ready Task 在可验证低价窗口打开、Agent 持久投影为 Idle 时，无需 Heartbeat 即原子建立 Assignment/Reservation/Binding/Goal/Outbox；
- `off_peak_only` 在窗口配置缺失、路由改变、进入高价窗口或 Availability 过期时均 fail closed，并记录 `nextEligibleAt` 或可操作诊断；
- 并发两个 Coordinator 时，同一 Task 和同一 Agent 最多各有一个成功 Reservation/active Goal binding；
- 自动 Task 不进入普通多消息批合并，Runtime 中 Task/Assignment/Goal/epoch/fencing token 与数据库一致；
- Goal `completed/blocked/cancelled/failed/budget_exhausted` 与 Task 状态映射经 Task/Goal 双 CAS 和证据门禁验证；Delivery ACK 不能使 Task 完成；
- 在丢失 signal、Core 重启、窗口边界和 Reservation 过期故障注入下，恢复扫描不丢不重，且不在高价窗口误启动。

生产开放顺序建议为：内部开发用户、单 Workspace 灰度、限制 8/32/128 轮观察、最后开放 256；灰度上限可以更小，但系统合同始终不得超过 256。

## 18. 最终不变量

1. Goal 正确性不依赖 Heartbeat、Timer 或浏览器存活。
2. 一个 Goal 最多消费 256 个 accepted Goal Iteration。
3. Goal Iteration 与 Agent Loop Round 是两个预算域。
4. Goal 状态不从 transcript 或 compaction summary 反推。
5. Agent 和 Verifier 只能提出 verdict，Coordinator 是唯一 Goal 终态写入者。
6. Continuation 具备 durable intent、幂等、CAS、epoch、lease 和 fencing。
7. 用户输入、安全、审批和策略收紧优先。
8. 重启不自动恢复自主执行授权。
9. 压缩不改变 Goal identity、预算和证据权威，coverage 不完整不提交。
10. 所有客户端共用服务端命令、状态机、事件和投影。
11. Auto-dispatched WorkspaceTask 必须绑定唯一 GoalRun，Goal 是自动任务持续执行的前置，不是一条自然语言提醒。
12. Agent Idle 是持久事实的保守投影；unknown 和过期投影永远不自动 Claim。
13. Task 执行偏好来自 `Task.executionWindow`，低价时段来自现有提供商/模型配置；不存在平行的工作区 `work-policy.json` 权威。
14. 低峰派发由事件和 durable intent 驱动；Heartbeat 不得决定任务是否开始、继续或完成。
15. Task 和 Goal 的绑定、版本、Reservation token、证据与终态可从 canonical facts 完整重建。

## 19. 2026-08-31 Goal 用户控制补充裁决

Goal Header 必须始终给用户一个结构化控制入口，详细矩阵见
`Docs/Features/任务调度器与Goal用户控制面设计.md`：

1. 无 Goal 时可 `set` 创建；Active 可 pause/cancel；Paused 或 Blocked 可 resume/cancel；终态只能新建；
2. UI 文案使用“停止”，服务端语义仍是可审计的 `cancel`，不得物理删除 Goal、Iteration 或证据；
3. resume 不重置已消费 Iteration，`budget_exhausted` 不可恢复额度；
4. Scheduler Pause 与 Goal Pause 是两个不同动作：前者只关闭新自动准入，后者才暂停具体 Goal 的 continuation；
5. 所有 Web/Desktop/Connector 客户端复用服务端 Goal command 和 snapshot，不从消息文本或本地状态反推 phase。
