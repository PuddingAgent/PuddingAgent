---
title: 案例：调度与 Token 内核
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: active
description: 调度、Goal、Runtime 测试共同引用 Platform/Runtime 产物并会创建 SQLite 夹具。不要并行运行 PuddingRuntimeTests 与 PuddingPlatformTests：并行构建会争用 PuddingPlatform.dll，并可能把临时 SQLite I/O 或外键错误伪装成功能回归。应先完成一次构建，再串行运行聚焦集和全量集；测试夹具必须创建真实 S
categories: [docs, how-debug]
tags: [案例, 调度与, 内核, how_debuge]
related_docs: [Docs/08_how_debuge/README.md]
related_files: [Source/PuddingHost/PuddingHost.csproj, TestScripts/test-pudding-deployment-gates.ps1, TestScripts/invoke-pudding-desktop-deployment.ps1, TestScripts/measure-pudding-process-baseline.ps1, TestScripts/dev_up_tests.py]
slug: how-debuge-14-案例-调度与token内核
draft: false
---

# 案例：调度与 Token 内核

> 本文档是 [How-Debuge 调试与诊断手册](README.md)（主索引）的主题分册，由原根目录 `How-Debuge.md` 于 2026-10-02 按主题拆分而来。
> 新增本主题的经验请直接追加到本文件；跨主题内容请回到主索引选择分册。

## 11.43 调度/Token 执行内核的串行验证与产品态边界

调度、Goal、Runtime 测试共同引用 Platform/Runtime 产物并会创建 SQLite 夹具。不要并行运行
`PuddingRuntimeTests` 与 `PuddingPlatformTests`：并行构建会争用 `PuddingPlatform.dll`，并可能把临时 SQLite I/O
或外键错误伪装成功能回归。应先完成一次构建，再串行运行聚焦集和全量集；测试夹具必须创建真实
`SessionEntity`，`CompactedBy` 也必须指向存在的摘要消息，不能通过关闭外键绕过生产 schema。

仓库根目录当前没有名为 `PuddingRuntime` 的 solution/project；若
`dotnet build PuddingRuntime --no-restore` 返回 `MSB1009`，使用真实入口
`dotnet build Source/PuddingRuntime/PuddingRuntime.csproj --no-restore --nologo`，并额外构建
`Source/PuddingHost/PuddingHost.csproj` 验证 Platform DI 组合。构建成功仍只证明源码，不证明当前
Desktop/Core 已加载。重启前先检查任务看板的 InProgress/Assigned、active Assignment、待认领 Delivery
和当前 Run；存在活跃工作时不得为了 smoke 强制重启，应报告 `ready-for-external-deploy` 并等待可中断窗口。

## 11.44 五分钟调度器“在运行”但吞吐量仍为零

不要只看 HostedService 存活或 `TaskAutoDispatch.Enabled=true`。按同一轮日志和 SQLite 事实逐层判断：

1. 日志必须出现 `availabilityRefreshed/idle/busy/unknown`。若 backlog>0 但刷新数为 0，先查 WorkspaceIds 与 Agent catalog；若 unknown/busy，沿 `reasonCode` 查 ChatCommand、active Assignment/Binding、运行中 SubAgent 和 Reservation。
2. `backlog/refinementReady/promoted` 分开解释。`refinementReady` 只是判定；authoritative 下只有 `promoted>0` 才证明 Backlog→Ready CAS 成功。
3. `candidates/eligible/started` 分开解释。eligible 后仍可能在最终价格窗口、Availability version、前台 ChatCommand、route/plan fingerprint 或 Serializable transaction fence 被拒绝；查相邻的 `final window fence refused` / `atomic start refused` 稳定码。
4. `inherit/off_peak_only` 必须在 `llm.providers.json` 的实际 Agent provider/model 上存在非空 `priceWindows` 与 `priceWindowProfileVersion`；不要只给审计 Agent 的 DeepSeek 模型配窗口，而 Service Agent 实际走 BigModel。
5. 启动证据不是 Delivery ACK。至少联查 `workspace_tasks.active_assignment_id`、active Reservation、`task_goal_bindings`、`goal_runs`、`goal_outbox`、`chat_execution_commands` 与真实 Run。
6. 子运行若出现 100+ rounds，检查 run 创建事件的 `template/max_rounds/max_tool_calls`。`workspace-task-agent` 或带 TaskPlan/TaskNode 的 managed WorkUnit 应被归一到 `<=40/<=120`；仍看到 600/2400 说明运行的还是旧程序集或该运行不是 managed WorkUnit。

