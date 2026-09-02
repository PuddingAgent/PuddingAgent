# Task-bound Goal 与 Agent 状态感知自动派发代码级施工计划

> 状态：实施中；G2/G3、Task-bound Goal 原子启动、结构化 Agent 路由、版本化 WorkUnit、调用边界 Token/成本硬预算、五分钟 Tracker/repair、全 Agent Availability 扫描与 provider/model 低价窗口 Resolver 已落源码；2026-08-29 已由 Desktop 构建并加载新程序集，完成一次真实自动派发 smoke；事件驱动唤醒、Goal 成本/后代工具归因、缓存 >99% 与七夜生产验收仍未完成
> 日期：2026-08-21
> 权威决策：[ADR-074 Goal 持久目标、自主续行与自动压缩](../07架构/89ADR-074Goal持久目标自主续行与自动压缩ADR.md)
> 任务领域边界：[ADR-072 工作区 TODO、峰谷 Auto 派发与定时任务第一阶段](../07架构/86ADR-072工作区TODO峰谷Auto派发与定时任务第一阶段ADR.md)
> Goal 完整设计：[Goal 持久目标、自主续行与自动压缩完整设计方案](Goal持久目标自主续行与自动压缩完整设计方案.md)
> 2026-08-28 当前边界：已修改源码、测试与 Shadow 配置，并由 Desktop 产品主管重建/重启 Core；仅执行幂等 schema 升级，未清理或重置 `D:\data`

## 1. 施工结论

目标链不是“晚上定时给 Agent 发一条消息”，而是：

```text
Task 进入 Ready/Deferred
  + Agent 的持久 Availability 投影变为 Idle
  + Task 选择的执行窗口当前允许
      -> TaskGoalDispatchCoordinator 原子预留 Task + Agent
      -> StartGoalFromTask 原子建立 TaskGoalBinding + GoalRun + GoalOutbox
      -> GoalContinuationWorker 进入现有 Conversation 可靠执行链
      -> Turn settle -> Goal Verifier -> continue/terminal
      -> Goal/Task 双 CAS 提交终态并释放 Reservation
```

施工顺序是强依赖链：

1. 先完成 ADR-074 GoalRun 持久化和 event-driven continuation；
2. 再建立可持久 Agent Availability Sensor；
3. 再建立 provider/model 价格时段 Resolver；
4. 最后启用 TaskGoalDispatchCoordinator 和低峰窗口触发；
5. Heartbeat 只保留原有周期检查用途，不参与 Task 启动、Goal 续行或终态判定。

### 1.1 2026-08-26 第一批实施记录

本批次先落“不会误派发、不会把等待子代理误判为空闲”的安全底座，没有绕过第 1 节的强依赖链：

- 新增持久 `agent_availability_projection`，从 Agent 配置、Task/Goal 占用、Chat execution command、Message delivery、运行中子代理和自动工作租约重建；缺失或过期投影为 `Unknown`，不存在直接 `SetIdle`。
- 新增 `agent_execution_reservations`，以 partial unique index 保证同一 Workspace 内一个 Agent/Task 只有一个 active 自动工作槽；租约带单调 fencing token，并支持 renew/release/expiry。
- 新增 `task_dependencies` 与 finish-to-start 图 API；拒绝自依赖和环，前置失败/取消/无完成证据归档会产生 `Broken`，非终态为 `Waiting`。
- `HeartbeatOrchestrator` 在原进程内 execution gate 之后增加持久 Availability gate；等待子代理、持有 Task/Goal、排队消息、Reservation、未知或投影重建失败时均跳过并重新排队，不打断 Agent 节奏。
- `agent_status` 改读持久投影；无新鲜投影时报告 `unknown`，并返回 busy reason、active Task/Goal/SubAgent，不再用“未在 wake queue”推导 `idle`。
- 新增确定性 `TaskAutoDispatchEvaluator`：priority → due/notBefore → created → sortOrder → taskId；要求偏好 Agent、依赖满足、30 分钟 idle grace、窗口允许，同一轮同一 Agent 最多选择一个 Task。
- 新增 `TaskAutoDispatchWorker`，默认 `Enabled=false`，当前只接受 `Mode=shadow`；配置为其他模式会拒绝运行。它不会写 Task、获取 Reservation、发送消息或创建 Goal。
- 新增认证诊断 API：Availability 查询/重建、Auto evaluate-only、依赖增删/读取/评估。Admin 后续只消费这些 canonical 结果，不在浏览器复制状态机。
- 执行窗口首批仅能证明 `anytime`；`inherit` 与 `off_peak_only` 在 provider/model 价格档案 Resolver 未落地前统一 `Unknown` 并 fail closed。

### 1.2 2026-08-26 第二批实施记录

- `GoalContinuationWorker` 已实现 durable outbox 的 due scan、claim/lease/fencing、过期恢复、busy defer、stale suppress 与有界 dead-letter；synthetic Turn 统一进入 `ConversationAcceptanceStore -> ChatExecutionCommand -> ChatExecutionWorker`，不直接调用 LLM。
- Conversation Acceptance 在同一事务中重验 Goal `activationEpoch/aggregateVersion`、单 Iteration、outbox lease；Task-bound Goal 还重验 Task version、active Assignment、Binding、Reservation fencing token 和 lease，并原子消费一次 Iteration 预算。
- `GoalSettlementWorker` 已从 canonical Turn 终态构造有界 Evidence Capsule，保守 Verifier 不接受自然语言 DONE；无独立完成事实只会继续。普通 Goal 的失败/取消/证据不完整进入可恢复 Blocked；Task-bound 尝试释放执行权时本次 Goal 终结为 Failed、Task 保持 Blocked/NeedsReview，Task canonical Completed 才允许 Task-bound Goal 完成。
- `TaskGoalDispatchTransactionStore` 已把 Task `Ready/Deferred -> Assigned`、Assignment、Agent Reservation、TaskGoalBinding、GoalRun、首个 GoalOutbox、Task events、canonical Goal events 和 Availability Reserved 投影放进同一个 Serializable SQLite 事务，并提供确定性幂等回放与 lost-race 稳定码；派发会在同一事务内将该 Agent/会话由旧版本遗留的 detached Blocked Task Goal 可审计退役（允许来自前一 Task），避免 `UX_goal_runs_active` 被误报为竞争。
- `TaskAutoDispatchWorker` 支持 `shadow | authoritative`。authoritative 仍要求 `TaskAutoDispatch.Enabled && TaskBoundGoals.Enabled && GoalRuns.Enabled && GoalRuns.ContinuationEnabled`，配置不满足时启动校验失败；默认配置四项均不开放自动执行。
- 自动 Goal Turn 注入服务端生成的 Active Task metadata（task/assignment/version/reservation fence），Runtime 继续使用原生 `task_claim/task_update` 工具更新 Task；普通 Message Delivery 不参与 Task-bound Goal。
- 聚焦验证：Platform 46 tests（Goal/settlement/atomic start/evaluator/reservation）通过；Runtime ActiveTask reservation fence 映射 1 test 通过；PuddingHost build 0 error。

当前明确未完成：provider/model 价格时段解析与 `off_peak_only` 真实开放、事件驱动边界调度（当前由有界恢复扫描降低延迟）、完整只读 LLM Verifier/证据策略、Task-bound blocked 后的重新预约恢复、Admin 联合状态面板，以及进程外重启/真实 Provider smoke。因此这是“安全默认关闭的 authoritative 源码链路”，不是自动任务生产验收，ADR-074 继续保持 Proposed。

### 1.3 2026-08-28 夜间吞吐复盘后的安全灰度

- 产品配置把 `TaskAutoDispatch` 设为 `Enabled=true, Mode=shadow`，恢复扫描与前台 idle grace 均为 5 分钟。该阶段只读取任务看板、重建 Agent Availability 并输出候选结果，不创建 Assignment、Reservation 或 Goal。
- Availability 不再把终态 Task 遗留的未释放 Assignment 当作活跃容量事实；只有与 `WorkspaceTask.ActiveAssignmentId` 一致且 Task 非终态的 attempt 才能占用 Agent。历史脏行继续保留审计，不在重建时直接改写 `D:\data`。
- Task→Goal 原子启动的最终 Agent fence 使用同一条 canonical active/non-terminal Assignment 规则；终态脏 attempt 不再出现“Availability 判 Idle、事务又判 Busy”的双重事实冲突。
- Goal continuation acceptance 统一使用注入的 `TimeProvider` 校验 outbox/reservation lease 与续租，避免测试时钟、系统时钟和调度器时钟不一致造成假 `stale_lease`。
- 通用 Task PATCH 对无 Assignment 的人工完成写 canonical `TaskCompleted`，并标记 `manual_without_execution`；已有 active Assignment 的 Task 必须走带证据的 `task_update completed` / Task-bound Goal settlement，不能再由看板状态 PATCH 伪造执行完成。`mark_failed` 同事务释放 Assignment。
- Scheduler 恢复扫描默认周期从 30 秒改为 5 分钟，避免无候选时高频轮询；未来事件信号负责低延迟唤醒，五分钟扫描只承担恢复与对账。
- 实时审计显示所有 8 张实施卡仍为 Backlog；Shadow evaluator 不会擅自 refinement Backlog。因此五分钟扫描上线不等于统一调度器生产验收，更不允许直接切 authoritative。2026-08-28 后续源码批次已补结构化路由、Backlog 准入、Tracker、确定性 repair 与 WorkUnit 执行推进；这些源码尚未部署，权威切换门仍未完成。