## 11.45 `refinementReady>0` 但全部 `backlog_route_changed`

先比较 `TaskBacklogRefinementEvaluator` 和 `TaskBacklogRefinementStore` 两次调用
`TaskAgentRouteMatcher.Fingerprint` 的输入。指纹只能包含会影响路由的稳定事实，例如 Agent ID、
provider/model、role、capabilities、enabled/frozen 与 Task 的结构化路由约束。`WorkspaceAgentDto.CreatedAt/UpdatedAt`
是 API 投影元数据；当前文件目录读取若每次投影为 `UtcNow`，把它纳入指纹会让“评估通过→事务重算”必然漂移，
表现为同一轮所有 ready candidate 都被 `backlog_route_changed` 拒绝。回归测试必须构造仅投影时间不同、
路由事实完全相同的两个 DTO，并断言 SHA-256 相同；真正修改 provider/model/capability 时仍必须变化。

若已经出现 `eligible>0` 但事务仍返回 `agent_changed`，检查候选评估是否为同一 Agent 的每个 Task 都调用
`RebuildAsync`。Availability version 是事务 fencing token；同一轮必须每个 Agent 只重建一次并让所有候选
共享该快照。逐任务重建会把版本从 N 推到 N+k，使第一个 eligible 携带的旧版本在真正启动前必然失效。

运行中心日志也是安全边界。临时 JWT/ECDSA 密钥未配置时，只能记录“使用进程临时密钥”和公钥指纹，
不得输出私钥、公钥原文、ControlToken、API Key 或 bearer token。若历史环形缓冲区已经包含密钥材料，
部署修复后还要重启 Desktop/Core 清空进程内缓冲，并按相应凭据生命周期评估轮换；生成诊断包前再次检查脱敏。

## 11.46 自动 Goal 已启动但两分钟后 `evidence_incomplete`，同时 Token 账本翻倍

先按同一 `goal_run_id/turn_id/command_id/run_id` 联查 `goal_iterations`、`conversation_events`、
`execution_runs` 和 `TokenUsageEvents`，不要把“Command succeeded”直接当成 WorkUnit 成功：

1. 若 Turn 内有 128 条以上 `message.thinking_summary.appended`，但 `turn.completed` 明明存在，检查
   `GoalSettlementStore` 是否对事件先按升序 `Take(128)`。Evidence Capsule 可以有界，但完整性必须在全终态窗口
   `Any(turn.completed/failed/cancelled)` 判定，引用列表取最新 128 条并保留终态；否则思考流会把终态挤出窗口。
2. `goal_iterations.llm_rounds/tool_calls/input_tokens/output_tokens/run_id/started_at_utc` 和 Goal 聚合指标必须在
   settlement 事务中从 canonical usage/tool events 与 ExecutionRun 回填。字段一直为 0 说明任务跟踪只推进状态、
   没有完成资源核算，吞吐/Token 诊断会失真。
3. 日志出现 `LLM response truncated finishReason=length ... contentChars=0` 时，不得把
   `（Agent 未返回可展示文本）` 投影为成功。Runtime 只允许一次短恢复轮，要求模型立即调用一个最合适工具或给出
   简短终答，不把已流出的长 reasoning 重放进 history；再次截断必须以 `llm_output_truncated` 失败收口。
4. 同一调用若 `TokenUsageEvents` 同时有 `session:trace:round` 和 eventId 两行，查
   `ConversationProjector Usage fingerprint check failed`。SQLite EF 不能翻译 `DateTimeOffset` 范围比较；先用
   session/route/source-prefix/token 取最近 32 个候选，再在内存应用 ±2 分钟窗口。不能 catch 后直接 fallback，
   否则每次调用都会双记账并夸大吞吐与 token/s。
5. 历史重复行不在诊断时直接删除；报表应优先使用唯一网关账本或按 canonical invocation 去重，清理生产数据另走
   显式备份/重建流程。源码测试和构建通过后仍需部署新 Core，再观察下一自动任务是否出现一次 direct usage、
   WorkUnit 是否推进，以及 Goal 指标是否非零。

## 11.47 Task 已 Blocked，但五分钟调度器长期 `idle=0 / tracked=1 / stalled=1`

这是执行权没有收口，不是“Agent 真的还在运行”。按同一 `task_id/goal_run_id/assignment_id/reservation_id`
检查四个围栏：

1. `goal_runs.status=Blocked` 且已经没有 active Turn/Command/Run 时，`task_goal_bindings.status` 不得仍为
   `active`，`workspace_tasks.active_assignment_id` 也必须为空。普通 Goal 的 Blocked 可显式恢复；Task-bound
   尝试一旦释放 Binding/Reservation/Assignment，应终结为 Failed 并保留证据，由 Task Resume/Requeue 建立
   新的 fenced Goal，不能留下既不持有租约、又占据 active-Goal 唯一索引的悬空 Blocked Goal。
2. 新结算路径必须在同一事务把 Binding 置为 `terminal`、释放 Reservation、把 Assignment 置为 Failed 并
   清除 Task 的 active assignment；Task 本身仍保持 Blocked/NeedsReview，不能为了释放 Agent 假装 Completed。
3. 历史脏状态由 `TaskExecutionTracker` 输出 `CleanupRequired/blocked_binding_still_active`，repair coordinator
   Serializable 重读 Goal/Binding/Assignment/Reservation 后执行同样清理。不能让它只输出 `Stalled/goal_blocked`
   却没有对应 repair，否则每五分钟只会重复观测同一个死锁。
4. 修复后的下一轮 Availability 应从 `active_task_owned` 变为 Idle；随后才可能出现 `eligible/started>0`。
   如果仍 busy，继续检查同 Agent 的其他 active Assignment、前台 ChatCommand 或 SubAgent，不能手工篡改投影。
5. Task 的 `resume`/重新排队会产生新的 fenced Assignment + Task-bound Goal；旧 Goal 与证据保持不变但阶段应为
   Failed/其他终态，不复用已经释放的 Reservation 或 Binding。
6. 如果 `active_task_owned` 对应的 Task 没有 TaskGoalBinding，检查 legacy
   `task_execution_bindings + message_deliveries`：Delivery 已 `delivered/ack`，但长于
   `TrackerStallThreshold` 仍没有 executionId/sessionId/claimedByExecutionId，是“投递成功、执行未开始”。
   tracker 应输出 `legacy_assignment_execution_missing`，repair 把 Task 标为 Blocked、记录
   `assignment_execution_missing` 并释放 Assignment；不得把 Delivery ACK 当成任务完成。
   Delivery 已进入 `dead_letter/failed/cancelled` 且没有 execution claim 时不再等待阈值，使用
   `legacy_delivery_terminal_without_execution` 立即收口。
7. 单轮顺序必须是 `track → repair → rebuild availability → evaluate → dispatch`。若先刷新/选候选再 repair，
   即使修复成功也会浪费当前五分钟扫描，低价窗口边界附近会直接损失一个调度周期。

## 11.48 旧 task dispatch outbox 每五分钟重复发送并报 stale assignment

`task_dispatch_outbox` 是至少一次投递，不等于允许永久重放已经失去 Assignment 所有权的消息。若每轮扫描
都出现相同 outbox 的 `stale assignment`，同时 Message Fabric 已按幂等键拒绝重复消息，按以下顺序检查：

1. 发送前必须重读 Task；Task 不再是 `Reserved`、`active_assignment_id` 已变化或为空时，直接把 outbox
   标记为 dead-letter，原因保存稳定 `stale_assignment`，不得先发送再依赖下游去重。
2. `TaskStateConflict`、`AssignmentStale`、`TaskNotFound` 是确定性终态冲突，立即 dead-letter；数据库/绑定等
   非确定性错误也必须消耗 `MaxAttempts`，达到上限后收口，不能无限重试。
3. 发送前重验与最终绑定之间仍有并发窗口；`CompleteDispatchAsync` 必须再次校验 Assignment owner。若消息已经
   通过稳定 MessageId 幂等落库、随后终态校验失败，只 dead-letter 原 outbox，不创建 Binding、不重发旧 owner。
4. Core 停机取消不是业务失败：保留当前 lease 并抛出取消，由过期租约恢复后按同一 MessageId 重放；不要在
   `OperationCanceledException` 分支直接 failed/dead-letter，避免把正常重启记录成派发故障。