authoritative 准入新增以下硬门：

1. Backlog refinement 必须产生稳定 `ready | needs_refinement` 决策，并显式提交 Ready；不得把全部 Backlog 无条件点火。
2. 历史非终态 Assignment 必须经 Tracker 对账为 running/waiting/requeue/blocked，不能靠“清表”恢复空闲。
3. Task 必须具有结构化 taskType/requiredCapabilities；Agent 匹配必须记录 capability、route、health、capacity 的 score breakdown，不能只依赖 `preferredAgentId`。
4. 先在 shadow 对账候选与真实前台占用，再按 `authoritative-single -> authoritative-bounded` 灰度；生产完成仍需 7 个夜间窗口。

配置入口（默认值无需写入配置文件）：

```json
{
  "TaskAutoDispatch": {
    "Enabled": true,
    "Mode": "shadow",
    "WorkspaceIds": ["default"],
    "ScanInterval": "00:05:00",
    "MinimumIdle": "00:05:00",
    "CandidateLimit": 100,
    "TaskTypeRoutes": {
      "implementation": {
        "RequiredCapabilityIds": ["cap-file-write", "cap-shell"],
        "AllowedRoles": ["Service"]
      },
      "research": {
        "RequiredCapabilityIds": ["cap-http-fetch"],
        "AllowedRoles": ["Service"]
      },
      "review": {
        "AllowedRoles": ["Service", "Audit"]
      }
    }
  },
  "TaskBoundGoals": {
    "Enabled": false,
    "GoalIterationBudget": 32,
    "ReservationLease": "02:00:00"
  }
}
```

### 1.4 2026-08-28 结构化任务与模型路由实施记录

本批次把“根据任务类型合理分配”从标题猜测改为稳定合同，并保持旧任务安全语义：

1. `WorkspaceTask` 新增 `taskType`、`requiredCapabilityIds`、`requiredProviderId`、`requiredModelId`、`allowAgentFallback`。SQLite 启动 bootstrap 对现有表幂等补列；Internal/External API、Admin/Agent 详情和 CAS Store 使用同一字段集。
2. `preferredAgentId` 是亲和性而不是天然全局锁。旧任务的 `allowAgentFallback=false` 保持首选独占；只有显式开启 fallback 或没有首选 Agent 时，Scheduler 才能选择其它兼容 Agent。
3. `TaskTypeRoutes` 由配置声明任务类型的 `AllowedRoles`、能力、provider/model 约束；任务自身显式约束与类型规则取交集。Provider/model 仍由 Agent 模板单一拥有，Task 只能声明要求，不能在派发时改写模板。
4. `TaskAgentRouteMatcher` 只读取结构化字段、Agent 模板和运行能力标记，不读取 title/description。首选优先，其余候选按稳定 AgentId 排序；Availability、idle grace、执行窗口和同轮单 Agent 单任务逐候选重验。
5. 路由选择生成包含 Task 约束、TaskTypeRoute、Agent provider/model/capabilities 和模板更新时间的 SHA-256。`TaskGoalDispatchTransactionStore` 在原子事务前重新读取 Agent 目录、重新应用当前类型规则并重算指纹；不兼容或配置漂移返回 `agent_changed`，不产生 Assignment/Reservation/Goal。
6. 产品经 Desktop 主管原子重建后由 PID 8824 加载；启动日志确认 5 个路由列原地补齐，Shadow 扫描执行且 `goal_runs=0`、active reservation=0。Task 路由/Store/完成事实/Availability/Goal 原子栅栏聚焦回归 46/46 通过。

仍未完成：不确定状态的人工/策略化 repair、checkpoint/AwaitHandle 消费、Task-bound blocked 后重新预约、动态质量/成本/TPS 模型评分、真实 off-peak Resolver、`authoritative-single` 灰度和七个夜间窗口验收。结构化路由、确定性 repair 和执行快照关闭了部分派发与恢复缺口，但不代表端到端自动执行已生产验收。

## 2. 冻结不变量

1. 一个自动 Task 最多有一个非终态 GoalRun，一个 Task-bound GoalRun 只绑定一个 Task/Assignment。
2. `TaskAutoDispatch.Enabled` 只能在 `GoalRuns.Enabled && TaskBoundGoals.Enabled` 时开启；否则 Host 启动验证 fail fast。
3. `Task.executionWindow` 是任务偏好权威；不创建 `work-policy.json`。
4. 当前低价时段由 Agent 有效 provider/model 路由与现有 LLM 配置中的价格时段共同决定；未知时 fail closed。
5. Agent `idle` 由持久事实保守投影；重启后初值是 `unknown`，不从进程内默认值推导。
6. Task-bound Goal 不进入通用 Message Delivery 的多消息批合并。
7. Agent 自然语言“完成”、Delivery ACK 和 Goal `DONE` 都只是输入事实，不是 Task Completed 命令。
8. Task/Assignment/Reservation/Binding/Goal/Outbox 在一个 SQLite 短事务中全成或全不成；LLM、Message Send、Tool 和网络不在事务内。
9. 用户消息、pause/cancel、approval、权限收紧和 Run Now override 优先于自动续行。
10. 正确性不依赖 signal、Timer、SSE、浏览器、Heartbeat 或进程内 Channel；这些只能降低延迟。
11. Scheduler 不从任务标题/描述推断能力或模型；路由只使用结构化 Task metadata、版本化 TaskTypeRoute 和 Agent 模板快照。
12. 兼容 Agent fallback 必须由 `allowAgentFallback` 显式授权；路由指纹在最终启动事务前重算，配置漂移 fail closed。
13. 自动派发必须由 Task 的 `autoDispatchEnabled=true` 显式 opt-in；普通 Ready/Deferred Task 默认不属于后台自动候选。

### 1.5 2026-08-28 自动执行 opt-in 与 Backlog refinement Shadow

- `WorkspaceTask` 与全部 Task API/Store 新增 `autoDispatchEnabled`，SQLite 既有表幂等补列且默认 `false`。`TaskAutoDispatchEvaluator` 的 Ready/Deferred 查询和最终 Task→Goal 事务都重验该字段；关闭 opt-in 会使启动 fail closed。
- 新增只读 `TaskBacklogRefinementEvaluator`。每五分钟只检查已 opt-in 的 Backlog，依次验证 description、acceptance criteria、非 `general` 的 taskType、TaskTypeRoute 与至少一个兼容 Agent；输出 `ready_for_auto_dispatch | description_required | acceptance_criteria_required | task_type_unclassified | no_compatible_agent`，Shadow 不改变 Task 状态。
- 8 张实施卡已显式登记 `taskType=implementation`、canonical `cap-file-write/cap-shell`、`allowAgentFallback=true` 与 `autoDispatchEnabled=true`，当前版本 v4；它们继续留在 Backlog，不由 Shadow 私自点火。首次真实 Shadow 曾因错误使用粗粒度 `runtime:*` 约束而将 8 张全部判为 NeedsRefinement；模板证据显示两个 Service Agent 的粗粒度布尔开关为 false、但能力注册表拥有 `cap-file-write/cap-shell`，因此路由已收敛到 canonical Capability ID，不修改 Agent 权限。
- `TaskBacklogRefinementStore` 已实现 future authoritative 模式的唯一 Backlog→Ready CAS：重验 Task version/opt-in/内容/Agent/TaskTypeRoute/路由 SHA-256，单事务写 Ready、version 与 canonical `TaskReady/backlog_refined`；任何漂移不产生状态事件。
- 新增 `TaskExecutionTracker`，与同一五分钟 Worker 同周期关联 Task、Assignment、Reservation、Binding、Goal、Iteration、ExecutionCommand/Run 与 continuation outbox，输出 `Healthy/Waiting/Stalled/Inconsistent/CleanupRequired` 及稳定 reason code。authoritative 模式下，独立 repair coordinator 仅对白名单中的确定性状态重新读取全部 fence 后修复；Tracker 本身仍不写状态。
- 最终产品由 PID 13084 加载且 `/health/ready=200`；`pudding-20260828_036.log` 的 Shadow 显示 `backlog=8, refinementReady=8, needsRefinement=0, promoted=0, candidates=0, started=0, tracked=0, healthy=0, waiting=0, stalled=0, inconsistent=0, cleanupRequired=0`。数据库保持 auto Backlog=8、Ready=0、Goal=0、active Reservation=0、active Binding=0；聚焦回归 55/55 通过。

本阶段已完成 opt-in、refinement/route/tracking 判断、authoritative Backlog CAS 和确定性 repair 源码，但产品仍为 Shadow，且 8 张卡尚未登记完整依赖，不确定的历史非终态 Assignment 也不会被自动猜测清算。不得把日志中的 `refinementReady` 当作 Ready 或已派发，也不得据此提前开启 authoritative。

### 1.6 2026-08-28 WorkUnit 执行计划与原子计划围栏

- 不新建第二套 Task 台账：`WorkspaceTask` 保持看板与状态权威，复用 `task_plan_runs/task_nodes` 保存某个 Task version 的不可变执行快照。计划表新增 `workspaceTaskId/version`、`schemaVersion/planVersion`、`planKind` 和 `planFingerprint`；节点新增 WorkUnit kind/sequence、依赖、能力、冲突范围、轮次/工具/时长/token/cost 预算、重试策略、进度指纹和 checkpoint artifact。
- 新增纯函数 `TaskExecutionPlanCompiler`，只读取结构化 `taskType`、Task 能力约束和 `TaskTypeRoute`，不读取标题/描述，也不调用模型。`implementation/operations/deployment` 编译为 `Explore -> Plan -> Change -> Test -> Review`；research/test/review/documentation 使用各自有界子图；未知 `general` fail closed。
- 计划 SHA-256 覆盖 Task version、schema/plan version、WorkUnit 顺序/依赖、能力、默认 checkout 冲突范围、预算和重试策略。Shadow 候选携带该指纹；`TaskGoalDispatchTransactionStore` 在任何写入前重编译并比较，不一致返回 `execution_plan_changed`，且不留下 Plan、Node、Assignment、Reservation 或 Goal。
- authoritative 启动时，执行计划根节点、全部 WorkUnit、Assignment、Reservation、TaskGoalBinding、GoalRun、首个 Outbox 和 canonical events 位于同一个 Serializable SQLite 事务。Binding、Goal route snapshot、Outbox 和 `task.goal_bound` 事件都记录 `taskPlanId/planFingerprint`；幂等 replay 不重复创建计划或节点。
- 新增 `work_unit_await_handles` 持久合同，冻结 `waiting/signaled/consumed/cancelled`、external id、fencing token 和时间戳边界；本批次没有实现其运行时生产/消费，不会把“表已存在”报告成异步等待闭环已完成。
- 启动初始化器现在显式调用 `TaskPlanningSchemaBootstrapper`；旧 SQLite 库幂等补列/表/索引。只读 `TaskExecutionTracker` 进一步投影 plan fingerprint、plan status 和当前 WorkUnit，并对 plan missing/fence mismatch/work-unit missing fail closed。
- 聚焦验证 30/30 通过：确定性计划与指纹、schema 旧库升级、原子全成/全不成、幂等无重复、指纹漂移拒绝、Tracker 当前 WorkUnit/缺失计划诊断。产品部署后仍保持 `Mode=shadow` 与 `TaskBoundGoals.Enabled=false`，因此运行库不会因本批次自动创建 Goal/Plan。
- 扩大调度回归 70/70、Host composition 1/1、Runtime build 0 error；最后一次 Desktop 主管重建结果 success，Core 新 PID 9116，`/health/ready=200`，加载二进制 SHA-256=`fc7fabc8cc9b629d9432b3294c11637f4f3f274a2e9d1a31c1f539b539adbed4`。运行库已出现全部新列和 `work_unit_await_handles`，但 `task_plan_runs/work-unit/await-handle/goal_runs/active binding/active reservation` 均为 0；`pudding-20260828_038.log` 继续报告 `mode=shadow, backlog=8, refinementReady=8, promoted=0, candidates=0, started=0, tracked=0`。

### 1.7 2026-08-28 WorkUnit Runtime 预算交接与二次围栏

- `GoalContinuationWorker` 现在从 Binding 的 `taskPlanId` 选择序号最小的非终态 WorkUnit，把 plan/node/fingerprint、目标、输出合同与预算写入 synthetic Turn 的受信上下文；这些字段用于可观察性和选择，不作为预算权威。
- `ConversationAcceptanceStore` 在创建 Command 的同一事务内重读 Task、Assignment、Reservation、Plan 和全部 WorkUnit，要求 plan fingerprint、当前 node、前置完成关系、Agent 所有权及正数预算全部一致；成功后原子把 WorkUnit 置为 `Running`，任何漂移以 `task_plan_changed` fail closed。
- `ExecutionCommandReader` 在真正执行前再次沿 `Command -> GoalIteration -> TaskGoalBinding -> Task/Reservation -> Plan/Node` 重读 canonical 事实。普通用户 metadata 不能凭空获得 WorkUnit 预算；租约过期、Task version 漂移、Plan/Node 变化或非法预算都会阻止 Runtime 启动。
- `ExecutionRunCoordinator` 将 Agent profile 与 WorkUnit 的轮次、工具调用、时长预算逐项取更严格值，并冻结绝对 deadline；`TurnExecutorAdapter` 把 canonical plan/node/parent identity 传入 `RuntimeDispatchRequest`，供上下文、journal 和子代理继承。以 Explore 默认值为例，全局 `600 rounds / 2400 tools / 24h` 会被钳制为 `25 / 60 / 30m`。
- WorkUnit 的 input/output token 与 cost 上限先从 canonical row 解析；后续第 1.8 节已经补齐调用边界累计扣减与硬停止。本节当时的 23/23 仅是阶段性验证记录，不能覆盖后续实现，也不代表当前 Desktop/Core 已加载新构建。

### 1.8 2026-08-28 执行内核、Token 硬预算与五分钟 repair 收口

- `GoalSettlementStore` 现在在同一事务中重读 Binding、不可变 Plan fingerprint、根节点与唯一 Running WorkUnit。成功迭代原子完成当前 WorkUnit；有后继时强制继续并由下一次 Acceptance 启动下一节点；最后节点只有在 Task 存在 canonical Completed fact 时才完成 Plan/Goal，否则转 `NeedsReview/task_completion_fact_missing` 并释放 Assignment/Reservation，禁止伪完成或空转。
- Plan snapshot 中的 Task version 只属于编译指纹，不再错误地与运行期间会递增的 live Task version 比较；live Task 乐观锁继续由 Binding 的 `ExpectedTaskVersion` 管理。该修复避免第二个 WorkUnit 因正常 `task_claim/task_update` 导致的版本增长被永久拒绝。
- `ExecutionRunCoordinator` 在 Run 开始时冻结 provider/model 价格快照和 WorkUnit input/output/cost 上限。Buffered/Streaming 通过同一 `ExecutionUsageBudgetTracker` 按 provider 调用累计 prompt、completion、cache-hit 与成本；达到上限后不再执行工具或发起下一轮 LLM。启用成本预算但价格未知、或 provider 未返回 usage 时 fail closed，分别记录稳定终态码。
- Streaming 每轮先清空 usage，避免 provider 某轮未返回 usage 时沿用上一轮数据造成重复记账。Token/cost 的最小可执行硬边界是 provider call：无法撤回已发生的一次调用，但调用完成后先记账、再决定是否允许工具和下一轮。
- 五分钟 `TaskAutoDispatchWorker` 在 authoritative 模式下运行独立 `TaskExecutionRepairCoordinator`。白名单只包括：清理已终态 Goal/Task 遗留的 active Binding/Assignment/Reservation、回收过期 continuation lease、在 Task/Goal/Reservation/版本/无开放 Iteration 等 fence 全部成立时补建缺失 continuation intent。它不猜测 Task 成功、不续期已过期 Reservation、不合成 Turn。
- Tracker 增加 Reservation fencing token 对账；repair 每次写入前在 Serializable 事务内重读 canonical facts，避免拿五分钟前的投影直接写库。
- 聚焦源码验证串行通过：Runtime 36/36（Token/成本预算、当前 Turn compaction guard、工具发现、Turn 交接），Platform 31/31（Goal/WorkUnit 推进、Tracker/repair、计划编译与自动派发）。并行测试曾因共享产物和 SQLite 夹具产生非功能性争用，随后按仓库约定串行复核通过。

仍未完成的是 checkpoint 生成/恢复、AwaitHandle signal/consume、冲突范围的实际锁管理、Task-bound blocked 后重新预约、动态质量/成本/TPS 路由反馈、新构建进程外部署与七个夜间窗口验收。任务卡必须把“源码实现/验证”“产品部署”“真实任务 smoke”“七夜生产验收”分开登记。

### 1.9 2026-08-29 夜间低吞吐修复：bounded authoritative + 价格窗口 + WorkUnit 护栏

本批次针对“Shadow 只观察、无 Ready 时不刷新 Availability、任务型子代理可显式索取 600 轮”的组合故障完成以下闭环：