5. `ConversationProjectionWorker` 不应在活跃流的每一个 raw source event 上紧循环。积压小于一个投影批次时
   先做短 coalescing；`ConversationProjector` 只有实际 materialize transcript 时记 Information，纯 checkpoint/
   catalog 推进记 Debug。否则会用日志与 SQLite 写放大抵消低价窗口吞吐。

## 11.49 子代理耗尽百万 Token，但 WorkUnit/Goal 只显示主代理用量

这是预算所有权和结算归因同时断裂，不能只降低 `maxRounds`：

1. 父执行在每次工具调用前把“剩余” input/output/cost budget 传给 ToolInvocation；同步子代理继续传入
   `RuntimeDispatchRequest.UsageBudget`。批量委派必须按任务数拆分预算，不能把完整父预算复制给每个 child。
2. child 的 `RuntimeDispatchResult.Usage` 必须是该执行（含同步后代）的累计快照；工具结果将
   `DelegatedUsage` 返回父级，Buffered/Streaming 都立即记入父账本。达到边界后以 `BudgetExhausted` 收口，
   不得继续下一工具或下一 LLM round。provider 调用本身不可中断，因此允许最后一次调用轻微越界。
3. Goal settlement 的主会话用量继续来自当前 Turn 的 canonical `usage.recorded`；递归子会话用量来自
   `TokenUsageEvents`，只加 `SessionSubAgents` 后代且限定在该 Turn 的 created/completed 时间窗。不要再加
   root session 账本，否则会把主调用和 canonical usage 重复计算。
4. 验收同时比较 WorkUnit 上限、GoalIteration/Goal 聚合和 `llm_gateway_usage_events`。三者不一致时，先确认
   运行的是新程序集，再按 root session、递归 sub-session、Turn 时间窗逐行对账。

## 11.50 Task Resume 后每五分钟都是 `task_goal_lost_race`

不要先清 Reservation 或反复点 Resume。先联查 Task 的历史 `task_goal_bindings` 与 `goal_runs`：

1. 若 Binding 已 `terminal`、Assignment/Reservation 已释放，但对应 Goal 仍是 `Blocked(3)`，同时新派发没有留下
   任何 Assignment/Plan/Goal 部分写入，真实冲突通常是 `UX_goal_runs_active`，不是 Agent 抢占。该索引把
   Active/Paused/Blocked 都视为同一 `(conversation, agent)` 的非终态所有者。
2. 新结算路径中，普通 Goal 的 Blocked 仍可恢复；Task-bound 尝试因阻塞而释放执行权时，本次 Goal 必须转
   Failed 并写 `goal.failed`，Task 继续保持 Blocked/NeedsReview。Task Resume 后创建全新的 budget/fence/Goal。
3. 对旧版本留下的 detached Blocked Task Goal，`TaskGoalDispatchTransactionStore` 只在“同 Workspace、同 Agent、
   同会话、terminal Task binding”全部匹配时，于新启动事务内先写 `superseded_by_task_retry` 的 Failed 终态和
   canonical event，再创建新 Goal。旧 Goal 可以属于该 Agent 的前一个 Task，因为 terminal binding 已证明它不再
   持有执行权；普通 Blocked Goal和 active binding 不得被自动退役。
4. `DbUpdateException` 日志必须至少包含 SQLite code、extended code 和 message。稳定并发唯一键冲突可以返回
   `task_goal_lost_race`，但不能再把所有约束错误只记 Debug，导致状态机缺陷被伪装成竞争。
5. 回归测试必须真实启用 SQLite partial unique index，先写前一 Task 的 terminal binding + Blocked Goal，再为同一
   Agent/会话启动另一个 Task，
   断言旧 Goal Failed、新 Goal Active、`goal.failed` 仅一条且事务无部分写入。

## 11.51 已结束子代理仍每 10 秒出现一次 `no_claim`

如果日志中的 target 是已终态子代理会话、每次 `execution_id` 都不同，而 `message_deliveries` 已没有该 target 的
queued/retrying 行，这不是模型重试，而是 Message Fabric 的恢复目标缓存没有淘汰：

1. 联查 `message_deliveries`，先确认不是 lease、busy 或未来 `available_at`；没有任何行时才判定 stale target。
2. `DiscoverPendingTargetsAsync` 每轮仍以 durable inbox 为权威；`TryDispatchKnownTargetsAsync` 对恢复扫描的
   `ClaimNextAsync=null` 必须删除对应 workspace/room/agent/handling-mode 缓存键。