1. `TaskAutoDispatchWorker` 每次五分钟扫描先列举 Workspace Agent，并从 canonical Task/Goal/ChatCommand/SubAgent/Reservation 事实重建全部 Availability；即使 Ready 候选为 0，也会输出 `availabilityRefreshed/idle/busy/unknown`，不再让“无候选”掩盖陈旧空闲投影。
2. 产品源码配置开启 `GoalRuns + Continuation + TaskBoundGoals`，`TaskAutoDispatch.Mode=authoritative`；每个 Agent 每轮仍最多一个 Task，新增 `MaxStartsPerScan=2` 限制全局启动突发。最终启动继续走唯一 Serializable Task→Plan→Assignment→Reservation→Binding→Goal→Outbox 事务及 live foreground fence。
3. `PuddingLlmModelConfig` 新增版本化 `priceWindows/profileVersion/sourceUrl`。`ProviderModelExecutionWindowResolver` 使用 Agent 实际 provider/model route；`anytime` 直接允许，`inherit/off_peak_only` 只在版本化低价窗口内允许，缺 route/profile、过期或非法时 fail closed；支持时区、跨午夜、星期与生效/失效边界。
4. 当前价格档案必须通过 LLM Model API 写入 `D:\data\config\llm.providers.json` 并热重载，不新增 `work-policy.json`。BigModel Coding Plan 当前官方高峰为北京时间 14:00–18:00（<https://docs.bigmodel.cn/cn/coding-plan/overview>）；DeepSeek V4 当前高峰为北京时间 09:00–12:00、14:00–18:00，其余为低峰（<https://api-docs.deepseek.com/quick_start/pricing/>）。价格规则会变化，因此生产判断读取版本化配置，不在 Resolver 代码中硬编码供应商时段。
5. `workspace-task-agent`、带 TaskPlan/TaskNode 的 managed WorkUnit 无论调用方请求多少，均限制为最多 40 rounds、120 tool calls；普通非任务子代理仍可在系统显式授权下使用 600/2400 大任务护栏。该边界直接阻断“单一实施卡跑 109/600 轮”的 token 空转。
6. 源码聚焦验证为 Platform 32/32、Core LLM 配置 14/14；这只证明代码和契约，部署后仍需用任务看板选择有限任务，核对 `GoalRun/Reservation/ExecutionCommand/Run` 及 provider usage/cache 事件。

### 1.10 2026-08-29 首次 authoritative smoke 暴露的执行/结算缺口

首次真实自动派发已证明 Task→Plan→Assignment→Reservation→Binding→Goal→Outbox→ChatCommand→Run 能原子启动，
同时暴露出调度器之后的三个吞吐故障，必须作为同一执行架构修复，而不是只调整五分钟扫描：

1. BigModel 首轮在 38,994 prompt + 4,096 completion 后以 `finishReason=length` 截断，只有 16,841 字符 reasoning、
   没有工具调用和正文；旧 Runtime 把它转成“未返回可展示文本”的成功 Turn。新策略只允许一次不重放 reasoning 的
   短恢复轮，明确要求立即调用单一最佳工具；再次截断以 `llm_output_truncated` 失败收口，防止无动作推理循环。
2. 该 Turn 产生 728 条 thinking event。旧 `GoalSettlementStore` 取最早 128 条 evidence，导致真实
   `turn.completed` 被挤掉并错误进入 `evidence_incomplete`。完整性现对全 Turn 事件窗口判断，EvidenceRefs 取最新
   128 条；同一 settlement 事务回填 Run、耗时、LLM rounds、工具次数、输入/输出 tokens 到 Iteration 和 Goal 聚合。
3. Runtime 已直记 `session:trace:round` usage，但 ConversationProjector 的 SQLite `DateTimeOffset`/nullable 指纹
   查询无法翻译，catch 后又按 eventId 补记，造成一调用两行。修复后 SQL 只筛稳定 route/token 候选并限制最新
   32 行，时间窗在内存判断；历史账本保留不就地删除，正式统计按唯一网关事实或 canonical invocation 去重。

上述修复的源码门禁包括：输出截断一次恢复策略、150 条 thinking 后仍保留终态、Goal 指标聚合、SQLite usage
指纹可翻译。它们通过后仍需部署并用下一张自动任务确认：一次调用只记一条 direct usage、Goal 不再伪阻塞、Plan
从 Explore 推进到 Plan，才可把本批次记为产品 smoke 完成；七夜吞吐和缓存目标仍是独立生产验收门。

### 1.11 2026-08-29 第二次真实自动派发 smoke 与剩余瓶颈

- Desktop 点火链已完成 build → artifact prepare → Core restart → 新程序集加载，加载程序集 SHA-256 为
  `413db0ac7e5bf432af0c43bd7ced561dbb2351f31d0d752701ad8262d165c47a`；最终 Core PID 24160 进入 Ready。
- 启动扫描无需人工 RunNow 自动建立 Goal `tg-0d688d4937a48de7b947e4d5b293a904`。本次累计
  157,059 input / 3,291 output，较修复前 1,666,495 input 的 runaway 降低 90.58%；最后一个 provider call
  允许把 150k input 上限轻微越过 4.71%，随后父/子执行均立即停止。
- 五次真实调用整体缓存 88,832 / 157,059 = 56.56%，包含 GLM 与 DeepSeek 各自冷启动；只统计 warm 调用为
  88,832 / 91,285 = 97.31%。其中 GLM warm 95.85%，DeepSeek 两次 warm 合计 98.78%，仍未达到 >99% 门禁。
- Goal/Iteration 已正确聚合父执行及递归子会话 Token，并在 Task-bound 尝试失败后以 Goal Failed、Task Blocked、
  Binding/Reservation/Assignment 释放收口，不再留下可恢复 Blocked Goal 抢占下一次唯一键。
- 仍存在两个归因缺口：Goal `cost=0` 未聚合 gateway usage 成本，`total_tool_calls=2` 只反映根执行而未包含同步后代；
  两者不得用于当前吞吐 KPI。GLM 首轮 1,648 个 thinking frame、约 216 秒且无有效行动，是模型路由/推理预算瓶颈。
- Message Fabric 恢复器曾把已无待投递行的子代理目标永久保存在内存，每 10 秒产生一次 `no_claim` 探测；
  新源码在恢复扫描无可领取行时淘汰该 target，未来 durable row/event 会重新登记，回归测试覆盖不再重复探测。
- 最终程序集下另一个自动任务的 GLM 冷轮 39,466 prompt / 4,096 output、93.2 秒后 length 截断；唯一短恢复轮
  39,512 prompt 中命中 39,424（99.777%），16.6 秒并产出工具调用。稳定前缀与有界恢复在线生效，但冷轮
  无行动输出仍属于未解决的模型路由/goodput 损耗。

这次 smoke 证明了“有界自动启动、Token 预算向同步后代传播、Task-bound 失败释放”的在线链路，但并不等于
P0 调度器验收完成。当前仍以五分钟恢复扫描为权威节拍，尚缺 Task/Availability/窗口边界事件驱动 intent，
也尚未完成连续七夜 goodput、吞吐和缓存对照。

## 3. 现有代码基线与差距

| 现有位置/符号 | 已有能力 | 施工判断 |
|---|---|---|
| `Source/PuddingCore/Tasks/WorkspaceTaskModels.cs` | `TaskExecutionWindow` 及 Task 字段已存在 | 保留字段，不复制到新策略文件 |
| `Source/PuddingCore/Scheduling/IWorkAdmissionFence.cs` | Fence 输入已含 Task/priority/window | 扩展路由/价格档案快照和稳定 reason code |
| `Source/PuddingPlatform/Services/Tasks/TaskCommandService.cs` | 手工 Assign/RunNow 会创建 Assignment/Outbox | 手工闭环保留；Auto 不复用“先发消息”路径 |
| `Source/PuddingPlatform/Services/Tasks/TaskDispatcher.cs` | Hosted scan、lease、Fence stub、Message Fabric 派发 | 类职责限定为 Manual Delivery Dispatcher；新增 Auto Coordinator，不把两个状态机继续塞进同一类 |
| `Source/PuddingPlatform/Services/Tasks/ManualAlwaysAllowFence.cs` | 手工阶段恒 allow | 仅保留在明确 manual/test 组合；Auto 组合不得注册该实现 |
| `Source/PuddingCore/Abstractions/IAgentExecutionAvailabilityProvider.cs` | 查询空闲且带 cooldown | 保留兼容读 Adapter；Auto Claim 改读持久 Projection Store |
| `Source/PuddingRuntime/Services/Messaging/AgentExecutionStateRegistry.cs` | `ConcurrentDictionary` 运行时状态 | 只作 signal/cache；未知 Agent 不得默认 Idle |
| `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs` | 可额外 claim 9 条并合并正文 | 增加可靠 `ExecutionPurpose` 分流；Task-bound Goal 必须 one delivery/acceptance = one Task/Goal context |
| `Source/PuddingRuntime/Services/GoalMode/GoalModeService.cs` | `goal_queue.json` + 自然语言注入、成功发送后推进游标 | 不满足 ADR-074；待新 GoalRun 门禁通过后退役，不导入旧 JSON 为新权威 |
| `Source/PuddingRuntime/DependencyInjection.cs` | 旧 `IGoalModeService` 默认注册 | 继续默认关闭；切换时删除 Dispatcher 后置注入和旧 DI |
| `Source/PuddingPlatform/Services/ConversationAcceptanceStore.cs` | 可靠受理 Turn/Command/Event | 抽出可供 Goal Worker 调用的原子 synthetic acceptance primitive |
| `Source/PuddingPlatform/Services/AgentChat/ChatExecutionWorker.cs` | 执行命令 lease 和 Runtime 调用 | 继续作为唯一执行链，不增加 Task/Goal 内层循环 |
| `Source/PuddingRuntime/Services/TurnExecutorAdapter.cs` | Platform -> Runtime 执行交界 | 透传受信 Goal/Task context，不从 message metadata 自由字符串猜测 |

## 4. 目标组件与所有权

| 组件 | 所在项目 | 唯一职责 |
|---|---|---|
| `GoalCommandService` | PuddingPlatform | 处理用户 Goal command 和受信 `StartGoalFromTask` |
| `SqliteGoalStore` | PuddingPlatform | Goal/Iteration/Verification/Outbox 持久化、CAS、lease、恢复 |
| `TaskGoalDispatchTransactionStore` | PuddingPlatform | 在同一 `PlatformDbContext`/SQLite transaction 内跨 Task/Reservation/Binding/Goal/Outbox 提交 |
| `GoalContinuationWorker` | PuddingPlatform | claim `goal_outbox`，最终 admission，受理 synthetic Turn |
| `GoalSettlementWorker` | PuddingPlatform | 消费 Turn 终态事实，构造 verification job |
| `GoalVerificationExecutor` | PuddingRuntime | 只读证据验证，不写 Goal/Task |
| `GoalCoordinator` | PuddingPlatform | 用 deterministic gates + verdict 提交 Goal/Task 下一状态 |
| `AgentAvailabilityProjector` | PuddingPlatform | 从 canonical/runtime committed facts 生成持久 Agent 可用性投影 |
| `AgentAvailabilitySignalAdapter` | PuddingRuntime | 把实时 busy/idle 候选变化发给 Projector，不直接宣布可 Claim |
| `ProviderModelExecutionWindowResolver` | PuddingPlatform | 解析 Task 偏好 + Agent route + 价格时段 |
| `ExecutionWindowBoundaryScheduler` | PuddingPlatform | 为已计算 `nextEligibleAt` 的 Deferred Task 生成一次边界 signal |
| `TaskGoalDispatchCoordinator` | PuddingPlatform | 确定性选候选、三次 fence、调用原子启动事务 |
| `TaskExecutionTracker` | PuddingPlatform | 五分钟只读关联 Task→Execution 全链事实，分类健康/等待/卡住/不一致/待清理，不直接修复 |
| `TaskGoalProjector` | PuddingPlatform | 投影 Task/Goal 联合状态和可解释的等待原因 |

Runtime 不成为 Task/Goal 事务写入者，Desktop 不承载任务调度业务逻辑，Admin 不维护第二套状态机。

## 5. Core 合同

### 5.1 Goal 合同

新建 `Source/PuddingCore/Goals/`：

- `GoalContracts.cs`：`GoalRunId`、`GoalPhase`、`GoalSourceKind(UserCommand|WorkspaceTask)`、snapshot、command result。
- `GoalStateMachine.cs`：合法 phase/activation/iteration 转换和预算不变量。
- `GoalPersistenceContracts.cs`：`IGoalStore`、CAS/lease/outbox request/result。
- `GoalVerificationContracts.cs`：Evidence Capsule、verdict、gate result。
- `TaskBoundGoalContracts.cs`：`StartGoalFromTaskCommand`、`TaskGoalBindingSnapshot`、Task/Goal 终态映射。
- `GoalEventTypes.cs`：稳定 canonical event name 和 required payload fields。

`StartGoalFromTaskCommand` 必填：

```text
workspaceId
taskId / expectedTaskVersion
assignmentId
agentId / conversationId
reservationId / reservationFencingToken
executionWindow
providerId / modelId / windowKey / windowProfileVersion / validUntilUtc
goalIterationBudget
requestedAtUtc
causationId / correlationId / idempotencyKey
```

`idempotencyKey` 固定为版本化规则，例如 `task-goal:{workspaceId}:{taskId}:{assignmentId}`，不使用随机消息文本哈希。

### 5.2 Availability 合同

新建 `Source/PuddingCore/Scheduling/AgentAvailabilityModels.cs` 和 `IAgentAvailabilityProjectionStore.cs`：

```text
AgentAvailabilityState = Unknown | Offline | Busy | Reserved |
                         WaitingApproval | Cooling | Idle | Frozen

AgentAvailabilitySnapshot:
  workspaceId, agentId, state, version,
  sourceCursor, observedAtUtc, validUntilUtc,
  activeConversationId?, activeTurnId?, activeExecutionId?,
  reservationId?, cooldownUntilUtc?, reasonCode
```

Store 提供 `GetAsync`、`ApplyFactAsync(expectedVersion, fact)`、`RebuildAsync`、`WatchChangedAsync`。不提供“调用方直接 SetIdle”的绕过方法。

### 5.3 Execution Window 合同

新建：

- `Source/PuddingCore/Scheduling/ExecutionWindowModels.cs`
- `Source/PuddingCore/Scheduling/IExecutionWindowResolver.cs`

`ExecutionWindowDecision` 至少包含：

```text
verdict = Allow | Defer | Unknown
code
providerId / modelId
windowKey / profileVersion
evaluatedAtUtc / validUntilUtc / nextEligibleAtUtc?
isUserOverride
```

修改 `IWorkAdmissionFence.cs`：

- 输入增加 route/window snapshot 和 Availability version；
- 输出保留 `validUntil`，便于 reserve/dispatch/accept 三次重检；
- 新增稳定码 `deferred_execution_window_unknown`、`deferred_window_closed`、`deferred_availability_stale`、`denied_goal_prerequisite_disabled`；
- 显式 `off_peak_only` 不被优先级自动越过。

### 5.4 受信执行上下文

修改 `Source/PuddingCore/Tasks/ActiveTaskRuntimeContext.cs` 或新建组合值 `TaskGoalRuntimeContext`：

```text
taskId, taskVersion, assignmentId, reservationFencingToken,
goalRunId, goalRevision, activationEpoch, iterationNumber,
acceptanceCriteria, remainingBudget, executionPurpose=task_goal
```

该对象来自服务端受理命令和数据库绑定，不允许从 Message `Content` 或模型输出反序列化获得。

## 6. SQLite 模型与事务

### 6.1 新表

`goal_runs`：

```text
goal_id PK
workspace_id, conversation_id, agent_id
source_kind, source_task_id?, source_assignment_id?
objective, acceptance_criteria_json
phase, revision, activation_epoch, aggregate_version, boot_id
max_iterations, accepted_iterations
tool/token/cost/time budget snapshot
policy_snapshot_json
created_at_utc, activated_at_utc?, terminal_at_utc?, updated_at_utc
```

`goal_iterations`：

```text
iteration_id PK, goal_id FK
activation_epoch, iteration_number
status, command_id?, turn_id?, run_id?
accepted_at_utc?, settled_at_utc?
result_code?, evidence_capsule_hash?
UNIQUE(goal_id, activation_epoch, iteration_number)
```

`goal_verifications`：

```text
verification_id PK, goal_id FK, iteration_id FK
attempt, status, verdict?, reason?, evidence_refs_json
verifier_route_snapshot_json, input_hash, error_id?
created_at_utc, completed_at_utc?
UNIQUE(iteration_id, attempt)
```

`goal_outbox`：

```text
outbox_id PK, goal_id FK
kind, activation_epoch, iteration_number
status, idempotency_key UNIQUE
available_at_utc, lease_owner?, lease_until_utc?
attempt_count, last_error_code?
created_at_utc, updated_at_utc
```

`task_goal_bindings`：

```text
binding_id PK
workspace_id, task_id, task_version_at_bind
assignment_id, agent_id, goal_id UNIQUE
reservation_id, reservation_fencing_token
status = active | completed | blocked | cancelled | failed | superseded
created_at_utc, terminal_at_utc?, updated_at_utc
```

索引/Constraint：

- partial unique：同一 `task_id` 最多一个 `status=active` binding；
- partial unique：同一 `assignment_id` 最多一个 active binding；
- `goal_runs(source_kind=WorkspaceTask)` 要求 task/assignment 非空；
- `accepted_iterations <= max_iterations <= 256`；
- outbox 的 goal/epoch/iteration 与幂等键唯一。

ADR-072 已规划的 `agent_availability_projection` 与 `agent_execution_reservations` 补齐实现，不在 Goal 目录复制第二套表。