3. 新消息的 durable row 或 `message.deliver` 事件会再次 `RememberTarget`，因此淘汰无行 target 不等于丢消息；
   不得通过永久保留 target 来替代持久队列发现。
4. 回归测试应先返回一个 pending target 但 claim=null，下一轮 pending 为空，断言 claim 次数不再增长。

## 11.52 Codex 如何让 Desktop 加载新 Core/前端制品并取得诊断

不要让自动化脚本另起一个使用相同 `D:\data` 的 Core，也不要直接覆盖运行中 Desktop/Core 的程序集。
Desktop 默认在 `127.0.0.1:8199` 提供回环控制面；除低敏 `/desktop/bootstrap/status` 外均使用
`X-Control-Token`，值来自 `D:\data\config\system.json → desktop.core.controlToken`，不得打印到终端、日志或诊断证据。

- 已编译 Core：调用 `POST /desktop/bootstrap/core/deploy-restart`，body 提供仓库内绝对
  `artifactDirectory`，可附 `artifactAssemblySha256`。只有清单部署、重启后加载哈希和 Ready 全部通过才返回 success。
- 仅重启 Core：调用 `POST /desktop/bootstrap/core/restart`；它不会加载源码改动，不能当成部署验收。
- 前端源码构建并部署：调用 `POST /desktop/bootstrap/frontend/build-deploy`。
- 已编译前端：优先调用 `POST /desktop/bootstrap/frontend/load`，body 提供仓库内绝对 dist 路径，
  可附 `artifactIndexSha256`。它不运行 pnpm，部署后重新校验入口哈希；浏览器仍需 reload 才加载新 hashed bundle。
- 诊断：调用鉴权的 `GET /desktop/bootstrap/diagnostics`，检查 Desktop/Core state、PID、busy、实际路径、
  最近 100 行日志和 lastDeploymentResult；并发请求得到 409，不要做无界重试。

运行中的 Desktop 锁住默认 `Source\PuddingDesktop\bin\Debug` 时，源码验证使用定向 `OutDir` 放到仓库
`.tmp-test-out`；Desktop 自身更新仍必须由进程外控制器停止旧 Desktop、用隔离发布目录构建并启动新 Desktop。

### 11.52.1 停机备份与历史 PID 的判定（2026-09-05）

`diagnostics.coreProcessId` 来自 `runtime.Session?.ProcessId ?? runtime.LastProcessId`。因此 `Stopped` 后仍有历史 PID 是正常现象，不能用 `!coreProcessId` 判断停机；必须同时核对 `coreState` 为 `Stopped/Idle`、对应 OS 进程已退出、没有活跃执行/预约。`TestScripts/test-pudding-deployment-gates.ps1` 的 7 项纯函数回归覆盖这一点，不执行维护动作。

本机第三轮控制器 `TestScripts/invoke-pudding-desktop-deployment.ps1` 提供 `StopAndBackup`、`Deploy -BackupManifestPath <实际回执路径>`、`Verify`。它仅针对当前仓库 Debug Core 路径和 `D:\data`，其他机器先修改并审核路径门禁。备份位于 `D:\Keys\PuddingDeploymentBackups`，含数据库/WAL、配置和旧 Core；目录 ACL 限当前用户与 SYSTEM，不包含工作区归档。部署前仍要协调停机窗口，不得与 UI 启动并发。失败恢复应明确记录是谁调用 start，避免把脚本自身恢复误判为自动重启。

采样入口 `TestScripts/measure-pudding-process-baseline.ps1` 验证 Desktop→Core 父子 PID 后记录 120 秒 CPU/Private/WS；CPU 除以逻辑 CPU 数，口径为整机百分比。重启前后 uptime、Debug/Release 和界面负载不同会混杂结果，不能直接宣称优化收益。进程私有内存不等于 GC heap，也不等于截图中的工作集。

诊断只输出字段白名单。不要输出整个 `system.json`、`desktop.core` 或登录响应，它们可能带控制令牌/JWT。凭据仅在本地内存中用于请求 Header，不能进入报告或仓库。

资源样本先与执行账本做时间重叠核对：`execution_runs.started_at/completed_at` 与 `chat_execution_commands` 是 epoch 毫秒，不是 ISO 日期文本；误用 julianday 会返回空集并把活跃窗口误标 idle。`llm_gateway_usage_events.occurred_at_utc` 则是 UTC 文本，两种口径不得混用。第三轮部署前 idle 标签因此已在报告中更正。