### 6.2 原子启动事务

`TaskGoalDispatchTransactionStore.StartAsync` 在单一 serializable SQLite transaction 内按顺序：

1. 读取并 CAS Task `Ready/Deferred -> Reserved/Assigned`，校验 `expectedTaskVersion`；
2. 校验 Assignment 当前且 Agent 匹配；
3. 以 Agent 版本和 fencing token 创建/确认 Reservation；
4. 校验 Window decision 尚在 `validUntilUtc` 内；
5. 插入 active `task_goal_bindings`；
6. 插入 Task-bound `goal_runs`；
7. 插入第一个 `goal_outbox`；
8. 追加 Task events 和 canonical Conversation events；
9. commit 后发 signal。

任意 unique/CAS 失败返回稳定的 `LostRace/TaskChanged/AgentChanged/WindowExpired`，不抛出无限重试。

### 6.3 终态事务

`GoalCoordinator.ApplyVerdictAsync` 对 Task-bound Goal 同时校验：

```text
goal.aggregateVersion
goal.activationEpoch
task.version
binding.status == active
reservation.fencingToken
iteration/verification identity
```

映射：

- `complete` + Task acceptance gates pass -> Goal Completed + Task Completed + binding completed + release reservation；
- `blocked/needs_user` -> Goal Blocked/Paused + Task Blocked/NeedsReview + release auto reservation；
- `budget_exhausted` -> Goal BudgetExhausted + Task NeedsReview；
- `cancelled` -> 依用户起因一致提交 Task/Goal cancellation；
- transient execution failure -> 保留 Goal/Task 证据，根据有界策略创建同 Goal 下一 outbox 或进入 NeedsReview；
- stale result -> 只追加审计，不改变当前状态。

## 7. 事件与触发

### 7.1 新增事件

```text
task.goal.bind_requested
task.goal.bound
task.goal.binding_terminal
task.auto_dispatch.deferred
task.auto_dispatch.suppressed

agent.availability.changed
agent.reservation.created
agent.reservation.released
agent.reservation.expired

execution_window.opened
execution_window.closed
execution_window.profile_changed

goal.created / activated / paused / resumed / cancelled / completed
goal.iteration.accepted / started / settled
goal.verification.requested / completed / failed
goal.continuation.requested / dispatched / suppressed
```

所有跨域事件至少含 `eventId/workspaceId/agentId/taskId?/assignmentId?/goalId?/causationId/correlationId/occurredAtUtc`。Availability 事件还必须含 source cursor/version，旧事件不得覆盖新投影。

### 7.2 触发源

`TaskGoalDispatchCoordinator` 响应：

- `task.ready`；
- `task.auto_dispatch.deferred` 的 `nextEligibleAtUtc` 到期；
- `agent.availability.changed` 进入 Idle；
- `execution_window.opened/profile_changed`；
- `agent.reservation.expired/released`；
- 用户 Turn settle 后产生的 availability 变更；
- commit signal 丢失时的低频恢复扫描。

恢复扫描有结构边界：只扫描已有 Ready/Deferred Task、pending GoalOutbox 和 expired lease 索引，限制 batch/page，不全表 COUNT，不遍历 Agent 目录。

## 8. 代码文件施工矩阵

### 8.1 PuddingCore

| 文件 | 操作 | 关键改动 |
|---|---|---|
| `Source/PuddingCore/Goals/GoalContracts.cs` | 新增 | Goal ID/phase/source/snapshot/command result |
| `Source/PuddingCore/Goals/GoalStateMachine.cs` | 新增 | phase/epoch/iteration/budget 纯状态机 |
| `Source/PuddingCore/Goals/GoalPersistenceContracts.cs` | 新增 | Store/CAS/outbox/lease 契约 |
| `Source/PuddingCore/Goals/GoalVerificationContracts.cs` | 新增 | Evidence Capsule 与 verifier verdict |
| `Source/PuddingCore/Goals/TaskBoundGoalContracts.cs` | 新增 | `StartGoalFromTask` 和 binding/result mapping |
| `Source/PuddingCore/Goals/GoalEventTypes.cs` | 新增 | 事件名和 payload 字段 |
| `Source/PuddingCore/Scheduling/AgentAvailabilityModels.cs` | 新增 | 持久 Availability 状态/快照/事实 |
| `Source/PuddingCore/Scheduling/IAgentAvailabilityProjectionStore.cs` | 新增 | query/apply/rebuild/watch |
| `Source/PuddingCore/Scheduling/ExecutionWindowModels.cs` | 新增 | 价格时段快照和 decision |
| `Source/PuddingCore/Scheduling/IExecutionWindowResolver.cs` | 新增 | 纯解析契约 |
| `Source/PuddingCore/Scheduling/IWorkAdmissionFence.cs` | 修改 | route/window/availability version 和 reason code |
| `Source/PuddingCore/Platform/ConversationEventContracts.cs` | 修改 | Goal/TaskGoal source/event payload |
| `Source/PuddingCore/Tools/ToolAuthorization.cs` | 修改 | `/goal` grammar；内部 Task command 不暴露为 slash command |
| `Source/PuddingCore/Configuration/PuddingConfigModels.cs` | 修改 | provider/model 可选价格时段字段 |

### 8.2 PuddingPlatform

| 文件 | 操作 | 关键改动 |
|---|---|---|
| `Data/Entities/GoalRunEntity.cs` 等 5 个 entity | 新增 | GoalRun/Iteration/Verification/Outbox/TaskGoalBinding 映射 |
| `Data/Entities/AgentAvailabilityProjectionEntity.cs` | 新增 | Availability 持久投影 |
| `Data/Entities/AgentExecutionReservationEntity.cs` | 新增 | Agent 唯一 auto reservation/fencing token |
| `Data/PlatformDbContext.cs` | 修改 | DbSet、partial unique index、check constraint |
| `Services/Goals/GoalSchemaBootstrapper.cs` | 新增 | schema/index/version 启动校验 |
| `Services/Goals/SqliteGoalStore.cs` | 新增 | Goal CAS/outbox lease/recovery |
| `Services/Goals/GoalCommandService.cs` | 新增 | 用户 Goal command |
| `Services/Goals/TaskBoundGoalCommandHandler.cs` | 新增 | 仅接受受信 `StartGoalFromTask` |
| `Services/Goals/TaskGoalDispatchTransactionStore.cs` | 新增 | 跨 Task/Reservation/Binding/Goal/Outbox 单事务 |
| `Services/Goals/GoalContinuationWorker.cs` | 新增 | durable claim -> synthetic acceptance |
| `Services/Goals/GoalSettlementWorker.cs` | 新增 | terminal event -> verification job |
| `Services/Goals/GoalCoordinator.cs` | 新增 | deterministic gates + verdict + dual CAS |
| `Services/Goals/GoalRestartReconciler.cs` | 新增 | boot fence，active -> paused/disarmed |
| `Services/Scheduling/AgentAvailabilityProjector.cs` | 新增 | committed facts -> persistent projection |
| `Services/Scheduling/AgentAvailabilityProjectionStore.cs` | 新增 | CAS/TTL/rebuild |
| `Services/Scheduling/AgentExecutionReservationStore.cs` | 新增 | TryReserve/Renew/Release/Expire |
| `Services/Scheduling/ProviderModelExecutionWindowResolver.cs` | 新增 | provider/model route + 价格时段 |
| `Services/Scheduling/ExecutionWindowBoundaryScheduler.cs` | 新增 | 有界 nextEligible boundary signal |
| `Services/Tasks/TaskGoalDispatchCoordinator.cs` | 新增 | candidate/fence/reserve/start goal |
| `Services/Tasks/TaskDispatcher.cs` | 修改 | 明确仅 manual delivery；不承担 Auto Goal |
| `Services/Tasks/TaskCommandService.cs` | 修改 | Auto 不生成普通 Task instruction outbox；手工语义保留 |
| `Services/Conversation/SystemCommandHandler.cs` | 修改 | 委托 `IGoalCommandService` |
| `Services/ConversationAcceptanceStore.cs` | 修改 | 可幂等 synthetic Goal acceptance primitive |
| `Controllers/Api/GoalCommandsController.cs` | 新增 | Goal 结构化命令 |
| `Controllers/Api/GoalQueriesController.cs` | 新增 | Goal/iteration/verification/binding 查询 |
| `Controllers/Api/ExecutionWindowDiagnosticsController.cs` | 新增 | 只读 evaluate，无第二份策略 PUT |
| `Services/LlmProviderFileService.cs` | 修改 | 价格时段 schema 验证、round-trip、原子写 |

### 8.3 PuddingRuntime

| 文件 | 操作 | 关键改动 |
|---|---|---|
| `Services/Goals/GoalContextContributor.cs` | 新增 | 受信 Goal/Task 动态 context layer |
| `Services/Goals/GoalVerificationExecutor.cs` | 新增 | 只读 verifier route 和 strict schema |
| `Services/Goals/GoalEvidenceSanitizer.cs` | 新增 | 有界 capsule、artifact refs、secret 防护 |
| `Services/Messaging/MessageDeliveryDispatcher.cs` | 修改 | `ExecutionPurpose=task_goal/goal` 时不 batch；删除 settle 后旧 GoalMode 注入 |
| `Services/Messaging/AgentExecutionStateRegistry.cs` | 修改 | 未知不默认 Idle；发布候选事实/signal |
| `Services/Messaging/DefaultAgentExecutionAvailabilityProvider.cs` | 修改 | 改为读持久 projection 的兼容 Adapter |
| `Services/TurnExecutorAdapter.cs` | 修改 | 透传 `TaskGoalRuntimeContext` |
| `Services/ContextPipeline.cs` | 修改 | 增动态 goal-state/task-state layer，不破坏稳定 prefix |
| `Services/GoalMode/*` | 退役 | 新 GoalRun 切换后删除旧队列服务/选项/模型 |
| `DependencyInjection.cs` | 修改 | 注册新 Runtime Goal 服务，移除旧 `IGoalModeService` |

### 8.4 PuddingHost 与 Admin

| 项目/文件 | 关键改动 |
|---|---|
| `Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 注册 Goal stores/workers、Availability projector、Window resolver、Coordinator；校验 flag 依赖 |
| `Tests/PuddingHost.Tests/Hosting/PuddingApplicationHostCompositionTests.cs` | ValidateOnBuild 覆盖 enabled/disabled/shadow 组合，确保 Hosted Service 不漏注册 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/GoalBanner.tsx` | 显示用户/Task-bound source、iteration、budget、pause/cancel |
| `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskDetailsDrawer.tsx` | 显示 Goal binding、等待窗口/Agent 原因、证据和深链 |
| `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/ExecutionWindowInspector.tsx` | 显示 Task 偏好、route/profile/version/nextEligible，只读评估 |
| `Source/PuddingPlatformAdmin/src/services/platform/api.ts` | Goal/binding/availability/window DTO 和 API |

PuddingDesktop 不增任务业务代码；它通过现有 WebView 消费 Admin/Core 投影。

## 9. 施工卡与依赖

### P0-1 Goal 合同与旧语义隔离

依赖：无。

- 增加 Core Goal/TaskBoundGoal 合同和纯状态机。
- 为 `/goal` parser 建立命令测试；`StartGoalFromTask` 不走用户 slash parser。
- 将旧 `GoalModeService` 标注为 LegacyInjection，继续默认关闭，不增字段或功能。

出口：状态机/256/epoch/CAS 属性测试通过；普通聊天不变。

### P0-2 Goal SQLite 事实与命令

依赖：P0-1。

- 建 Goal 4 表 + binding 表、constraint 和 schema bootstrapper。
- 实现 Goal Store、GoalCommandService、idempotency、snapshot/query/events。
- 实现 restart reconciler：新 boot 将 active Goal disarm/pause。

出口：create/status/pause/resume/cancel 可重启重放；重复 request 不重复建 Goal。

### P0-3 Durable continuation 与 synthetic acceptance

依赖：P0-2。

- 实现 outbox claim/lease/fencing/signal/recovery。
- 抽取 ConversationAcceptanceStore 原子受理 primitive。
- 端到端走 `ChatExecutionWorker -> TurnExecutorAdapter -> AgentExecutionService`。
- 用户消息优先，pause/cancel/stop 使旧 epoch 失效。

出口：`heartbeat=0` 连续 20 个 iteration；故障点不丢不重；无第 257 个 accepted iteration。

### P0-4 Verifier 与 Goal/Task 终态门禁

依赖：P0-3。

- 实现 settlement worker、evidence capsule、deterministic gates 和只读 verifier。
- Agent `DONE` 只建 proposal；Coordinator 是唯一 Goal 终态写入者。
- 先完成非 Task Goal 验收，再开放 Task-bound Goal。

出口：ADR-074 G0–G3 通过，`GoalRuns.Enabled` 可单独灰度。

### P1-1 Availability Sensor

依赖：Task/Conversation/Runtime canonical facts 可读；可与 P0-2–P0-4 代码并行，但不能先启用 Auto。

- 建 projection/reservation 表与 store。
- 从 active Turn/Tool/SubAgent、approval、user queue、cooldown、lifecycle 事实重建。
- 把 Runtime registry 改为低延迟事实发布器，不当权威。
- 先 shadow 比对 old/new availability，记录 false-idle/false-busy。

出口：重启后 unknown -> rebuild；所有负状态均不 Claim；直接用户消息可抢占 Reservation。

### P1-2 Execution Window Resolver

依赖：Core 配置合同。

- 扩展现有 provider/model 配置的可选价格时段。
- 实现 Windows/IANA timezone、DST/跨日/边界、effectiveAt/profileVersion。
- Agent route 未知、profile 缺失和无效配置 fail closed。
- 只读 Evaluate API 和 Admin Inspector。

出口：相同快照确定性一致；`off_peak_only` 精确返回 next eligible；没有 `work-policy.json`。

### P1-3 Task-bound Goal 原子启动

依赖：P0-4、P1-1、P1-2。

- 实现 binding entity/store、`TaskBoundGoalCommandHandler`、跨域事务。
- 实现 Task/Goal 双 CAS 终态映射。
- Runtime 透传 `TaskGoalRuntimeContext`。
- MessageDeliveryDispatcher 排除 task_goal/goal 批处理。

出口：手工调用 `StartGoalFromTask` 可完成一项 Task；所有崩溃点全成或全不成；无重复 binding/Goal。

### P1-4 Event-driven Dispatch Coordinator

依赖：P1-3。

- 确定性候选顺序：Task priority -> due/notBefore -> readyAt -> sortOrder -> taskId；Agent 按 preferred/capability/idleSince/agentId。
- reserve/dispatch/accept 三次 Fence，每次重校验 route/window/availability version。
- 注册事件 signal 与有界恢复扫描。
- 先 evaluate-only，再单 Workspace/单 Agent 启用。

出口：低价窗口打开后无需 Heartbeat 即开始；窗口关闭或用户消息到达后不误启动。

### P2 UI、观测与旧路径退役

依赖：P1-4 稳定。

- Goal Banner、Task binding、Availability、窗口原因和 deep link。
- 指标/日志/诊断页连接 Task -> Goal -> Iteration -> Turn -> Run -> Verification -> Usage。
- 停止旧 GoalMode 后置注入，删除 DI 和测试替换。
- 旧 `goal_queue.json` 不导入、不自动删除；若存在只记一次脱敏警告，由用户决定是否归档。

出口：新路径是唯一 Goal/Auto Task Owner，旧 JSON 不再被读写，回滚不会删除新事实。

## 10. 测试矩阵

### 10.1 PuddingCoreTests

新增：

- `Goals/GoalStateMachineTests.cs`：合法/非法 phase、epoch、255/256/257、resume 不重置。
- `Goals/TaskBoundGoalContractsTests.cs`：必填字段、幂等键、Task/Goal 映射。
- `Scheduling/ExecutionWindowResolverContractTests.cs`：unknown/fail-closed 和稳定 code。
- `SystemCommandParserTests.cs`：Goal grammar、越界、空 objective、引号/多行。

### 10.2 PuddingPlatformTests

新增：

- `Services/Goals/GoalSchemaBootstrapperTests.cs`：表、索引、constraint、重复启动。
- `Services/Goals/SqliteGoalStoreTests.cs`：CAS、lease、epoch、idempotency、recovery。
- `Services/Goals/GoalCommandServiceTests.cs`：多入口命令和 restart disarm。
- `Services/Goals/GoalContinuationWorkerTests.cs`：事务边界崩溃、用户抢占、stale suppression。
- `Services/Goals/GoalCoordinatorTests.cs`：DONE proposal、gates、verdict、Task/Goal 双 CAS。
- `Services/Scheduling/AgentAvailabilityProjectorTests.cs`：unknown 默认、rebuild、TTL、approval/user queue/tool/subagent。
- `Services/Scheduling/AgentExecutionReservationStoreTests.cs`：一 Agent 一 Reservation、renew/release/expiry/fencing。
- `Services/Scheduling/ProviderModelExecutionWindowResolverTests.cs`：时区、跨日、边界、route/profile 改变。
- `Services/Tasks/TaskGoalDispatchTransactionStoreTests.cs`：每个 SQL 故障点的全成/全不成。
- `Services/Tasks/TaskGoalDispatchCoordinatorTests.cs`：确定排序、竞争、defer、用户优先。
- 更新 `LlmProviderFileServiceTests.cs`：价格时段 round-trip，未知字段/无效时区 fail closed。

### 10.3 PuddingRuntimeTests