大历史会话诊断优先按 `turn_id` 走 `ix_ce_turn`，或取有界尾部；只按 command_id，甚至 conversation_id 再过滤 command_id，可能扫描数十万事件。第三轮取消了这种慢查询，改用精确 Turn 与有界 tail。当前 `ConversationEventStore` 将 occurred_at/committed_at 都写为提交时刻；仅比较这两个字段不能测真实接收至落库延迟，事件长空档需跨 provider receive、消费、持久化阶段核对。

## 11.53 最近日志同时出现 MCP 连接拒绝、HTTP TaskCanceledException 与登录 Warning

先按启动周期聚类，不要把三种不同语义合并成“后端不稳定”：

1. 若每次 dev-up 启动都先出现 `Client (PuddingAgent ...) client initialization error`，目标为
   `127.0.0.1:5100`，随后 Codex MCP 又能正常 initialize/tools-list，根因通常是启动器只创建了 MCP
   子进程、却没有等待监听端口就启动 Backend。`start_codex_service` 必须等待精确子进程接受 TCP；子进程
   提前退出或 15 秒未就绪应 fail-fast，并给出 stdout/stderr 路径。
2. `/agents/status`、conversation/process-items 等读接口在页面刷新、导航或关闭后抛出
   `TaskCanceledException` 时，先核对 `HttpContext.RequestAborted`。只有 request token 已取消的
   `OperationCanceledException` 才是客户端断开：记 Debug、返回 499（若响应尚未开始），不生成 errorId/500。
   后端自己超时但 request token 未取消仍走 Traceable 500，不能被宽泛吞掉。
3. 成功登录若被 `[Auth:Login] Received ... PasswordLen=...` 和 `User found=...` 记为 Warning，是日志分级和
   信息暴露缺陷。认证日志不得记录密码形状、查询命中或账户启用状态；成功/失败只保留最小 Information
   事件，失败原因对外保持不可枚举。
4. 验收时分别运行 `TestScripts/dev_up_tests.py` 和 `TraceableExceptionMiddlewareTests`，再检查新启动周期：
   Backend 首次 MCP reconciliation 前 5100 已监听、客户端取消不再进入 error 文件、正常登录不再产生 Warning。

## 11.54 调度器显示“运行中”但 UI 无法管理、候选始终为 0

先把“后台服务存活”“任务进入候选”“执行真正开始”拆开验证：

1. 调用 `GET /api/workspaces/{workspaceId}/task-scheduling/auto-dispatch/status`，确认 effective state、policy revision、前置门禁和 lastScan；不要只读发布包 `appsettings.json`，用户策略位于 `<DataRoot>/config/system.json` 的 `taskAutoDispatch`；
2. 查 `workspace_tasks.status, auto_dispatch_enabled`。Backlog/Ready 全为 false 时，`candidates=0` 是显式 opt-in 结果，不是 Worker 死亡；通过任务编辑或卡片菜单“纳入自动调度”；
3. 调度中心 Pause 只禁止新的自动启动，已运行 Goal 必须在 Chat Header 单独暂停或停止。若 UI 已暂停但仍有 intent 被消费，检查 Bridge/Coordinator/Starter 是否仍注入 `IOptions<T>` 快照而不是 `IOptionsMonitor<T>`；
4. “立即扫描”与周期 scan 必须都进入 `TaskAutoDispatchScanRunner`，日志顺序应为 `track → repair → availability → refinement → evaluate → start`；“立即修复”不绕过 Task 状态机；
5. `assignment_execution_missing` 不是模型失败：联查 Delivery ACK、execution claim、Assignment 和 active_task_owned。Repair 后 Task 保持 Blocked，由 Resume/Requeue 生成新 fence；
6. 修改前端后浏览器能看到新按钮并不证明 Core API 已加载，反之旧页面无按钮也不能否定源码。分别记录 Admin build、Host build、产品资源哈希与 Core Ready 时间，最后再做真实点击 smoke。

## 11.55 Goal 自动续行消息显示大量 `\uXXXX`

先在浏览器选中异常消息并读取 DOM `innerText`：如果 DOM 本身仍包含 `\u7EDF\u4E00`，问题在消息内容或投影，
不是字体、Markdown 或 CSS。沿同一个 Turn 检查 `metadata.goal_managed=true`、
`metadata.automation_origin=goal_continuation` 和 `<goal_payload>`：