- `Services/GoalContextContributorTests.cs`：受信 context、稳定 prefix 边界、不从文本猜 ID。
- `Services/GoalVerificationExecutorTests.cs`：strict schema、无工具、timeout/retry/fail-closed。
- 更新 `MessageDeliveryDispatcherTests.cs`：Task-bound Goal 不 batch；普通消息批处理不回归。
- 更新 `AgentExecutionStateRegistryTests.cs`：unknown 不默认 Idle，事实顺序不回退。
- 更新 `TurnExecutorAdapterTests.cs`：Task/Assignment/Goal/epoch/fencing 完整透传。
- 新增 `Services/TaskE2E/TaskBoundGoalE2ETests.cs`：Task -> Goal -> 20 Iteration -> verifier -> Task terminal。
- 删除/替换 `GoalModeServiceTests.cs`：切换后不再以 JSON 注入作为 Goal 验收。

### 10.4 Host/Admin/E2E

- Host composition：三个 flag 组合、每个 Hosted Worker 唯一、缺依赖 fail fast。
- Admin：Goal Banner、Task binding、waiting reason、window profile、SSE replay、pause/cancel。
- 故障注入：reserve 后、binding 后、outbox commit 前后、accept 前后、verdict 前后崩溃。
- 时间边界：窗口开启前 1ms/开启时/关闭前 1ms/关闭时，以及 route/profile 临界变更。
- 用户竞态：Ready + Idle 后用户消息抵达，Auto 必须释放或 defer，不抢占 foreground。

## 11. 可观测性

日志共同字段：

```text
workspaceId, agentId, taskId, taskVersion, assignmentId,
reservationId, reservationFencingToken, availabilityVersion,
providerId, modelId, windowKey, windowProfileVersion,
goalId, goalRevision, activationEpoch, iterationNumber,
commandId, turnId, runId, verificationId,
decisionCode, nextEligibleAtUtc, correlationId, causationId, errorId
```

指标：

```text
agent_availability_total{state}
agent_availability_transition_total{from,to,reason}
agent_idle_without_runnable_task_seconds
agent_reservation_total{result}

task_auto_dispatch_total{result,reason}
task_auto_dispatch_queue_age_seconds
task_goal_binding_active_total
task_goal_start_latency_seconds
task_low_price_window_missed_total{reason}

execution_window_decision_total{verdict,reason,provider,model}
execution_window_open_total{provider,model,window_key}

goal_outbox_pending
goal_continuation_lag_seconds
goal_iteration_total{result,source_kind}
goal_terminal_total{phase,source_kind}
```

`task_low_price_window_missed_total` 只在一个已配置的低价窗口结束时，仍有本可运行 Task 却因非窗口原因未启动时增加；标签使用有界 reason code，不把 taskId 放入指标高基数标签。

## 12. Feature Flag、切换和回滚

```text
GoalRuns.Enabled
TaskBoundGoals.Enabled
TaskAutoDispatch.Enabled
AgentAvailabilityProjection.Mode = disabled | shadow | authoritative
ExecutionWindowFence.Mode = disabled | shadow | enforce
```

启用序：

1. Schema + read-only query；
2. GoalRuns 内部用户灰度，Auto 关闭；
3. Availability shadow + Window shadow；
4. Availability authoritative，Auto 仍关闭；
5. TaskBoundGoals 手工单 Task smoke；
6. TaskAutoDispatch 单 Workspace/单 Agent；
7. 扩大 Agent/工作区，观测至少一个完整低价窗口；
8. 退役 Legacy GoalMode。

回滚：

- 先关 `TaskAutoDispatch.Enabled`，只停止新 binding；
- 再将 TaskBoundGoals 改为 read/pause/cancel only，不删除事实；
- 已 accepted Turn 允许 settle，旧 epoch 不再 continuation；
- 释放未 dispatch Reservation，保留 Task/Goal/Verification/Usage 审计；
- 不把新 GoalRun 倒灌回 `goal_queue.json`，不启用双 Owner。

## 13. 构建、部署与验收命令

每张施工卡先运行最小定向测试，分项目串行扩大。建议命令：

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --nologo
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --nologo
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore --nologo
dotnet test Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo
git diff --check
```

若实际 `.csproj` 名称不同，以 `rg --files -g '*.csproj'` 结果为准，不把输出写入 `D:\data`。

源码修改后的产品验收分两段：

1. 内部开发 Agent 完成定向测试并交付 `ready-for-external-deploy`；
2. 进程外控制器部署并启动明确新构建，校验新 PID、产物时间/哈希、Ready/Health 与旧失败窗口后存活；
3. 在新 Pudding 产品会话中做真实 Task-bound Goal 功能 smoke，再交付 `in-product-functional-complete`；
4. Desktop/Core 重启、崩溃恢复和退出回收由进程外控制器最终判定。

## 14. 生产验收门禁

### A0 合同

- Goal/Task/Availability/Window 枚举和错误码在 Core/OpenAPI/Admin 一致。
- 256 外层 Iteration 与内层 Agent Loop Round 不共用字段。
- 没有 `work-policy.json` 或第二个前端状态机。

### A1 Goal 前置

- ADR-074 G0–G3 全部通过；Heartbeat 全关时 Goal 仍续行。
- DONE 不直接完成；verifier 失败 fail closed。
- 重启默认 disarm，用户 resume 才重新授权。

### A2 Availability

- 重启后从 unknown 重建，stale/unknown 不 Claim。
- active Turn/Tool/SubAgent、approval、user queue、reservation、cooldown 任一存在均不 Idle。
- 并发 Coordinator 对同一 Agent 只有一个 Reservation 成功。

### A3 Execution Window

- `off_peak_only` 在路由/价格档案未知时不执行。
- reserve/dispatch/accept 跨过窗口边界时重新判定。
- 用户 Run Now override 有权限、原因、事件和单次范围。

### A4 Task-bound Goal

- Assignment/Reservation/Binding/Goal/Outbox 事务故障注入全成或全不成。
- 一 Task 一 active Goal，一 Agent 一 auto Reservation。
- Task-bound Goal 不 batch，Runtime context 的 task/assignment/goal/epoch/fence 全部一致。
- Goal 终态与 Task 终态双 CAS，Delivery ACK 不误完成。
- Task-bound Goal 进入 Blocked/NeedsUser/Unsafe 时，同一结算事务必须释放 Binding、Assignment 与
  Reservation；Task 保留 Blocked/NeedsReview，本次 Goal 以 Failed 终态保留审计证据。历史
  `Blocked + active binding` 由五分钟 tracker/repair 自愈；历史 `Blocked + terminal binding` 在该 Agent
  下次 Task 派发事务内转为 `superseded_by_task_retry` Failed，然后才允许新 Goal 创建，不能长期占用 Agent 或
  伪装成 `task_goal_lost_race`。
- legacy 手工派发若 Delivery 已确认但超过 stall threshold 仍无 execution/session/claim 事实，必须 fail closed
  为 Blocked 并释放 Assignment；Delivery 已 dead-letter/failed/cancelled 则立即收口。扫描内 repair 必须先于
  Availability 重建和候选派发，不能浪费下一周期。
- `task_dispatch_outbox` 在发送前重验 Task/Assignment owner；失去所有权或确定性终态冲突必须 dead-letter，
  原子 Binding 前再次重验以关闭发送期间的并发失效窗口；其他发送/持久化错误统一受 `MaxAttempts` 限制，
  Core 停机取消保留 lease 等待恢复，禁止每五分钟永久重放已 stale 的派发。
- WorkUnit 的 input/output/cost budget 必须沿 ToolInvocation → 单个/批量 SubAgent → RuntimeDispatch 传播；
  同步 child 累计 usage 返回父级并由 Buffered/Streaming 共同计账。Goal 结算采用“主 Turn canonical usage +
  当前 Turn 时间窗内递归子会话 TokenUsageEvents”，不得遗漏委派，也不得重复加入 root ledger。

### A5 低峰实效

- 在至少一个完整真实低价窗口中，Ready + Idle 任务无需 Heartbeat 自动启动。
- 窗口内没有可运行 Task 时 Agent 保持空闲是正常结果，不空转发提醒。
- 窗口结束后能解释未派发的确切原因，并能从 Task 追溯到 Goal/Turn/Run/Usage。

## 15. 完成定义

只有同时满足以下条件，才能宣布“Agent 感知与低峰自动派发已实现”：

1. GoalRun 是 SQLite/canonical event 权威，旧 JSON GoalMode 不再运行。
2. Availability 是持久可重建投影，进程内 registry 不能误决定 Claim。
3. Task 偏好与 provider/model 价格时段没有重复配置权威。
4. Auto Dispatcher 的唯一启动产物是 Task-bound Goal，不是普通提醒消息。
5. Task/Goal 的启动、续行、终态、取消、抢占和恢复都有故障注入证据。
6. 真实低价窗口 smoke 证明任务会及时启动，且窗口外不误启动。
7. 新构建的进程外部署、Ready/Health、新 PID/产物和崩溃恢复已验收。
8. ADR 状态、完整设计、本施工计划、Docs 索引和 `code_map.md` 与实际实现状态一致。