1. 新消息由 `GoalContinuationWorker.BuildPrompt` 生成。JSON encoder 应保留可读 Unicode，但继续转义 `<`、`>`、
   `&` 等 HTML 敏感字符，防止 objective 伪造 `</goal_payload>` 边界；不要改用不受约束的 relaxed encoder。
2. 历史消息已持久化为带 `\uXXXX` 的合法 JSON，前端只能在上述两个服务端 metadata 同时命中时提取
   `<goal_payload>` 并使用 `JSON.parse` 还原，再投影为简短的 Goal/Task/WorkUnit 文本。
3. 不要对所有用户消息做正则 Unicode 替换：普通用户可能真的输入 `\uXXXX` 或同名 XML tag。
   metadata 不匹配或 payload 损坏时必须保留原文，作为协议诊断证据。
4. `types.ts` 与实验 `projections/messageProjection.ts` 两条投影路径必须共用同一 formatter 和测试，
   否则 feature flag 切换后问题会复现。
5. 源码测试和 Admin build 通过只证明新制品可生成；部署前当前浏览器仍显示旧 bundle。加载新前端制品并 reload 后，
   再用同一历史消息验证页面中不出现 `\u[0-9A-Fa-f]{4}`，同时普通用户的同形文本保持不变。

## 11.56 External Agent 消息返回 202，但迟迟没有回复

不要把 `202 Accepted` 或 Message Delivery 的 `delivered/accepted` 当成 Agent 已执行完成。沿同一个 External
`messageId` 分层定位：

1. 先调用 `GET /api/external/v1/token`，核对 Token 未撤销/到期，含 `messages.send` 且 workspace claim 精确命中；
   External API 默认关闭，非 Loopback 明文 HTTP 会在 Controller 前被拒绝。
2. 查 `room_messages`：应存在 `from_kind=connector`、`from_id=access-token:{tokenId}`、目标 workspace 的消息；
   没有这一行时检查 Idempotency-Key、workspace/Agent 是否启用，以及 `external_api_idempotency` 是 conflict、in-progress
   还是已完成 replay。
3. 查同 messageId 的 `message_deliveries`。queued/delivering/retrying 表示基础设施仍在处理；delivered 只表示
   `MessageDeliveryDispatcher` 已交给 canonical Conversation acceptance，不代表 LLM 回复完成。
4. 用 `chat_execution_commands.metadata_json` 中精确的 ingress messageId 找 canonical command，同时核对
   workspaceId 和 AgentInstanceId；不要按“最后一条 command”猜。没有 command 时继续查 acceptance/dispatcher 日志和
   Delivery 的 handling mode/lease。
   若 Delivery 已是 `delivered`、Agent 消息日志甚至已有正确回复，但 command 仍不存在，说明消息走了旧的 direct Runtime
   路径。外部 Connector 请求必须由服务端写入 `canonical_turn=true`，并在 Dispatcher 中转交
   `AcceptMessageFabricConversationTurnAsync`；不能让 receipt 用“最后一条 Agent 回复”补偿这个缺失事实。
5. command 有 `terminal_sequence` 后，再按 `session_id + terminal_sequence` 查 `conversation_events`，并使用
   `ConversationTerminalMessageFormatter` 的结果解释 reply/error；不要直接从内部 payload 或前端最后一条消息拼回复。
6. Receipt 查询只允许当前 Token actor 自己发送的消息。另一个 Token 即使拥有相同 workspace/scope 得到 404 是预期
   隔离，不是数据丢失；日志中不得打印 Token Secret 或完整 Authorization Header。
7. Host build/定向测试只证明源码可生成。产品结论必须在加载新 Core 后重建最小 scope Token，真实 POST 一次并轮询
   Location，观察 delivery 与 execution 两套状态独立推进；共享 dirty worktree 时不要直接部署覆盖当前运行实例。

Admin “访问令牌”页若把 Active/Revoked 显示成数字 `0/1`，同时 Active 行缺少“撤销”按钮，先检查
`GET /api/admin/access-tokens` 列表是否直接序列化了持久化 enum。列表与详情必须统一投影为稳定字符串 wire 名称；只修
详情 DTO 或添加 enum converter 不足以修复列表。创建的 smoke Token 在测试结束后必须通过管理 API/UI 撤销，不能因为
页面按钮消失而直接留在 Active 状态。

