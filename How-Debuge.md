# PuddingAgent 调试与诊断指南

> 本文记录可重复使用的诊断路径、关键日志和验收方法。目标是找到故障发生在哪个架构边界，而不是根据前端现象直接打补丁。

## 1. 基本原则

1. 先确认环境状态，再分析业务代码。
2. 先定位失败阶段，再定位具体函数。
3. 使用 `traceId`、`conversationId`、`turnId`、`commandId`、`runId`、`messageId` 串联证据，不能只按时间猜测。
4. 前端显示“发送中”不等于 LLM 正在运行，它可能停在命令受理、Worker 领取、LLM、终态提交、投影或 SSE 任一阶段。
5. HTTP 202 只代表命令已持久化受理，不代表 Agent 已完成。
6. 不用异常作为正常控制流，不吞掉初始化错误，不允许服务在不完整 Schema 上报告健康。
7. 修复后必须同时验证实时 SSE、持久化投影和延迟历史回填。

## 2. 先确定运行目录

不要在诊断脚本中硬编码盘符。运行时数据根目录按以下优先级确定：

1. 启动参数指定的数据根目录；
2. `PUDDING_DATA_ROOT`；
3. 程序输出目录下的 `data`。

PowerShell 中可先建立诊断变量：

```powershell
$repo = (Get-Location).Path
$dataRoot = if ($env:PUDDING_DATA_ROOT) {
    $env:PUDDING_DATA_ROOT
} else {
    Join-Path $repo "data"
}

$devLogs = Join-Path $repo "tmp\dev"
$runtimeLogs = Join-Path $dataRoot "logs"
```

最终路径来源以 `PuddingDataPaths` 和当前启动参数为准。

## 3. 日志位置

### 3.1 dev-up 进程日志

| 文件 | 用途 |
|---|---|
| `data/logs/dev-up-YYYY-MM-DD.log` | 启动器生命周期、子进程退出与重启熔断 |
| `tmp/dev/backend.out.log` | 后端控制台日志，排查启动与聊天链路的首选 |
| `tmp/dev/backend.err.log` | 后端进程级错误和启动失败 |
| `tmp/dev/frontend.out.log` | 前端编译与开发服务器输出 |
| `tmp/dev/frontend.err.log` | 前端编译、模块加载错误 |
| `tmp/dev/proxy.out.log` | 反向代理请求与上游状态 |
| `tmp/dev/proxy.err.log` | 端口占用、代理连接失败 |

若终端连续出现 `frontend exited ... restarting`，先查看
`tmp/dev/frontend.err.log` 的第一条编译错误。前端在 30 秒内连续退出 3 次后，
`dev-up.py` 会停止整组进程并打印错误日志路径，避免确定性编译错误形成无限重启循环。

#### 清理仓库开发日志和临时输出

先停止全部受管进程，再执行：

```powershell
python .\dev-up.py --down
python .\dev-up.py --clear
```

PowerShell 包装命令等价为 `.\dev-up.ps1 -Clear`。`--clear` 只删除仓库内明确
归 dev-up/测试构建所有的 `tmp/`、`.tmp/`、`.tmp-build/`、`.tmp-test-out/`、
`.codex-out/` 和 `data/logs/`。任一受管进程仍在运行时命令会拒绝执行。

该命令不清理 `D:\data`、项目 `bin/obj`、`publish`、前端 `dist/node_modules`、
源码或 `data/agents`。需要保留诊断证据时，先归档对应日志再执行清理。

### 3.2 应用结构化日志

应用日志位于 `<dataRoot>/logs`：

| 目录 | 内容 |
|---|---|
| `system/pudding*.log` | 全量系统日志 |
| `error/pudding-error*.log` | Error 及以上日志 |
| `components/agent_execution` | Agent 执行循环 |
| `components/context_pipeline` | 上下文组装、裁剪、压缩 |
| `components/llm_gateway` | LLM 请求、流式响应与耗时 |
| `components/tool_runner` | 工具调用 |
| `components/memory` | 记忆读取、召回和写入 |
| `components/session_state` | 会话状态 |
| `components/event_queue` | 事件队列 |
| `components/event_dispatcher` | 事件分发 |
| `components/sub_agent` | 子 Agent |
| `diagnostics` | Timeline、会话诊断证据 |
| `sessions` | 会话级日志 |

临时提高诊断粒度：

```powershell
$env:PUDDING_LOG_LEVEL = "Debug"
python .\dev-up.py --restart
```

问题结束后恢复 `Information`，避免 Debug 日志长期放大磁盘占用。

## 4. 五分钟快速分诊

### 第一步：确认环境

```powershell
python .\dev-up.py --status
Invoke-WebRequest http://localhost/health -UseBasicParsing
```

如果健康检查不是 200，先检查启动、编译、DI 和端口，不要先调试聊天 Controller：

```powershell
Get-Content .\tmp\dev\backend.err.log -Tail 200
Get-Content .\tmp\dev\backend.out.log -Tail 400
Get-Content .\tmp\dev\frontend.err.log -Tail 200
Get-Content .\tmp\dev\proxy.err.log -Tail 100
```

### 第二步：查找 Error

```powershell
Get-ChildItem $runtimeLogs -Recurse -File |
    Select-String -Pattern "\[ERR\]|UnhandledException|SQLite Error|ObjectDisposedException" |
    Select-Object -Last 100
```

如果 API 返回了 `errorId`：

```powershell
$errorId = "<API 返回的 errorId>"
Get-ChildItem $runtimeLogs -Recurse -File |
    Select-String -SimpleMatch $errorId
```

`TraceableExceptionMiddleware` 会记录：

```text
[UnhandledException] errorId=... traceId=... sessionId=... path=...
```

因此 `errorId` 是 HTTP 500 的首要检索键，找到它后继续按 `traceId` 聚合同一次请求的证据。

### 第三步：截取当前运行周期

旧日志中的错误不能证明当前版本仍然失败。应从最后一次启动标记开始分析：

```powershell
$lines = Get-Content .\tmp\dev\backend.out.log
$start = ($lines |
    Select-String -Pattern '^\[Startup\] Ensuring Memory DB tables\.\.\.$' |
    Select-Object -Last 1).LineNumber

$currentRun = $lines[($start - 1)..($lines.Count - 1)]
$currentRun |
    Select-String -Pattern "\[ERR\]|SQLite Error|ObjectDisposedException|ConversationProjector|ChatWorker|Coordinator"
```

## 5. Conversation 命令链路

当前主链路：

```text
POST /api/v1/conversations/{conversationId}/turns
    ↓
SubmitTurnHandler / ConversationAcceptanceStore
    ↓ 原子持久化
User Message + Turn + Command + turn.accepted
    ↓
ChatExecutionWorker 领取 Lease
    ↓
ExecutionRunCoordinator
    ↓
AgentExecutionSnapshotFactory
    ↓
ITurnExecutor / AgentExecutionService
    ↓
IExecutionJournal.CommitTerminalAsync
    ↓
Conversation Event Store
    ↓
ConversationProjectionWorker / ConversationProjector
    ↓
SSE replay/live + 历史消息 API
    ↓
前端单调状态合并
```

### 5.1 每一阶段应看到的证据

| 阶段 | 关键证据 | 缺失意味着 |
|---|---|---|
| HTTP 受理 | POST 返回 202；响应包含稳定 ID | 路由、认证、请求契约或受理事务失败 |
| 事件写入 | `[ConversationEventStore] Appended ...` | 命令没有进入持久事实层 |
| Worker 领取 | `[LeaseStore] Acquired cmd=... turn=... runId=... fence=...` | Worker 未运行、Command 不可领取或 Lease CAS 失败 |
| 执行开始 | `turn.started`，Coordinator 开始运行 | 快照组装或执行前置条件失败 |
| LLM/工具 | `llm_gateway`、`tool_runner`、`runtime_activity` | Provider、网络、上下文或工具阶段阻塞 |
| 终态提交 | `turn.completed`、`turn.failed` 或 `turn.cancelled` | 执行结果没有原子提交 |
| 投影 | `[ConversationProjector] Projected conv=... checkpoint=A->B` | Event Store 与读模型之间存在积压或投影失败 |
| SSE/历史 | 相同稳定 `messageId/turnId/commandId` | 前后端身份或游标不一致 |

`ConversationProjector` 的 `events=0` 不一定是错误。部分事件只推进 checkpoint，不产生聊天消息投影。真正的异常是 checkpoint 长时间落后、重复失败或终态事件存在但消息读模型始终缺失。

### 5.2 一次请求必须保持的身份

- `conversationId`：浏览器观察、POST 命令、Event Store 和投影必须使用同一个值。
- `clientRequestId`：命令幂等键。
- `clientMessageId`：客户端用户消息身份。
- `turnId`：一次用户回合。
- `commandId`：可领取、可恢复的执行命令。
- `runId + fenceToken`：当前执行尝试及其写入权限。
- `assistantMessageId`：助手消息从开始、流式片段、终态到历史投影保持不变。

不要在 Controller、Worker 或投影器中重新生成这些 ID。

## 6. 常见症状

### 6.1 页面 502 或登录失败

先看后端是否真正启动。常见根因包括：

- 编译错误；
- DI 生命周期错误；
- 后端端口被旧进程占用；
- 后端启动失败后代理仍在运行。

```powershell
python .\dev-up.py --status
Get-Content .\tmp\dev\backend.err.log -Tail 200
Get-Content .\tmp\dev\proxy.err.log -Tail 100
```

不要因为页面显示 502 就先修改认证或聊天接口。

### 6.2 POST 返回 500

1. 从响应取得 `errorId`。
2. 在 `error/pudding-error*.log` 和 `backend.out.log` 中检索。
3. 找到对应 `traceId`。
4. 判断异常发生在请求绑定、受理事务、配置解析、数据库还是执行层。

前端只显示 `Request failed with status code 500` 时，浏览器错误文本不是根因，服务端 `errorId` 才是诊断入口。

### 6.3 一直“发送中”或 Agent 没有回复

按顺序检查：

1. POST 是否返回 202；
2. 用户消息和 `turn.accepted` 是否持久化；
3. Worker 是否取得 Lease；
4. 是否出现 `turn.started`；
5. LLM 调用是否开始、是否超时；
6. 是否提交 `turn.completed/failed/cancelled`；
7. 投影 checkpoint 是否前进；
8. SSE 是否收到终态，前端是否按终态清理 pending。

如果跳过中间阶段，容易把投影故障误判为 LLM 无响应，或把 LLM 超时误判为 SSE 断线。

### 6.4 回复出现后又消失

这是实时状态被旧历史快照覆盖的典型表现。检查：

- 流式事件和历史 API 是否返回同一个 `assistantMessageId`；
- 历史投影是否已经包含终态回复；
- 前端是否在 `completed turn` 尚未 materialize 时拒绝过旧历史；
- SSE checkpoint 与历史 projection checkpoint 是否一致；
- 浏览器重连后是否从正确的 `Last-Event-ID` 继续。

验收不能只看回复“出现过”，必须等待一次延迟历史拉取后再次确认。

### 6.5 `Agent LLM config is null`

命令载荷不应携带 `llmConfig`。正确边界：

```text
Command 只保存身份与用户意图
    ↓
AgentExecutionSnapshotFactory
    ↓
AgentRuntimeProfileResolver
    ↓
Agent 模板/实例配置 + LLM Provider Service
    ↓
不可变执行快照
```

检查：

- Agent 实例和模板身份是否正确；
- `PreferredProviderId/PreferredModelId` 是否由配置服务解析；
- LLM Provider 配置是否由统一 Service 和 `PathHelper/PuddingDataPaths` 加载；
- 是否有组件绕过 Resolver 直接读取 JSON、数据库或硬编码路径；
- Worker 是否错误地信任了客户端传入的模型参数。

### 6.6 `SQLite Error 1: no such table`

首先确认多个 `DbContext` 是否共享同一个 SQLite 文件。EF Core `EnsureCreated` 只适用于判断空数据库，不能保证共享数据库中另一个模型的表已完整创建。

Memory 数据库的正确初始化顺序：

```text
MemoryDbInitializer
    ↓ 显式执行 Schema/init_memory.sql
MemoryLibraryDbInitializer
    ↓ 显式创建图书馆 Schema
应用启动完成
```

约束：

- 不允许通过多个 `EnsureCreated` 猜测 Schema 是否完整；
- Schema 文件缺失或 DDL 失败必须阻止启动；
- `CREATE TABLE/INDEX IF NOT EXISTS` 承担幂等；
- 增加列前先查询 `PRAGMA table_info`，不要用预期异常实现幂等；
- 修复测试必须覆盖“数据库已被另一个 DbContext 创建”的场景。

Platform 的 `TokenUsageEvents` 增加字段时，不要只修改 Entity 和
`OnModelCreating`：`Database.EnsureCreatedAsync()` 不会升级已经存在的表。当前由
`TokenUsageSchemaBootstrapper` 在启动阶段完成 `ParentSessionId` 列与索引的幂等升级。

同样地，`chat_execution_commands` 是 Turn 受理事务的事实表。新增命令字段时必须同步维护
`ConversationCommandSchemaBootstrapper`。如果 `POST /api/v1/conversations/{id}/turns`
返回 500，且 `backend.out.log` 出现
`table chat_execution_commands has no column named metadata_json`，说明 Entity 已写入新字段、
但已有 Platform SQLite 尚未升级。先确认启动日志包含
`[ConversationCommandSchema] Added chat_execution_commands.metadata_json`，再用
`PRAGMA table_info("chat_execution_commands")` 验证；不要把该错误归因到 LLM、SSE 或 Worker。
诊断顺序：

1. 先检查 `backend.out.log` 是否存在编译错误；例如 `CS0103 platformDb` 是启动代码的
   变量作用域错误，DDL 尚未执行，不能归类为数据库迁移失败。
2. 从当前启动周期查找
   `[Startup] Platform DB tables and token usage schema ensured`。
3. 必要时用 `PRAGMA table_info("TokenUsageEvents")` 验证列，并从
   `sqlite_master` 验证 `IX_TokenUsageEvents_ParentSessionId`。
4. Schema Bootstrapper 不允许吞掉 DDL 异常继续启动；否则 EF 模型已访问新字段时，
   请求阶段才会以 `no such column` 失败。

针对性测试：

```powershell
dotnet test .\Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj `
    --no-restore `
    --filter "FullyQualifiedName~MemoryDatabaseInitializationTests"
```

### 6.7 回复完成但历史消息缺失

检查 Event Store 与读模型：

1. Conversation head 是否大于 projection checkpoint；
2. `ConversationProjectionWorker` 是否启动；
3. Projector 是否持续扫描所有落后 Conversation；
4. 终态事件是否携带稳定的 `assistantMessageId`；
5. `ChatTranscriptWriter` 是否按稳定 ID 幂等写入；
6. 重启后积压事件是否会自动补投影。

投影调度必须由持久化 head/checkpoint 驱动，不能只依赖事件写入线程中的 fire-and-forget 调用。

### 6.8 Smart 工具一直等待或子代理永久 Running

先取得 `runId`，不要只用 `subSessionId` 猜测执行状态。标准链路：

```text
SmartWorkflowToolBase(originToolId/model)
  -> SubAgentTool
  -> SubAgentInvocationService(invocationId/batchId)
  -> SubAgentManager(runId + deadline)
  -> AgentExecutionService(round/llm/tool)
  -> runs/{runId}/events.jsonl
  -> conversation-projection.cursor
  -> Conversation Event Store
  -> Session SSE
  -> subAgentReducer
```

如果终态是 `budget_exhausted`、`Maximum agent rounds reached` 或工具调用上限，按事实链检查预算，不要从父
Agent 的自然语言推断：

1. 查看 `<DataRoot>/config/runtime.execution.json` 的 `subAgents.maxRounds`、
   `maxToolCallsTotal`、`maxTimeoutSeconds`、`budgetGraceRounds`、
   `budgetGraceTimeoutSeconds`；它们是普通 `spawn_sub_agent` 的唯一预算来源。
2. 查看 `runs/{runId}/input.json` 的 `limits` 与 `run.json`，确认固化值；父工具参数不应再出现
   `max_rounds`、`max_tool_calls_total` 或 `timeout_seconds`。
3. 查看 `events.jsonl` 的 `subagent.run.started`，核对
   `max_rounds/budget_grace_rounds/max_tool_calls/max_elapsed_seconds/budget_grace_timeout_seconds`
   与系统配置一致；再检查 `subagent.budget.notice` 是否依次出现 `start`、`remaining_80`、
   `remaining_50`、`grace_started`，最后按终态的实际计数判断撞到哪条护栏。
4. 大型任务基线是 600 轮、2400 次工具调用、24 小时；正常轮次或时间预算用尽后默认还有
   20 个收尾轮次，且硬时限内预留最多 30 分钟；若父 deadline 压缩了运行窗口，收尾时间最多
   占有效硬时限的 25%。`budget_exhausted` 应保留 `output.md`，父 Agent 可用
   `resume_sub_agent_id=<subSessionId>` 续跑；新 runId 的计数应从零重新开始。
5. 若仍提前结束，继续区分父 Turn deadline、
   一小时无进展看门狗、Provider 首包/流空闲超时和用户取消。

检查运行归档：

```powershell
Get-ChildItem D:\data\workspaces -Recurse -Directory -Filter "run_*"
Get-Content <run-dir>\run.json
Get-Content <run-dir>\events.jsonl -Tail 50
Get-Content <run-dir>\conversation-projection.cursor
Get-Content <run-dir>\errors.jsonl -ErrorAction SilentlyContinue
```

诊断顺序：

1. `run.json` 是否包含 `originToolId / role / providerId / modelId /
   timeoutSeconds / maxRounds`；
2. `events.jsonl` 是否有 `run.created` 和 `run.started`；
3. 是否停在 `context_assembled`、`llm.started` 或 `tool.started`；
4. started 是否有对应 completed/failed；
5. `conversation-projection.cursor` 是否等于 `events.jsonl` 行数；
6. Conversation Event Store 是否出现同一 `runId`；
7. SSE sequence 是否送达浏览器；
8. 前端 `subAgentReducer` 是否按 `runId` 归并。

判断：

- `events.jsonl` 不增长：Runtime 内部卡住或未携带 `RuntimeExecutionIdentity`；
- 文件增长而 cursor 不前进：Conversation 投影失败，检查
  `SubAgentConversationProjectionWorker` 日志；
- cursor 前进而浏览器不可见：检查 Session SSE 的 Last-Event-ID 和 gap recovery；
- UI 有 `llm.started` 无 completed/failed：Provider 调用或取消传播卡住；
- UI 有 `tool.started` 无 completed/failed：工具执行、审批或终端进程卡住；
- `run.json=running` 但已有终态事件：进程曾在事件和 manifest 更新之间退出，
  再次终态提交应使用稳定 eventId 幂等修复；
- 超时显示 failed 而非 timed_out：检查 Manager 是否传递
  `ExecutionDeadlineUtc`，Runtime 不得从错误文本猜测超时。

Smart 工作流是同步工具调用，父 Agent 会等待子 run 返回。诊断“模型交互卡死”时还要区分：

- 同一 `runId` 长时间停在 `llm.started/tool.started`：检查 Provider、工具或取消传播；
- 连续出现多个不同 `runId`，且每个都正常 completed：父模型在重复调用 Smart 工具，
  不是单个工具死锁；应检查 Prompt 是否要求自包含结果，以及系统子代理预算；
- 标记为 `ReadOnly` 的 Smart 工具仍拿到 `file_write/shell/spawn_sub_agent`：描述符与
  capability 白名单不一致，会放大耗时和副作用，必须改为显式只读白名单。
- 后续原子 Smart 调用的 prompt token 持续增长：检查请求是否意外携带
  `pool_name` 或 `reuse_parent_context=true`。标准 `smart_*` 调用应是一次性
  `subSessionId` 且 `reuse_parent_context=false`；只有直接 `spawn_sub_agent`
  的显式请求可以选择池化或父上下文。
- 一次 Smart 失败后连续出现不同 Provider/Model 的 run：检查调用是否显式传入
  `allow_fallback=true`。默认调用不得静默切换模型；没有该参数仍发生切换就是执行契约回归。

若 run archive 和右上角运行坞都在更新，但主消息气泡仍停在“深入分析中”，检查前端
`MessageStream` 的 memo 比较。正文和 assistant status 在同步 Smart 等待期间通常保持不变，
但 `timelineItems` 会从 thinking 更新为 `tool_call(smart_*)`；memo 必须比较 timeline
内容和 process summary。刷新后等待秒数明显归零，则检查 `AgentMessageBubble` 是否仍从
组件 mount 时刻计时；正确锚点是该 Turn 的服务端 `createdAt`。

`SubAgentIndicator` 不允许按前端经过时间猜测终态。对于仍为 Running 的卡片，可以低频查询
`GET /api/sessions/{sessionId}/sub-agents`，只用持久化终态校正事件快照；活动明细仍以
Conversation Event 为准。若发生校正，仍要继续定位事件产生、归档、投影或 reducer 中的断点。

### 6.9 池化子代理 Create 成功、Execute 报 `saving entity changes`

典型错误：

```text
SubAgentPool.CreateAsync -> 成功
SubAgentPool.ExecuteAsync(ReuseSubSessionId=...)
  -> SessionStateManager.TrackSubAgentStartAsync
  -> UNIQUE constraint failed: session_sub_agents.sub_session_id
```

先区分两个身份：

- `SubSessionId`：池化会话身份，可跨多次任务复用；`session_sub_agents` 只允许一条
  当前状态行。
- `RunId`：单次执行身份；每次 Execute 必须新建，用于 run archive 和审计。

正确链路：

```text
pool create
  -> SubAgentSessionId.Create
  -> Idle（不创建 run、不调用 SpawnAsync）

pool execute
  -> new RunId
  -> TrackSubAgentStartAsync
     -> INSERT ... ON CONFLICT(sub_session_id) DO UPDATE
     -> 同 parentSessionId 重置为 running，并清空旧终态
  -> Runtime dispatch
```

诊断顺序：

1. 过滤日志 `[SubAgentPool] Reserved`、`[SubAgentMgr] Execute sync`、
   `[SSM] Sub-agent current state set to running`，确认一次 execute 只有一个 Runtime
   派发。
2. 查询 `session_sub_agents`，同一 `sub_session_id` 必须恰好一行；复用启动后应为
   `running`，`completed_at/Success/reply_summary/error_summary/full_result_json`
   必须清空。
3. 查询 `sub_agent_runs` 或 run archive：同一 `sub_session_id` 可以有多个不同
   `run_id`，每次任务一个。
4. 若 create 后已经出现 run archive 或 LLM 调用，说明 Pool 又把“预留身份”实现成了
   `SpawnAsync`，会导致首轮双执行。
5. 若同一 `sub_session_id` 的 `parent_session_id` 改变，必须拒绝执行；不能允许跨父
   会话抢占。

禁止以下修复：

- 捕获所有 `SaveChangesAsync` 异常并继续执行：会掩盖 Schema、磁盘和连接故障。
- 复用时跳过 `TrackSubAgentStartAsync`：上一轮仍保持 completed/failed，后续终态会
  被幂等检查忽略。
- 删除 UNIQUE 索引：会让当前状态出现多行，运行数、取消和 UI 投影全部失真。

## 7. 延迟问题的定位

不要用“点击发送到看到回复”的总时间直接归因 LLM。应拆分：

```text
accept latency
queue wait
snapshot build
LLM first-token
tool execution
LLM continuation
terminal commit
projection lag
SSE delivery
frontend render
```

优先查询：

- `runtime_activity`：按 component/operation 查看阶段耗时；
- `telemetry_metric_events`：Provider、工具和执行指标；
- `llm_gateway`：首 token、流结束和网络错误；
- `tool_runner`：审批等待和工具耗时；
- Conversation event sequence/checkpoint：投影延迟。

只有在证据表明 Provider 阶段最长时，才处理 LLM 超时或 Provider 稳定性。

### 7.1 子代理刷新后 Token/工具指标归零

先区分“执行事件没有产生”和“bootstrap 没有恢复”：

1. 查看
   `data/workspaces/{workspaceId}/agents/{agentId}/runs/{runId}/events.jsonl`，
   确认 `subagent.llm.completed`、`subagent.tool.completed` 和终态存在。
2. 查询 `conversation_events`，确认相同事件具有 `run_id` 和连续 sequence。
3. 检查 `/api/conversations/{id}/bootstrap` 的 `subAgentEvents`，确认内部
   round/LLM/tool 事件的顶层 `runId` 没有丢失。
4. 检查 live SSE 与 gap replay JSON 是否同样输出顶层 `runId`。
5. 刷新前后打开子代理面板，对比 Token、工具次数、模型、轮次和终态。

如果 live 正常而刷新后归零，通常不是 reducer 计算错误，而是 Event Store
信封的 `RunId` 没有经过 bootstrap/replay 序列化，或者 bootstrap 只分页普通
消息、未单独加载 `subagent.*` 事件。不要恢复旧 `/sub-agents` 轮询作为补偿。

如果刷新后 Token 恰好成倍增长，检查相同 `eventId` 是否同时经 bootstrap、
gap replay 和 live SSE 到达，以及 `subAgentReducer` 是否已记录并拒绝重复事件。
不要按事件来源分别维护三套计数器。

如果服务重启后仍显示历史 run 为 Running，检查
`ISubAgentRunStore.RecoverInterruptedRunsAsync` 和
`SubAgentConversationProjectionWorker` 启动日志。上一进程的进程内任务不能续跑，
必须经终态仲裁提交 `subagent.run.interrupted`，不能只在前端按时间隐藏。

### 7.2 子代理检查器缺少模型消息或工具输入输出

子代理检查器的执行时间线只消费 canonical `subagent.*` Conversation Event：

1. 从检查器复制 `Session ID` 和 `Run ID`，用于日志与 run archive 关联。
2. 查看对应 `events.jsonl`：`subagent.llm.completed` 应包含
   `message_preview/reasoning_available`；`subagent.tool.started` 应包含
   `arguments_preview`；`subagent.tool.completed/failed` 应包含
   `output_preview` 和截断标记。
3. 预览字段必须经过 KeyVault 脱敏且有长度上限。原始隐藏思维链、完整 Prompt、
   密钥和完整工具输出不应进入事件文件。
4. 如果 archive 有字段而页面没有，依次检查 Conversation Event 投影、
   bootstrap 的 `subAgentEvents`、gap replay、live SSE 和 `subAgentReducer`。
5. 不要为详情检查器新增轮询或第二条实时通道；历史恢复与实时追加必须共享同一
   eventId 幂等 reducer。

### 7.3 子代理检查器只显示摘要或完整结果为空

先区分 Conversation Event 摘要与 run archive 完整输出：

- `subagent.run.completed` 中的 `result_summary/reply` 是会话投影使用的有界摘要，
  不能当作返回主 Agent 的完整结果；
- `data/workspaces/{workspaceId}/agents/{agentId}/runs/{runId}/output.md` 保存子代理
  最终原始回复；同步 `spawn_sub_agent` 的结构化工具结果会把该内容放入
  `rawOutput` 返回给主 Agent；
- 检查器只在选中终态 run 后调用
  `GET /api/sub-agents/runs/{runId}/output` 一次性读取 `output.md`，运行状态仍只由
  canonical Conversation Event reducer 决定。

诊断顺序：

1. 从检查器复制 `Run ID`，直接请求 output 端点，确认 HTTP 状态与 `output` 长度。
2. 若端点返回 `null`，检查同一 run 目录是否存在 `output.md`，以及
   `AgentExecutionService.TryCompleteSubAgentRunAsync` 提交终态时的 `output` 是否为空。
3. 若端点有完整内容而 UI 仍显示摘要，检查
   `SubAgentActivityDock.getSubAgentRunOutput` 是否成功，以及页面是否仍在提供旧
   `dist/` 产物。
4. 不得用 `result_summary` 回退伪装成完整结果；加载失败应显式显示错误。

### 子代理卡片进入消息流

症状：主 Agent 连续调用多个 Smart 工具时，消息流出现“子代理执行结束”横条，
或子代理状态更新导致滚动位置变化。

检查顺序：

1. 检查 `viewport/messageProjection.ts` 和 `VirtualMessageItem`，确认不存在
   `subagent` / `subagent-anchor` 分支；
2. 浏览器统计 `[data-testid="subagent-anchor"]`，结果必须为 `0`；
3. 同时确认右上角固定运行坞仍显示活动 run，检查器列表仍能查看每个终态 run；
4. 从 run 的 `events.jsonl` 检查 `parent_turn_id / parent_run_id`，父子因果关系应
   保存在诊断数据中，而不是依赖消息流卡片表达。

不要通过删除 run、过滤 canonical 事件或缩小卡片 CSS 处理；消息流与运行诊断必须是
两套职责清晰的投影。

### 7.4 `file_search` 路径不一致或 `smart_explore` 只返回文件清单

`file_search` 的 Agent 可见契约是“只返回规范化绝对路径”。Everything、
BuiltIn provider 以及 fallback 不得分别暴露不同路径格式。

诊断顺序：

1. 直接执行同一目录的 Everything 与 BuiltIn 搜索，确认结果 JSON 数组中每个值都能
   通过 `Path.IsPathRooted`，且指向同一实际文件。
2. 若 provider 返回相对路径，检查 `FileSearchTool.NormalizeAbsolutePaths` 是否使用
   已解析的搜索根目录转换；不要在前端或主 Agent 提示词中补路径。
3. 若 `smart_explore` 只返回文件名或“找到 N 个文件”，检查传给子代理的任务是否仍含
   `DIRECT_ANSWER / VERIFIED_ARTIFACTS / RESPONSIBILITY / RELATIONSHIPS / EVIDENCE`
   输出契约。
4. `file_search` 只负责发现候选路径。Explorer 必须继续用 `code_outline`、
   `file_read` 或 `search_grep` 验证高价值候选，并返回符号、行号、职责、调用/数据流
   关系和与问题的直接关联；不能把未经读取的路径清单当作完成结果。
5. 主 Agent 收到符合契约的证据包后，不应为确认同一事实重复调用上述探索工具；若仍
   重复搜索，先检查 Explorer 的 `GAPS` 是否明确声明了未验证项。

### 7.5 Smart 工具显示成功但只返回 `done/completed`

所有 `smart_*` 工具必须返回 canonical 五段报告：
`SUMMARY / CHANGES / EVIDENCE / RISKS / BLOCKERS`。角色细节不同，但不能只报告状态。

诊断顺序：

1. 从子代理检查器复制 Run ID，读取归档 `output.md`，确认是模型原始回复过短，还是
   `spawn_sub_agent` 结果封装丢失了 `rawOutput`。
2. 检查 Smart 角色 Prompt 是否调用了 `AppendCanonicalReportRules`，并包含本角色产物
   字段。例如 Developer 必须有文件/符号/命令/构建测试证据，Tester 必须有测试命令、
   计数、失败复现与覆盖缺口。
3. 查看 Runtime 日志中的 `INVALID_REPORT`。日志包含工具、Agent、失败原因和输出长度；
   返回给主 Agent 的结构化错误包含 `subAgentId/runId/validationError`，工具
   `Output` 必须保留完整 `spawn_sub_agent` 结果信封和 `rawOutput`。
4. 如果报告已有五段仍被拒绝，检查每段是否真的有内容；当前共享校验要求报告总长至少
   80 字符，`SUMMARY` 与 `EVIDENCE` 各至少 20 字符。
5. 不要在主 Agent 侧把短结果补写成成功报告，也不要自动无限重试。修正对应角色的
   Prompt/模型后重新调用，避免悄悄重复消耗 Token。

如果 `events.jsonl` 中曾出现完整五段报告，但 `output.md` 最终只剩
“探索完成/已生成报告”等状态摘要，这是执行引擎覆盖，不是 UI 丢字段：

1. 按顺序对比 `subagent.llm.completed`；完整报告可能先被无外层 JSON 的兼容解析标记为
   `CONTINUE`，随后模型用合法 `DONE` envelope 返回短摘要。
2. 检查 Smart 调用是否显式携带
   `expected_output_contract=SUMMARY, CHANGES, EVIDENCE, RISKS, BLOCKERS`。
3. 带该合同的执行应保留最近一次通过共享校验的候选报告；最终 DONE 内容不完整时，
   Runtime 日志应出现
   `[AgentExec] Restored prior contract-complete output session=... round=...`，
   且 `output.md` 和返回主 Agent 的 `rawOutput` 都应是恢复后的完整报告。
4. 如果没有恢复日志，先确认候选报告五个标题使用 `SECTION:` 格式且满足长度门槛，再查
   `ExpectedOutputCandidateTracker` 是否在同一 delegated run 内创建和观察。

如果 `output.md` 很长且包含完整五段，但 `INVALID_REPORT` 却记录约 100 字符，检查
`spawn_sub_agent` 的结构化结果信封：

1. `rawOutput` 是子代理完整输出的权威字段；`summary` 只表示 `SUMMARY` 段，或者在解析
   失败时只是模型输出的第一行，不能拿它代替完整五段报告做校验。
2. Qwen 等模型可能先输出一句说明，再用 JSON 代码围栏包住 `status=DONE` 信封；
   `AgentLoopResponse.Parse` 必须从前导说明后提取 JSON fence，并把其中的 `message`
   作为最终正文。
3. Smart Workflow 解包应先读取 `rawOutput`，再通过 `AgentLoopResponse.Parse` 解出嵌套
   `message`；只有不提供 `rawOutput` 的替代实现才回退到 `summary`。
4. 同期出现的 Embedding 401 属于回合后记忆嵌入故障，不会导致已完成的 Smart 报告
   缺段；不要把它误判为 `INVALID_REPORT` 的根因。

### 7.6 Smart 子代理在截止时间显示 cancelled，且轮次/工具统计归零

已复现样本：

```text
Session ID: 861ce7e80f0749c491afd75593763731-sub-2e58232a
Run ID:     run_20260719_071437_a0a21510e3fd
```

症状是运行恰好在约 600 秒结束，检查器显示 `The operation was canceled.`，但归档
已有 28 轮和多次工具调用，terminal 却被写成 `cancelled` 且统计为 0。该现象不是
Provider 主动取消，而是调用方 deadline 取消在 LLM 边界被转换成普通失败，并且外层
提前提交了一个缺少 journal 统计的终态。

诊断顺序：

1. 在子代理检查器复制 Session ID 和 Run ID，定位
   `data/workspaces/{workspaceId}/agents/{configurationAgentId}/runs/{runId}/`。
2. 检查 `run.json` 的 `maxElapsedSeconds/deadlineUtc/status`，确认结束时间是否贴近
   deadline。
3. 检查 `events.jsonl` 最后的 `subagent.round.*`、`subagent.tool.*` 和 terminal。
   已有 round/tool 事件但 terminal 为零，说明是终态累计链路错误，不是子代理没工作。
4. 过滤日志关键词：

   ```text
   [LlmInvocation]
   [AgentExec]
   CompleteRun
   run_20260719_071437_a0a21510e3fd
   ```

5. `LlmInvocationService` 必须在 caller token 已取消时重新抛出
   `OperationCanceledException`；只有 Provider 自身失败才返回普通 failed result。
6. `AgentExecutionService` 必须用 `ExecutionDeadlineUtc` 分类：
   deadline 到达为 `timed_out`，用户控制取消为 `cancelled`。同步与 SSE 都必须走公共
   terminal 路径，从 journal/事件累计真实轮次、工具次数和 Token。
7. 取消或超时后不得继续使用已取消 token 启动 memory writeback、compaction 或
   subconscious fallback；否则会产生第二次取消噪声并掩盖首个终态。

Smart 嵌套调用还要检查：

- Smart 不应携带 round/timeout 参数，预算必须来自 `runtime.execution.json`；
- 只有 Planner 执行快照设置 `AllowSubDelegation=true`；
- Planner capability whitelist 只包含 `smart_explore`；
- Explorer 的下一层委派开关必须为 false；
- `DelegationDepth >= MaxDelegationDepth` 时，`PuddingToolRegistry` 必须在调用前拒绝。

正确终态不变量：

```text
deadline reached  => timed_out
no progress       => execution_stalled
explicit cancel   => cancelled
terminal totals   => 与此前持久 run 事件一致
terminal count    => 1
```

### 7.7 主 Agent 显示“异常”，子代理预算却还没有结束

典型症状：Smart 工具仍在运行，但主 Agent 先进入“异常”；子代理
检查器仍显示 Running，或者子代理刚完成而父 Turn 已写
`runtime_execution_failed / 执行超时 (1200s)`。

这不是“把子代理超时再加长”可以解决的问题。根因是父 Turn 与子代理分别从各自调用
时刻计算相对 timeout：子预算晚于父预算，父取消令牌会在子代理提交结果前切断整条
工具链。

2026-07-22 的另一种已确认样本是：父 Turn `f689e8978b224ac3948422fb7efd1bea`
从 `11:15:17Z` 运行到 `11:55:17Z`，正好耗尽 2400 秒；一个同步子任务在
`11:53:17Z` 到达 `parent deadline - 120s` 后，主 Agent 又为同一任务启动 120 秒
重试，直接吃光父级收尾窗口。该症状不是 Provider 卡死，而是直接
`spawn_sub_agent` 未执行与 Smart 相同的收尾预留。

诊断顺序：

1. 查 `execution_commands / execution_runs / execution_journal`，先确认父 Turn 的
   `terminal_code` 和实际耗时；`执行超时 (1200s)` 表示父级预算先耗尽。
2. 查活动 Agent 的 `manifest.json.maxElapsedSeconds` 与 `runtime.execution.json`，Smart 工具已无预算常量。
3. 查子 run 的 `run.json.deadlineUtc`，验证它是否晚于父 Turn deadline。晚于即是
   deadline 传播断裂。
4. 沿以下字段逐层检查，任一层为 null 都会导致下游重新计时：

   ```text
   TurnExecutionContext.ExecutionDeadlineUtc
     -> RuntimeDispatchRequest.ExecutionDeadlineUtc
     -> ToolInvocationRequest.ExecutionDeadlineUtc
     -> ToolExecutionContext.ExecutionDeadlineUtc
     -> SubAgentInvocationRequest.ParentExecutionDeadlineUtc
     -> SubAgentSpawnRequest.ParentExecutionDeadlineUtc
   ```

5. `SubAgentManager` 的并发门等待也必须使用 deadline token；不能先无限等待信号量，
   拿到槽位后再启动 timeout。
6. 检查父 deadline 前最后 120 秒是否又出现同步子 run。若有，调度边界必须在创建
   run 前返回 `insufficient_execution_budget`，让主 Agent 用剩余时间总结现有结果。
7. 对超时/取消 run 对照 `events.jsonl` 与 terminal：已有 23 轮、57 次工具但 terminal
   为 0，说明异常边界丢失 Runtime 返回值。`FileSubAgentRunStore` 必须从持久事件合并
   真实轮次、工具数、耗时和工具失败统计后再提交终态。
8. 正确终态必须为 `execution_timeout`，而不是
   `runtime_execution_failed` 或普通 `cancelled`。

当前预算不变量：

```text
parent deadline = Turn 启动时冻结一次
parent hard cap = 86400s（最终安全上限）
progress window = 3600s（只由 meaningful progress 续期）
subagent max    = 86400s / 600 rounds / 2400 tool calls
smart_* budget  = 同一系统子代理预算；公开参数不可覆盖
parent reserve  = 120s
child deadline <= parent deadline - reserve
sync retry      = 剩余不足 reserve 时拒绝创建 run
terminal totals = max(Runtime 返回值, archive 已观察事实)
downstream      = 只能收紧，禁止放宽
```

重启验证若出现 `WinError 32` 且 `backend.out.log` 无法轮转，不要删除日志。先检查超时
Turn 派生的 `dotnet test` / `testhost` 是否在父 Runtime 退出后仍存活；这些孤儿进程会继承
后端 stdout 句柄并持续锁住日志。确认父进程已不存在且命令行属于该 Turn 后，只终止这棵
精确进程树，再重新启动 `dev-up.py`。另外，注入 singleton `IRuntimeLlmClient` 的解析器
必须也是 singleton 或通过显式 scope factory 获取；`ValidateScopes` 报
`Cannot consume scoped service ... from singleton IRuntimeLlmClient` 时应修正生命周期，而不是
关闭 DI 校验。

### 7.8 长任务应继续运行，还是已经停滞

父 Turn 的固定 2400 秒终止已由两级看门狗取代：24 小时硬安全上限，以及默认 1 小时
无有效进展窗口。出现 `execution_timeout` 或 `execution_stalled` 时先看终态码，不要把两者
都解释成 Provider 超时。

诊断顺序：

1. 检查 `D:\data\config\runtime.execution.json` 的 `turns`：

   ```text
   defaultHardTimeoutSeconds = 86400
   maxHardTimeoutSeconds     = 86400
   noProgressTimeoutSeconds  = 3600
   watchdogPollIntervalSeconds = 5
   llmFirstChunkTimeoutSeconds = 300
   llmStreamIdleTimeoutSeconds = 120
   ```

2. 检查 Agent manifest 的 `maxElapsedSeconds`。它仍会收紧平台硬上限；旧实例残留 2400
   会继续在 40 分钟终止，必须原地升级为 86400。
3. 过滤日志 `[Coordinator] Watchdog cancelled`：`kind=HardTimeout` 表示硬上限；
   `kind=Stalled` 同时记录 `idleSeconds/lastStage`，用于定位停在 LLM、工具还是子代理。
4. 过滤 `[StreamWatchdog]`。首块超过 300 秒或流中 120 秒无块属于单次 Provider 调用卡死，
   和父 Run 的一小时跨阶段窗口不同。Provider `streamTimeoutSeconds` 只能收紧空闲窗口；
   持续收到流块时不再按旧的固定“流总时长”取消。
5. `IExecutionProgressRegistry` 中，LLM 新文本/推理/工具参数、LLM 完成、工具完成和同一
   Conversation 子代理的新事实会续期；lease 续租、SSE keepalive、空 provider 帧只计
   liveness。相同 Run、阶段、指纹的重复输出不续期。
6. 若界面仍把 `execution_stalled` 显示为普通“异常”，检查 Conversation terminal 投影和
   前端错误码映射；不得通过刷新或 SSE 心跳反向修改服务端看门狗状态。

### 7.9 `ResponseEnded` 后 Agent 联系人持续显示“异常”

典型日志：`[DirectLlm] STREAM ERROR`，异常为
`HttpIOException: The response ended prematurely. (ResponseEnded)`；对应 Conversation
终态为 `turn.failed / runtime_execution_failed`，而 `/api/workspaces/{workspaceId}/agents/status`
在 Run 已结束后仍返回 `failed`。

诊断时先区分两个层次：

1. `ResponseEnded` 是 Provider/网络在响应未完整结束前断开，先查同一时刻是否多个模型或
   潜意识请求同时失败；若是，优先判断为传输层瞬态故障，不要归因前端 SSE。
2. 联系人状态表示“当前是否仍在运行”，不能把历史 `turn.failed` 永久当成当前异常。
   `TurnFailed`、`TurnCancelled`、`RunLeaseLost` 都是终态；Run 结束后联系人应回到
   `idle`，失败详情由聊天终态事件保留。

重试安全边界：

- 只允许在尚未产生任何 `StreamDelta` 时重试传输错误；
- 一旦已产生正文、思考、usage 或工具调用增量，禁止重试，避免重复正文和重复工具调用；
- HTTP 5xx、无状态码且带网络/IO 内因的 `HttpRequestException`、`HttpIOException`、
  HTTP client timeout 可重试；HTTP 4xx、协议解析错误不可重试；
- `OpenAiLlmGateway` 抛出 HTTP 错误时必须保留 `StatusCode`，否则重试策略无法区分
  4xx 与 5xx。

验证日志应出现首块前的 `[DirectLlm] STREAM RETRY before first delta`，最终成功时不应
写入 `turn.failed`；若首块后断流，则应直接失败且只能看到一次 Provider 请求。

### 7.10 Smart 显示失败，但子代理已经产出完整报告

不要只看父 Turn 的“1 个失败”标签。先在对应 run archive 对照 round 终态、完整输出和
terminal 事件：

```powershell
$runDir = 'D:\data\workspaces\default\agents\<agentId>\runs\<runId>'
rg -n 'subagent.tool.failed|"status":"DONE"|subagent.run.failed|subagent.run.completed' `
  (Join-Path $runDir 'events.jsonl')
Get-Content (Join-Path $runDir 'output.md')
Get-Content (Join-Path $runDir 'errors.jsonl')
Get-Content (Join-Path $runDir 'run.json')
```

若先出现一次 `subagent.tool.failed`，之后出现 canonical 五段报告和
`subagent.round.completed status=DONE`，但下一条仍是 `subagent.run.failed`，重点检查：

1. `AgentExecutionOutcomePolicy` 是否把完整 canonical 报告置于文本失败启发式之前；报告
   在 EVIDENCE 中提到 `Completed→Failed` 或 `timed out` 不代表本次运行失败。
2. Smart 失败结果的 `Output` 是否仍保留 `spawn_sub_agent` 信封及 `rawOutput`；若为空，
   父 Agent 会重复已经完成的探索。
3. `total_rounds` 应等于 `subagent.round.started` 的最大 round，而不是 journal 条目数量；
   工具调用和状态记录不能被重复计作 round。
4. Yolo 模式下仍须执行角色 `AllowedToolNames` 暴露边界。若 Explorer 调用了 `goal_read`
   等白名单外工具，同时核对 Tool schema 和 AgentFirewall CapabilityGate。

单次错误后继续完成是正常自愈；只有没有合格最终交付、显式 `FAILED`、超时或取消时才应
提交失败终态。

## 8. 浏览器验收

每次修改聊天链路后至少完成：

1. 登录并打开同一个 Conversation。
2. 发送一个唯一文本，例如 `E2E_<时间戳>`。
3. 确认 POST 返回 202。
4. 确认 SSE 实时显示用户消息和助手回复。
5. 等待历史同步周期，再确认回复没有消失。
6. 刷新页面，确认消息从持久化投影恢复。
7. 断开并恢复后端，确认 SSE 能重连和 replay。
8. 检查当前运行周期不存在新的 Error。

不能只以“页面出现文字”作为通过条件。

### 8.1 React 根节点空白

页面标题和静态 HTML 已加载、但 Chat 工作台整体空白时，先检查浏览器控制台，不要先归因于
代理、SSE 或历史接口。Mako 开发服务器出现
`Runtime error found, and it will cause a full reload` 时，继续读取紧随其后的第一条
`ReferenceError` 及组件栈；例如 Hook 在依赖数组中读取尚未初始化的 `const`，会在
`AgentMessageBubble` 首次渲染时直接中断整个 React 根节点。

修复后至少同时验证：

1. 受影响组件的聚焦 Jest 测试从相同异常恢复为通过；
2. `npm run build` 成功；
3. 重新加载 `/admin/chat` 后工作台和 Composer 可见；
4. 仅统计重新加载时间点之后的浏览器 Error，避免把修复前缓存的控制台记录误判为新错误。

## 9. 日志埋点约束

新增命令链路日志时，至少包含：

- `traceId`
- `conversationId`
- `turnId`
- `commandId`
- `runId`
- `messageId`
- `eventSequence`
- `projectionCheckpoint`
- `providerId/modelId`（不得记录密钥）
- `durationMs`
- `terminalStatus`
- `errorId`

日志应记录组件边界和状态转换，不要记录完整 Prompt、API Key、Authorization Header 或用户敏感内容。

推荐格式：

```text
[Component] action status key=value key=value durationMs=...
```

错误日志必须说明：

- 哪个组件失败；
- 哪个稳定身份受影响；
- 当前状态和期望状态；
- 是否可重试；
- `errorId`；
- 原始异常。

## 10. 代码调试入口

| 问题 | 首要断点 |
|---|---|
| 请求没有受理 | `ConversationTurnsController`、`SubmitTurnHandler` |
| 原子写入失败 | `ConversationAcceptanceStore` |
| Worker 不领取 | `ChatExecutionWorker`、`SqliteExecutionLeaseStore.TryAcquireAsync` |
| 快照/LLM 配置错误 | `AgentExecutionSnapshotFactory`、`AgentRuntimeProfileResolver`、`AgentLLMConfigResolver` |
| Agent 循环不结束 | `ExecutionRunCoordinator`、`TurnExecutorAdapter`、`AgentExecutionService` |
| Smart/子代理卡住 | `SubAgentManager`、`AgentExecutionService`、`FileSubAgentRunStore`、`SubAgentConversationProjectionWorker`、前端 `subAgentReducer` |
| 取消/Steering 无效 | `SqliteControlInbox`、`ExecutionControlService` |
| 终态丢失 | `SqliteExecutionJournal.CommitTerminalAsync` |
| 历史缺失 | `ConversationProjectionWorker`、`ConversationProjector`、`ChatTranscriptWriter` |
| SSE 重连错误 | `SessionEventsController`、前端 `subscribeSessionEvents` |
| 回复被旧历史覆盖 | 前端 `useChatState` 的 history reconciliation |
| 消息滚动突然跳动/重叠 | 前端 `useMessageViewportRuntime`：检查 `data-virtualized`、`data-viewport-item-id` 唯一性、row 实测高度、历史 prepend anchor、`followMode` 和每帧 scroll 读取次数 |
| Memory 缺表 | `MemoryDbInitializer`、`MemoryLibraryDbInitializer`、`Program.cs` 启动顺序 |

### 10.1 Chat 滚动跳变诊断

先区分三类原因：

1. **富文本会话的虚拟化决策错误**：检查 `chat-message-viewport-content` 的 `data-virtualized`。短会话保持正常文档流；中等会话若累计 Markdown/process 内容已经达到高渲染重量，应提前虚拟化，不能只等 row 数达到 80/200。刷新后 DOM 中 `[data-viewport-item-id]` 应只保留可见行和少量 overscan。
2. **历史前插未恢复锚点**：滚动到顶部加载旧消息前后，记录第一条可见 row 的 `data-viewport-item-id` 和相对 viewport top。二者应保持不变；不能只比较 `scrollTop`。
3. **贴底抢滚动**：用户阅读历史时 `followMode` 必须为 `off`；仅 `user-send`、手动回底部或 pinned 模式允许写入底部位置。
4. **虚拟行 key 冲突**：统计 `[data-viewport-item-id]` 总数与唯一值数量，并检查控制台 `Encountered two children with the same key`。row id 必须来自 user/assistant message id，不能只使用 canonical `turnId`；高度缓存同样必须按 message id，避免历史前插后下标复用。

性能检查：

- 连续触发多个 `scroll` event，同一 animation frame 内 `scrollHeight` 只能读取一次；
- 短时间线连续上下滚动时 `scrollHeight` 应保持稳定；
- 历史 prepend 后，正常文档流的 `scrollTop` 增量应等于新增内容高度，第一条可见 row 的屏幕位置不变；
- 不要在 `MessageList`、`useChatState` 或子组件再注册第二套滚动修正逻辑。

### 10.2 后端突然停止且登录返回 502

先执行：

```powershell
python .\dev-up.py --status
Get-Content .\tmp\dev\backend.out.log -Tail 200
```

若状态为 `Backend: stopped`，但日志在停止前没有未处理异常，应检查最后一条
`[HostShell]` / `[Terminal]` 记录。曾出现子代理执行
`taskkill /PID <PuddingAgent host pid> /F`，直接终止宿主，表现为代理和登录接口同时
返回 502，而不是登录控制器故障。

修复后的安全约束：

- `TerminalSecurity` 在 Normal/YOLO 之前执行宿主安全不变量；
- 原始进程终止命令必须被拒绝；
- 只允许使用 `terminal_cancel(job_id)` 终止当前会话创建的后台任务；
- 恢复后必须同时验证 `dev-up.py --status` 为 HTTP 200、登录成功以及 Chat 页面可加载。

### 10.3 LLM 注册表启动失败，或 Agent 使用了错误模型

先检查最新启动日志和 Agent manifest：

```powershell
python .\dev-up.py --status
Get-Content .\tmp\dev\backend.out.log -Tail 200
Get-Content D:\data\agents\<agentId>\manifest.json
```

- 若启动日志包含 `llm.providers.json must define at least one profile`，说明仍在把
  Provider/Model 注册表误当成默认路由配置。`profiles` 可以为空；遗留 `roles`
  也不能阻止 Provider 注册表启动。
- 主 Agent 只读取 manifest 的 `preferredProviderId + preferredModelId`。用这两个值
  精确核对 `D:\data\config\llm.providers.json` 中已启用、未废弃的 Provider/Model；
  不要把 `config/llm.json` 当成执行期真相源。
- 字段缺失或注册表中不存在时，预期终态为 `agent_configuration_invalid`；错误消息应
  包含 Agent ID、manifest 路径、缺失或无效的字段，并明确说明没有选择 fallback。
- `config/llm.json` 仅是管理兼容镜像。即使其中保留旧 Provider/Model，也不得改变主
  Agent 的执行模型。确认日志中的 `[AgentInvocation] resolved` Provider/Model 与
  manifest 完全一致。

## 11. 测试诊断

### 11.1 DI 接口未注册导致 Null Service

**症状**: 测试中 `GetService<TInterface>()` 返回 null，但 `TImplementation` 已注册。

**根因**: `services.AddSingleton<ConcreteType>()` 只注册具体类型，不注册接口。`GetService<IInterface>()` 返回 null。

**修复**: 使用 `services.AddSingleton<IInterface, ConcreteType>()` 注册接口映射。

**检索关键词**: `Sequence contains no elements`, `IChatTranscriptWriter`, `transcriptWriter is null`

### 11.2 Chat 回放测试返回空 turns，但事件路由代码没有日志

**症状**：空历史 active replay 用例期望恢复一个 Turn，实际为 `[]`；既没有
`replayLatestTurn align`，也没有 `event.terminal.unmapped/staleTarget`。

**先查证据**：筛选 `[Pudding ChatDiag] session.select.error`。如果其中出现
`normalizeConversationEventType is not a function`，说明回放尚未进入事件路由，不能先改
React state/ref 竞态。

**根因**：Jest 对 `@/services/platform/api` 使用整模块 mock；生产代码新增命名导出后，
测试 fixture 没同步。`handleSelectSession` 捕获归一化阶段的 `TypeError`，于是 turns 保持
空数组。

**修复与防回归**：在 mock 中补齐与生产入口同语义的
`normalizeConversationEventType`，再单跑空历史 replay 用例，并确认日志依次出现
`session.select.history.loaded`、`replayLatestTurn align`、`event.terminal.apply`、
`event.done.applied`。这类失败应先区分“fixture 漂移”和“运行时竞态”。

### 11.3 Chat 错误终态必须保留日志检索字段

`error` 事件以及带 `isError/errorId/errorCode` 的 `done` 事件都必须投影为 error 气泡，
不能把后一类误标为 success。最终 Markdown 至少保留当前事件中已有的 Session、
Message/Turn、Trace、Error ID、Location、Error Code、Round、Model 和 Endpoint Host；
禁止记录 API Key、Authorization Header 或完整请求体。

若服务端已经持久化 `## 请求失败` 诊断 Markdown 或 Session fuse 文本，前端应原样保留；
否则使用统一格式化器生成。排查时以 `errorId` 为第一检索键，再关联 `traceId`、
`sessionId` 与 `messageId/turnId`。

### 11.4 Umi/Jest 报 imported binding 无法转换

**症状**：新增 Hook 测试在收集阶段失败，错误包含
`Cannot transform the imported binding "X" since it's also used in a type annotation`，业务断言尚未执行。

**根因**：当前 Umi/Jest Babel 链对同一个 import binding 同时出现在运行时 import 重写和
TypeScript 类型标注中的场景处理不稳定；这不是 Hook 运行时错误。

**修复**：测试 fixture 使用测试文件内的窄类型，或让 TypeScript 从局部值推断；不要为了
修测试去修改生产 API 类型。修复后先单跑该测试文件，再合入 Chat 定向集。

### 11.5 Chat Hook 拆分后的依赖与时序诊断

`useChatState` 的复杂生命周期现在通过分组 port object 和 bindable callback ref 协作。
出现“函数已执行但调用的是旧会话/旧 projector”时，依次检查：

1. binder 是否在每次 render 同步写入稳定 ref，而不是只在 mount effect 绑定；
2. identity port 的 `sessionIdRef`、`selectedSessionIdRef`、`sseSessionIdRef` 是否指向同一事务；
3. buffer/reset 是否由 `useSessionEventBuffers` 单一所有，切会话时是否同时清理 delta/thinking timer；
4. history projector 是否先绑定，再允许分页或 selection effect 发起历史请求；
5. 用 `useChatState.selection.test.tsx` 覆盖“发送未返回时切会话”“空历史 replay”“快速终态”竞态，
   不要只给搬迁后的内部函数写静态快照测试。

### 11.6 Agent 已完成但回复气泡必须刷新才出现

**症状**：用户消息立即出现，Agent 也确实完成；浏览器可能已经记录
`[Pudding ChatDiag] event.done.applied`，但页面仍停在首 Token 等待态，刷新后回复才出现。

**诊断顺序**：

1. 用同一 `conversationId/turnId` 查询 `conversation_events` 和 `ChatMessages`，确认终态事件、
   回复正文与稳定身份都已落库；
2. 检查浏览器 `event.done.applied` 的 `replyLen/currentAnswerLen/isStreaming`，区分 SSE 未到达与
   本地 Turn 已完成但被另一套视图遮蔽；
3. 对比 Agent conversation 查询的 `eventCursor` 与最后一条 `messages.role`；
4. 检查代理日志是否对同一 cursor 连续返回 `304`。

已出现过的竞态是：canonical event cursor 先推进到终态，助手消息读模型稍后才物化。
此时 conversation 快照可能暂时以 `user` 消息结尾；如果客户端把该快照当作完全追平，并继续携带
相同 `knownCursor`，服务端会稳定返回 `304`，不完整快照直到刷新都不会更新。

前端应同时守住两层：

- `chatClientStore` 发现 conversation 以用户消息结尾时，暂不使用条件 GET，并以活跃频率继续
  拉取，直到助手消息投影出现；
- `MessageList` 在 canonical 投影落后期间保留并覆盖本地 SSE 已完成的助手 Turn，同时抑制同一
  `commandClientId` 的陈旧 `activeRun` 等待占位。

若 `GET /api/workspaces/{workspaceId}/agents/{agentId}/conversation` 已返回
`activeRun: null` 和对应助手终态，但页面仍停在“深入分析中/复杂推理中”，再检查两种投影回退：

1. `mergeActiveRunIntoTurns` 不得用空或较短的 `outputSnapshot` 整体覆盖本地 SSE assistant；
   `answerMarkdown`、`timelineItems` 与行 identity 都必须单调合并，避免 reasoning 预览被清空或
   React remount 后退回等待态。
2. canonical conversation 已经由 workspace/agent/main-session 查询限定，页面入口还会校验
   workspace/agent。`MessageList` 不得再按 `localTurns` 是否能匹配来过滤 canonical turns；
   本地历史在首次加载、分页或终态投影竞态中可以为空或不完整，这种过滤会同时删掉服务端已持久化
   的助手终态、图片 metadata 和 Agent 入站消息。

快速判别方法：记录刷新前等待占位与侧栏状态，然后请求上述 conversation endpoint；若服务端已终态，
刷新后占位立即消失，就是前端投影/同步问题，不是 LLM 仍在推理。后端日志中的
`[AgentExec:Stream:Round] thinkingFrames > 0` 还能进一步证明 reasoning 已从模型产生。

回归测试至少覆盖“终态 cursor + user-only 快照强制全量追平”和“本地终态回复覆盖 user-only
canonical 快照且不显示等待占位”；还要覆盖“本地已有 reasoning、activeRun 快照为空”和
“localTurns 为空但 canonical 历史完整”。浏览器验收必须在不刷新页面的前提下观察运行气泡和
最终回复出现，再刷新确认持久化投影一致。

### 11.7 子代理早已结束，但运行坞仍显示 Running

**症状**：右侧运行坞或输入框状态条长期显示一个或多个子代理运行中；运行归档和
`sub_agent_runs` 已有 `completed/failed` 终态。

先比对三份事实：

1. `runs/{runId}/events.jsonl` 是否有 `subagent.run.*` 终态；
2. `GET /api/sessions/{sessionId}/sub-agents` 是否返回终态与 `completedAt`；
3. 浏览器 `subAgentReducer` 中同一 `subSessionId/runId` 是否仍为 `running`。

长会话的 bootstrap 只携带最近 5000 条 `subagent.*` 事件，窗口可能从历史 run 中间开始，
也可能完全排除旧 run 的终态。前端应只对本地 active run 使用会话状态快照做终态校正，不能
让快照把已经终态的卡片重新降级为 running。成功终态在运行坞停留 12 秒，异常终态停留
30 秒；历史与完整 `output.md` 仍可在检查器中查看。

回归测试至少覆盖：事件快照只有 `run.started`、状态 API 已 `completed`；校正后卡片立即终态，
且成功/异常均在各自停留窗口后从运行坞隐藏。

如果历史子代理卡片出现在最新主 Agent 消息气泡中，或刷新/滚动后卡片换绑到另一轮，继续检查：

1. 父 Conversation bootstrap 中该 `subagent.*` 事件的 envelope 是否缺少 `turnId`，payload 是否
   缺少 `parent_turn_id`；这类旧事件不得调用通用 `resolveTurnIdForEvent` 并回退到最新 Turn。
2. `useSessionEventProjection` 必须先将所有 `subagent.*` 交给 `subAgentReducer`，随后立即返回；
   消息列表和主 Agent `timelineItems` 均不得消费子代理运行事实。
   canonical Conversation View 也不得把 legacy `subagent.*` 转成 `processItems`；
   `MessageList` 应防御过滤已有历史数据中的 `subagent.` / `subagent_` kind。
3. 对当前 run，检查 `runs/{runId}/run.json` 的
   `parent_execution_identity.conversation_id/turn_id`，再确认相同 `eventId` 已进入父
   Conversation bootstrap。若事件只存在于 `SubSessionId` 的子会话，修复
   `FileSubAgentRunStore` 的投影目标，并将精确 run 的 `conversation-projection.cursor`
   回拨后重放；不得清空整个事件库。
4. 若游标已回拨但长期不推进，统计 run 目录数量并检查补投扫描是否始终停在固定首批。
   `maxRuns` 是单轮有界扫描量，扫描起点必须跨轮次轮转；不能每两秒永久重复前 N 个目录。

正确结果是：主消息流中没有“子代理运行中/失败”气泡；活动和终态只显示在独立运行坞，并且
bootstrap、gap replay 与 live SSE 都折叠到同一个 run。

### 11.8 新消息已受理但长期无回复，浏览器重复拉取同一事件页

先同时看三处证据：浏览器 `[Pudding ChatDiag] events.replay.complete`、
`conversation_heads.head_sequence` 和 `chat_execution_commands.status/attempt_count`。

- 如果 `events=50`、`lastSequenceNum` 已变化，但 `nextFrom` 不变且 `applied=0`，检查回放页最大
  sequence 的归并初值；`Math.max(Number.NaN, sequence)` 永远是 `NaN`，会让游标停在同一页。
- 如果最新 Turn 为 `pending/attempt_count=0`，同时更早命令仍为 `running`，说明同会话串行 Worker
  正被旧执行占用；继续查该 run 的 Smart 子代理、LLM 请求 deadline 和 lease 续租，不能先归因 SSE。
- 如果控制台同时报告重复 `subagent-completed-*` key，说明历史终态被重复投影；终态时间线项必须
  按稳定 ID 幂等覆盖，不能每次 replay 都追加。
- 如果最终回复刷新后存在，但运行中出现空助手壳、回复另起一条或 React 报重复
  `message:agent:{turnId}:assistant:0` key，检查 conversation API 的 user/agent 两条消息是否返回同一个
  canonical `turnId`。用户消息通常没有 execution `runId`，投影层必须用 `ChatMessages.turn_id`，或用
  `chat_execution_commands.user_message_id/message_id` 反查 Turn；前端合并必须比较 `turnId`，不能再比较
  `runId`。否则同一逻辑轮会被拆成“本地运行壳 + 最终消息”两轮，React 的重复 key 复用会让气泡闪烁或消失。

服务端 forward/backward event page 应读取 `limit + 1` 条，再以 `count > limit` 计算 `hasMore`；
恰好装满最后一页时返回 `hasMore=true` 会制造无意义的额外回放请求并干扰诊断。

### 11.9 图片发送后气泡无图，或文本模型报 `unknown variant image_url`

先区分三条独立链路：前端附件元数据、主 Agent 模型路由、视觉读取工具。

1. 浏览器发送前应出现“待发送图片 N 张”，发送后同一用户气泡立即出现 N 个图片元素；刷新后仍应由
   `visionArtifactIds` 恢复。若只有文字，检查 `useMessageSend` 的乐观消息和离线 Outbox 是否保留 metadata。
   若 `POST /vision-artifacts` 返回 500，先按 `errorId` 查日志。`Unsupported vision artifact MIME type
   'image/bmp'` 表示前端漏掉了 provider-safe 转码：`IntentConsole` 应通过
   `visionArtifactImage.normalizeVisionArtifactFile` 将 BMP/GIF/AVIF 等浏览器可解码格式转成 PNG。
   后端只接收 JPEG/PNG/WebP；绕过前端提交其它 MIME 时必须返回 415 和支持列表，不能抛出 500。
2. 日志中的主调用必须仍是实例快照的 Provider/Model，例如
   `[LlmInvocation] ... provider=deepseek ... model=deepseek-v4-pro`。上传图片不能把主 Agent 强制路由到
   某个固定视觉模型。
3. `DirectLlm` 只有在当前模型配置包含 `vision` 标签时才能序列化图片内容。文本主模型不会接收
   `image_url`；`ExecutionRunCoordinator` 必须先调用 `VisualArtifactObservationService`，日志顺序是
   `[VisualObservation] Analyze` → 视觉 `[LlmInvocation]` → `[VisualObservation] Completed` → 主模型
   `[LlmInvocation]`。不能再以“主模型可能自行调用 `image_reader`”作为正确性条件。
4. 原生视觉主模型应记录 `[VisualObservation] Native vision route` 并跳过第二次视觉调用。
   `image_reader` 仍用于 Agent 对某张图做定向二次检查，调用时才应出现 `tool=image_reader` 与
   `[ImageReader] Analyze ...`。

`unknown variant image_url, expected text` 表示文本模型收到了多模态 payload；检查该模型是否被错误标记为
`vision`。`model_not_found` 则先查询 Provider 的 `/models`，不要把不存在的视觉模型 ID 写进
`llm.providers.json`。强制预识别不依赖 Agent manifest 的 `cap-image-reader`；只有需要 Agent 主动二次
复查时才要求该 capability。可用以下命令快速确认：

```powershell
Invoke-WebRequest http://localhost:5000/api/tools/image_reader -UseBasicParsing
rg -n "VisualObservation|LlmInvocation|DirectLlm:Tools|tool=image_reader|ImageReader" .\tmp\dev\backend.out.log
```

### 11.10 连续压缩后会话标题重复出现“压缩 - ”

先调用 `GET /api/sessions/{sessionId}` 检查 `title`。如果 API 已返回
`压缩 - 压缩 - ...`，问题发生在后端持久化，不是前端渲染。标题的唯一生成权威是
`CompactionSessionSuccessor.BuildSuccessorTitle`：它必须剥离全部既有连续前缀，再添加一次
`压缩 - `。不要在 Controller、事件投影或前端分别补前缀，否则重放和刷新会产生不同标题。

如果 Session API 已经是单前缀，但消息气泡仍显示重复前缀，再检查
`GET /api/workspaces/{workspaceId}/agents/{agentId}/conversation` 的 `messages[].sourceName`。
普通 Agent 输出必须使用实例 manifest 的 `displayName/name`，不能使用 `main.Title`；
只有消息信封显式携带来源身份时才保留信封中的 `sourceName`。

修复代码后，历史污染数据不会自动改名。开发环境可先列出受影响会话，再通过现有 Rename API
逐条归一化；只修改 `title`，不要删除会话、消息或压缩事件：

```powershell
$sessions = Invoke-RestMethod 'http://localhost:5000/api/sessions?workspaceId=default'
$sessions | Where-Object { $_.title -match '^(压缩\s*-\s*){2,}' } |
    Select-Object sessionId, title
```

### 11.11 刷新 Chat 后历史消息长时间空白

先把耗时拆成三段，不要只看浏览器转圈：

1. 测量
   `GET /api/workspaces/{workspaceId}/agents/{agentId}/conversation`
   的耗时和响应字节数。若几十条消息达到数 MB，统计
   `messages[].processItems`；初始历史只能携带 `processSummary`，完整事件 payload
   必须在用户展开“查看过程”时经
   `/conversation/messages/{messageId}/process-items` 单独加载。
2. 查看 `tmp/dev/proxy.out.log` 的 `sse-line ... events=N`。刷新一次若从 sequence 0
   重放数千事件，检查主会话是否被 legacy `useChatState` 和 Agent canonical projection
   同时加载；Agent projection 拥有主会话时，legacy 历史/SSE 必须跳过。需要建立 legacy
   SSE 时，应等待历史 cursor 同步完成并发送 `Last-Event-ID`。
3. 接口已很快但首屏仍慢时，检查
   `chat-message-viewport-content[data-virtualized]` 和实际挂载 row 数。几十条包含表格、
   代码块或图片的消息不能一次性全部 Markdown 渲染；虚拟化应只挂载可见行与 overscan。

刷新/首次打开的正确视口语义是从最新一条消息开始。Agent canonical 架构下应先等
`AgentChatClientSnapshot.isRefreshing=false`，不能先按 IndexedDB 旧快照定位；随后在虚拟行
分批测量或图片延迟撑高期间以有界窗口收敛到底部。程序化 `scrollTop` 引发的 `scroll` 事件
不是用户意图；只有 wheel/touch/pointer/滚动键输入才终止初始收敛。用户第一次真实向上滚动
后，后续历史前插必须恢复可见 DOM 锚点，不能再次抢回底部。

推荐记录修复前后四个量：conversation 响应字节、API duration、SSE replay event 数、
首屏挂载 row 数。只减少接口耗时而仍同步渲染全部历史，不能视为完成修复。

### 11.12 飞书消息已发出但 Agent 或飞书没有回复

先按同一个 `connectorId/messageId/commandId` 区分五段，不要把“飞书没回复”直接归因到 LLM：

```text
Feishu WS -> Gateway ingress -> Message Fabric/ADR-059
          -> Conversation terminal -> Reply projection
          -> Connector delivery -> Feishu OpenAPI
```

当前运行时从渠道服务商、渠道实例和 Agent `channelIds` 三个文件事实装配绑定。以下命令只输出
布尔状态，不打印凭据：

```powershell
$agentId = "<agentInstanceId>"
$manifestPath = Join-Path "D:\data\agents\$agentId" "manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$channelId = @($manifest.channelIds)[0]
$channelPath = Join-Path (Join-Path "D:\data\channels" $channelId) "manifest.json"
$channel = Get-Content -LiteralPath $channelPath -Raw | ConvertFrom-Json
[pscustomobject]@{
    AgentId = $manifest.agentInstanceId
    ChannelId = $channel.channelId
    BoundInAgent = @($manifest.channelIds) -contains $channel.channelId
    FeishuEnabled = $channel.isEnabled -eq $true
    HasAppId = -not [string]::IsNullOrWhiteSpace([string]$channel.feishu.appId)
    HasAppSecret = -not [string]::IsNullOrWhiteSpace([string]$channel.feishu.appSecret)
    PrivilegedUserCount = @($channel.feishu.privilegedUserOpenIds).Count
}
```

旧 `D:\data\config\feishu.json` 只供 Harness 手工测试，Pudding Runtime 不会自动选一个
Agent 继承它。旧 Agent manifest 的 `feishu` 对象会在启动时迁移到 `data/channels` 并删除；
渠道或绑定修改后需要重启。

可以先执行不会发送消息的真实连通性冒烟。它只获取 tenant token、建立并关闭
WebSocket；默认测试套件会跳过该 Live 测试：

```powershell
$env:PUDDING_RUN_FEISHU_LIVE_TESTS = "1"
dotnet test .\Tests\HarnessAgent.Core.Tests\HarnessAgent.Core.Tests.csproj --no-restore `
  --filter "TestCategory=Live"
Remove-Item Env:PUDDING_RUN_FEISHU_LIVE_TESTS
```

按日志阶段检索：

```powershell
rg -n "\[Feishu\]|\[MessageGateway\]|\[ConnectorDelivery\]|Gateway ingress accepted" `
  .\tmp\dev\backend.out.log
```

| 最后证据 | 结论 |
|---|---|
| `[Feishu] Loaded 0 channel-owned connector binding(s)` | 渠道服务商/渠道未启用、渠道未绑定唯一启用 Agent，或 Agent 缺少对应 `channelIds` 引用 |
| `Channel credentials are incomplete` | 渠道 manifest 的 AppId/AppSecret 缺失 |
| `WebSocket connection failed` | endpoint、网络、凭据或飞书应用配置错误 |
| Echo 没有 `initial ping sent` 或 ping 后没有 `type=pong` | 长连握手/心跳层故障；官方 SDK 要求 WebSocket open 后立即发送首个 ping |
| Echo 有 `initial ping sent` 和 `type=pong`，确认发送新消息后却没有 `method=1 ... type=event` | 协议连接已活跃，但飞书未向本客户端投递事件；查事件订阅/版本发布，并排除同 AppId 的其他长连客户端正在分流事件 |
| 有 `event_type=im.message.receive_v1`，但 `message_type=-` 且没有 `received message` | 入站模型字段映射错误；真实事件是 `message_type`，不是出站 OpenAPI 请求的 `msg_type` |
| 有 WS event、没有 `Inbound accepted` | 事件字段/类型映射或 Gateway durable acceptance 失败；飞书应收到非 200 ACK |
| 有 `Ingress accepted`、没有 `Gateway ingress accepted` | Agent MessageDelivery 未被 canonical ADR-059 受理，查 delivery retry/dead-letter |
| Command succeeded、没有 `Reply projected` | terminal event/metadata/reply projection Schema 或 Worker 异常 |
| 有 `Reply projected`、没有 `ConnectorDelivery Delivered` | 只查 Connector delivery 重试和飞书 OpenAPI 错误；不要重跑 Agent |

飞书斜杠指令是另一条受控分支。`/help`、`/status`、`/whoami` 可由任意飞书用户调用；`/yolo`
等特权指令要求事件 sender `open_id` 位于当前渠道 manifest 的
`feishu.privilegedUserOpenIds`。不要把 open_id 打到诊断输出，只检查数量和布尔命中结果。

正常拦截日志为 `[MessageGateway] Command intercepted ... privileged=... whitelisted=...`。
该消息没有后续 `Gateway ingress accepted` 或 Agent Turn 是正确行为，因为 Pudding 会直接创建
durable Connector reply。未命中白名单应收到 `Permission denied` 且 Runtime mode 不变；命中后
`/yolo` 才能切换模式。若日志显示 `ForwardToAgent` 分支，则只允许投递处理器生成的
`AgentMessage`，原始斜杠文本不得成为 Agent prompt。

`/whoami` 应回复当前入站事件的 sender `open_id`，用于配置
`privilegedUserOpenIds`。它不需要白名单，也不应出现 Agent Turn。系统日志只记录命令种类和
授权布尔值，不应打印回复中的 open_id；需要核对 ID 时以飞书回复和 Web canonical transcript
为准。若回复 `Feishu user ID is unavailable`，检查 Gateway 是否传入 `SourceChannel=feishu`
和非空 `ExternalUserId`，禁止从用户正文或客户端参数补造身份。

如果 endpoint 返回 HTTP 400 且 `code=9499`，先逐项对照官方 SDK 的 discovery
请求：JSON 字段必须精确为 `AppID`/`AppSecret`（不能让 `JsonContent` 的 Web 默认策略改成
`appID`/`appSecret`），并携带 `locale: zh` 与非空 `User-Agent`。同一凭据能换取 tenant
token，不代表这个 endpoint 请求格式正确。

需要把飞书协议与 Pudding Agent 执行拆开时，运行独立 Echo：

```powershell
# 收到一条文本、原样回复一次，然后退出
dotnet run --project .\Tests\HarnessAgent.Cli -- feishu-echo --once

# 无人操作的连接冒烟；10 秒没有消息时退出码为 2
dotnet run --project .\Tests\HarnessAgent.Cli -- feishu-echo --timeout-seconds 10
```

日志出现 `WebSocket connected` 只证明 endpoint discovery 和 WSS 建连成功。修正的协议序列应先出现
`initial ping sent`，随后收到 `method=0 ... type=pong`。若确认在当前 Echo 运行窗口内发送了一条新消息，
但始终没有 `method=1 ... type=event` 或 `received message=...`，不要将历史旧消息当成新投递；用同一凭证
单独运行官方 SDK 对照（不与 Pudding/Echo 并发建连），并检查飞书开发者后台：应用必须是
企业自建应用、事件订阅方式必须选择“使用长连接接收事件/回调”、必须订阅
`im.message.receive_v1`，相关权限和应用版本必须已经发布到当前租户。官方 SDK 同样收不到时，
不要继续修改 pbbp2 parser；先修复飞书后台配置或确认消息发给了当前 AppId 对应机器人。

数据库诊断时牢记：`chat_execution_commands.reply_projected_at` 只表示 durable connector
delivery 已创建；真正的发送状态在 `message_deliveries.status`。同一飞书 `message_id` 重投时，
Message/Delivery/Conversation acceptance 都应命中稳定幂等身份，不能出现第二个 Agent Turn。

飞书长连接要求事件处理尽快完成。Gateway 只等待 durable acceptance，不等待 Agent 完成；
如果 ACK 仍超时，查 SQLite 锁、事件发布订阅阻塞和 schema 初始化，而不是缩短 Agent 执行时间。

### 11.12.1 飞书长文本进入 Agent 后变成 `[post]`

这不是空消息。先检查 canonical `ChatMessages.Content` 是否为 `[post]`，同时检查其
`metadata_json.feishu_message_type` 是否为 `post`。如果系统日志中的 `Inbound accepted` 正常、
随后 `[AgentExec] ... msgLen=6`，说明故障位于飞书协议内容转换层，不能通过修改 Prompt、Context
Pipeline 或 LLM 修复。

飞书普通文本的 `content` 是 `{"text":"..."}`；富文本 `post` 则是
`title + content/content_v2` 的段落和元素数组。当前 `MessageMapper.ExtractText` 应调用
`FeishuPostContentConverter`，先转 Markdown，再对未知结构提取纯文本。若代码仍直接把
`FeishuTextContent` 用于所有消息类型，就会在找不到顶层 `text` 后回退成 `[post]`。

聚焦回归：

```powershell
dotnet test .\Tests\HarnessAgent.Core.Tests\HarnessAgent.Core.Tests.csproj --no-restore `
  --filter "FullyQualifiedName~ProtobufFrameTests"
dotnet test .\Tests\PuddingAgent.IntegrationTests\PuddingAgent.IntegrationTests.csproj --no-restore `
  --filter "FullyQualifiedName~FeishuInboundPostTests"
```

第一组锁定直接/locale 包装、`content_v2` 优先、Markdown 与畸形 JSON 降级；第二组锁定转换后的
正文和 `feishu_message_type=post` metadata 进入 Gateway envelope，且纯富文本不触发资源下载。

### 11.13 飞书流式卡片未出现、停止更新或重复回复

先确认渠道 manifest 的 `feishu.streamingRepliesEnabled` 没有显式关闭，并确认飞书应用已开通
CardKit 卡片创建、更新以及机器人消息回复权限。流式投影只消费已经提交的
`message.content.appended`；浏览器 SSE 有 delta 但飞书没有卡片时，按以下日志顺序定位：

```powershell
rg -n "\[FeishuStream\]|\[ConnectorDelivery\]" .\tmp\dev\backend.out.log
```

| 最后证据 | 结论 |
|---|---|
| 没有 `Projection created`，但 Command 已很快 succeeded | 短回复可能在 stream worker 建立资源前完成；普通文本终态是允许的竞争结果 |
| `Projection created` 后反复 operation failed | 查 CardKit 权限、tenant token、connector running 状态和飞书返回码；5 次失败后应转普通文本兜底 |
| 有 `Card published`、没有 `Content projected` | 查该 Command 的 committed `message.content.appended` 事件，不要从尚未提交的 Runtime token 排查 |
| `Content projected` 后卡片停止 | 查 `pending_event_sequence`、`operation_sequence` 与 `last_error`；重试必须复用 sequence/uuid |
| 有 `Final delivery projected`、没有 `ConnectorDelivery Delivered` | 终态已进入 durable egress；只查 `message_deliveries` 的 retry/dead-letter 和 CardKit final API，不重跑 Agent |
| 同时出现卡片和第二条终态文本 | 查 projection 是否在 terminal 前错误进入 `failed`，以及普通 projector 是否看到了同一 `command_id + connector_id` |

数据库只查看状态与游标，不输出消息正文或飞书身份：

```powershell
sqlite3 D:\data\databases\pudding_platform.db "SELECT command_id,status,operation_sequence,last_event_sequence,pending_event_sequence,attempt_count,last_error FROM connector_stream_projections ORDER BY updated_at DESC LIMIT 20;"
```

正常生命周期是 `starting → resource_created → active → finalizing → completed`。`active` 阶段的
增量更新是可恢复展示投影；最终正文仍来自 terminal event，并通过稳定的 Connector delivery 更新
同一张卡、关闭 `streaming_mode`。因此 `reply_projected_at` 已设置不代表卡片已完成，最终仍以
`message_deliveries.status=delivered` 和 projection `completed` 为准。

若 `status=finalizing` 且 `attempt_count` 持续增长，尤其 `last_error` 是 CardKit
`300309 streaming mode is closed`，说明终态卡片已经不可继续更新。任一阶段累计 5 次失败后都应
进入 projection `failed`，由 `ConversationReplyProjectionWorker` 根据同一 committed terminal
event 投递普通文本兜底；不能让 `finalizing` 绕过重试上限形成无限循环。

网页只显示用户气泡、刷新后仍像“卡住”时，先检查 Turn 是否其实已经失败：

```powershell
sqlite3 D:\data\databases\pudding_platform.db "SELECT command_id,turn_id,status,terminal_sequence,last_error FROM chat_execution_commands ORDER BY id DESC LIMIT 10;"
sqlite3 D:\data\databases\pudding_platform.db "SELECT conversation_id,turn_id,sequence,type,payload FROM conversation_events ORDER BY sequence DESC LIMIT 10;"
```

- 若已有 `turn.failed`，前端 bootstrap 的 `turns` 必须把该 Turn 恢复成持久错误卡片，并显示
  `errorCode + errorMessage`；只推进事件 cursor、忽略 Turn 快照会制造假性“卡住”。
- 若失败 Command 来自飞书，CardKit 终态或普通文本兜底也必须使用同一 terminal event；不得
  重新调用 Agent 或临时改用其他模型。
- 若没有 terminal event，再按 Worker lease、Provider 请求和 watchdog 链路继续排查；此时才是
  真正的执行未收口。

### 11.14 飞书图片在 Agent 中仍显示 `[image]`

正确链路是：

```text
Feishu image event -> image_key -> authenticated message resource download
                   -> workspace vision-artifacts -> visionArtifactIds metadata
                   -> canonical Web bubble + native vision or forced visual observation
                   -> grounded Agent answer + Feishu/Web projection
```

先确认当前启动周期出现以下顺序：

```powershell
rg -n "Image materialized|Inbound accepted|Gateway ingress accepted|VisionArtifact|VisualObservation" `
  .\tmp\dev\backend.out.log
Get-ChildItem D:\data\workspaces\default\vision-artifacts `
  -Filter "vision-*" | Sort-Object LastWriteTime -Descending | Select-Object -First 6
```

| 最后证据 | 结论 |
|---|---|
| WS 有 `message_type=image`，没有 `Image materialized` | 查 `content.image_key`、消息资源读取权限和 OpenAPI HTTP 状态 |
| 下载报资源超过 50 MiB | 当前安全上限拒绝该资源；不要改成无界 `ReadAsByteArrayAsync` |
| 报 unsupported MIME/signature | 当前只接收 JPEG/PNG/WebP；响应 MIME 不能代替文件签名校验 |
| 有 `Image materialized`，没有 `Inbound accepted` | artifact 已落盘，但 Gateway durable acceptance 失败；飞书应收到非 200 并重投 |
| Web 正文是图片提示但无图片 | 查 RoomMessage metadata 是否仍含 `visionArtifactId(s)`，再查 artifact GET；不要把二进制/base64 写进 SQLite |
| 有 `Image materialized`，没有 `VisualObservation` | 先确认该消息已创建 Agent Turn；若主模型是原生视觉，应有 `Native vision route`，否则检查 Coordinator DI/旧 DLL |
| 有 `Analyze`，没有 `Completed` | 查视觉 capability 路由和该 Provider 调用错误；系统应阻断主 Agent，不能继续猜图 |
| 有 `Completed`，主 Agent 仍识别错误 | 核对视觉观察本身；观察正确则查本轮组装上下文，观察错误则用同一 artifact 调 `image_reader` 定向复查并检查视觉模型质量 |
| Agent 仍声称只看到 `[image]` | 通常是后端仍加载旧 DLL，或消息发生在重启前；确认 PID/启动时间后发送一张全新的图片 |

同一 `connectorId + message_id + image_key` 会生成相同 artifact ID。飞书重投后文件数量不应增加；
如果已有 `Image materialized` 日志但同一消息不断下载，检查 `D:\data\workspaces\<workspace>\vision-artifacts`
下对应 `.json` 与图片文件是否成对存在。日志只记录 message/artifact 身份，不记录图片正文、`image_key`
或飞书用户 ID。

### 11.15 MCP Server 连接成功但 Agent 看不到或无法调用工具

先把 MCP 协议、Workspace 注册和 Agent 权限三层拆开：

```powershell
# 不依赖 Pudding 数据库，验证官方 SDK 的严格 Streamable HTTP 生命周期
dotnet run --project .\Tests\Mcp.Cli\Mcp.Cli.csproj

# 真实启动 codex mcp-server，验证 tools/list + 只读 tools/call（会调用 Codex）
dotnet run --project .\Tests\Mcp.Cli\Mcp.Cli.csproj -- --codex-smoke

# 独立 Service + fake Codex：第一个 MCP Client 断开后由第二个 Client 按 taskId 取结果
python .\TestScripts\codex_service_smoke.py

# 通过当前常驻 Service 调用真实 Codex，并验证 Client 断线恢复
dotnet run --project .\Tests\Mcp.Cli\Mcp.Cli.csproj -- `
  --codex-service-real-smoke http://127.0.0.1:5100/mcp

# 验证 Service 中的真实 Codex 能在 Yolo 权限下启动 PowerShell 命令
dotnet run --project .\Tests\Mcp.Cli\Mcp.Cli.csproj -- `
  --codex-service-yolo-smoke http://127.0.0.1:5100/mcp

python .\dev-up.py --status
Invoke-WebRequest http://127.0.0.1:5100/health

# 查询指定 Workspace Skill 的内存运行状态
# GET /api/workspaces/<workspaceId>/skills/<skillId>/runtime-status

rg -n "\[MCP\]|tool.execution" .\tmp\dev\backend.out.log
```

| 最后证据 | 结论 |
|---|---|
| CLI 不是 5/5 | 先查 `initialize` 字段、Accept Header、Session/Protocol Header、分页或 DELETE；不要从 Agent Prompt 排查 |
| 状态为 `Unavailable` 且配置错误 | `configJson` 是严格 JSON；本地地址需显式 `allowPrivateNetwork=true`，公网必须 HTTPS |
| 日志有 `Connection failed` | 查 Endpoint、DNS/SSRF 策略、TLS、KeyVault `bearerTokenSecretId` 和远端 Server 日志；禁止把 Token 写入 Skill 配置或日志 |
| stdio 状态为 `Unavailable` | 查 `command` 是否为直接可执行文件/裸命令、`arguments` 是否逐项配置、绝对 `workingDirectory` 是否存在；不要把整条 shell 命令塞进 `command` |
| WindowsApps 下的 Codex 报 Access denied | Codex Desktop 包内二进制不保证后台进程可执行；安装官方 npm CLI，并在 Skill 中指向用户 npm 目录下的绝对 `codex.cmd` |
| `--codex-smoke` 能发现工具但调用失败 | 先用同一服务账户执行 `codex --version`，再查用户目录下 Codex 登录状态；stdio 只继承 SDK OS/runtime 环境白名单，不会继承任意 Token、代理变量或自定义 `CODEX_HOME` |
| Backend 重启时 Codex 任务中断 | Codex Skill 不应再是 stdio；确认 Endpoint 为 `http://127.0.0.1:5100/mcp`，并确认进程树是 `dev-up → Codex Service → codex`，不是 `Pudding → codex` |
| `Codex MCP : stopped` | 查 `tmp/dev/codex-service.err.log`、5100 端口和 `Source/PuddingCodexService` 构建；不要回退为 Pudding 子进程掩盖故障 |
| `codex_task_start` 返回后一直 Queued | 查 Service BackgroundService 是否启动、`D:\data\codex-service\tasks` 文件状态和 Service 日志 |
| Codex 仍报告 Windows sandbox/helper 错误 | 查新 Task JSON 必须为 `sandbox: danger-full-access`、`approvalPolicy: never`；`codex_task_start` schema 不应再暴露这两个调用参数。旧 Task 不会原地升级 |
| Task 为 Running，Pudding 重启后查不到 | 必须复用 Pudding 级 `taskId` 调 `codex_task_get`；不要用 Codex `threadId` 代替 |
| Agent 卡片长期显示 Running，但 Task JSON 已 Completed | 这是会话中的一次性文本快照，不是任务事实源；检查是否误用了 `codex_task_start`。Pudding 自修复必须调用 `pudding_self_heal_start`，并以 Task 中的 `restartRequestId` 查询监督器结果 |
| Codex 直接杀死 Backend，随后出现较长 502 | 下发链路错误；Task Prompt 不得包含 `taskkill`、`dotnet run PuddingRuntime`、`dev-up --restart` 或 `/yolo`。检查 Task 是否 `restartPuddingOnCompletion=true`，并确认 `tmp/dev/backend.restart.request.json` 由 Service 生成 |
| `pudding_build_restart` 拒绝 | 只有 Completed Task 可触发；同时只允许一个 `backend.restart.request.json` 待处理 |
| restart 结果为 `build_failed` | staging build 失败，旧 Backend 应仍在线；查 `tmp/dev/backend.err.log`，不要先杀 Backend 再排查编译 |
| restart 为 `restarted` 但页面 502 | 对比 `backend.restart.result.<requestId>.json` 的 PID，并查 staging DLL 启动日志与 `/health`；Codex Service PID 应保持不变 |
| stdio JSON 解析失败 | Server stdout 必须只有逐行 JSON-RPC；诊断输出写 stderr。Pudding 只在 Debug 日志记录有界 stderr 行 |
| 有 `Tools refreshed`，目录没有工具 | 工具目录查询必须携带正确 `workspaceId`；无 Workspace 的全局目录故意不暴露动态 MCP 工具 |
| 目录有工具，Agent Prompt 没有 | 查 `AgentRuntimeProfileResolver` 的 Workspace ID、Capability Policy 和 LLM schema；MCP schema 应来自 `RawJsonSchema` |
| Agent 调用返回 authorization denied | MCP 工具固定为 High 风险并要求运行时审批，这是预期安全边界；不要依据远端 `readOnlyHint` 降级 |
| 返回 403 workspace mismatch | Tool 快照或执行上下文跨 Workspace；禁止移除二次 Workspace 校验 |
| `tools/list_changed` 后仍是旧清单 | 查通知名称、Session 是否仍存活和 `Tools refreshed`；失败应 fail-closed，不继续使用陈旧定义 |

HTTP 日志中的 Endpoint 只保留 scheme/host/path；stdio 日志只记录可执行文件名。两者都不应包含 query、userinfo、Bearer Token、工具参数或结果正文。独立 Codex Service 正常时应发现 `codex_task_start/get/reply/cancel`、`pudding_self_heal_start` 与 `pudding_build_restart/restart_get` 七个工具。Pudding 与 Service 之间使用 `taskId`；内部 `structuredContent.threadId` 只由 Service 保存并用于后续 Codex reply。

### 11.16 飞书 `/compact` 被拦截但未执行，或重复压缩

正确链路是：

```text
Feishu /compact -> MessageGatewayIngress whitelist + stable IDs
                -> ISystemCommandHandler
                -> IRequestCompactionHandler
                -> context.compaction.started/completed
                -> ICompactionSessionSuccessor rebind main session
                -> durable Connector reply + Web lifecycle event
```

先看当前启动周期的边界日志：

```powershell
rg -n "\[MessageGateway\] Command intercepted|\[SystemCommand\]|\[Compact\]|\[CompactSuccessor\]" `
  .\tmp\dev\backend.out.log .\tmp\dev\backend.err.log
```

| 最后证据 | 结论 |
|---|---|
| 回复 `not implemented yet` | 后端仍加载旧 `SystemCommandHandler`；重新构建并重启后发送一条全新的飞书消息 |
| 回复 `Permission denied` | `/compact` 是特权指令；检查当前 channel manifest 的 `feishu.privilegedUserOpenIds` 是否精确包含 sender `open_id` |
| 有 `Command intercepted`，没有 `[Compact]` | 查系统命令 DI 和解析；不得把 `/compact` 改成 Agent prompt 绕过 |
| 有 `[Compact] failed` | 按同一 `compactionId` 查 Agent Profile、摘要模型、事件存储与后继 Session 创建错误 |
| 有 `[Compact] completed`，没有 `[CompactSuccessor] rebound` | 后继会话事务未完整收敛；检查 Controller SessionRepository、Agent manifest 写入与 redirect |
| 成功后下一条飞书消息仍进旧会话 | 对比 Agent manifest `mainSessionId` 与 Controller canonical Main；不能只依赖内存 redirect |
| 同一飞书 `message_id` 触发第二次压缩 | 幂等查询被错误限定到当前 Conversation；必须用稳定 `clientRequestId + responseMessageId` 跨后继会话命中旧结果 |

针对性回归：

```powershell
dotnet test .\Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore `
  --filter "FullyQualifiedName~SystemCommandHandlerTests"
dotnet test .\Tests\PuddingAgent.IntegrationTests\PuddingAgent.IntegrationTests.csproj --no-restore `
  --filter "FullyQualifiedName~FeishuCommandInterceptionTests"
```

### 11.17 Web/飞书 `/status` 被送给 Agent、显示不完整或仍回复未实现

`/status` 的正确链路只有一套：

```text
Web slash classifier ─→ system-command endpoint ───────────────┐
Feishu Gateway slash classifier ─→ ISystemCommandHandler ─────┤
                                                               ▼
                         ISystemStatusSnapshotProvider
                           → Agent Profile + Provider/Model
                           → ContextHealth remaining/effective
                           → Session/sub-agent + Runtime state
                         → canonical transcript
                         → Web system Turn / Feishu Connector reply
                         ╳ Agent Turn / ChatExecutionCommand
```

正常回复至少包含 Agent、Session、剩余/有效上下文、已用比例、模型、Runtime mode、运行中子代理数
和 capability 数；部分数据源失败时应出现 `Warnings`，而不是让 Agent 根据聊天内容猜状态。

先查当前启动周期：

```powershell
rg -n "\[MessageGateway\] Command intercepted|\[SystemCommand\]|\[SystemStatus\]" `
  .\tmp\dev\backend.out.log .\tmp\dev\backend.err.log
```

| 现象 | 结论 |
|---|---|
| Web 创建了普通 Agent Turn | 前端仍只识别 `/yolo` 或未加载新 bundle；检查 `isSystemCommandText` 与浏览器资源版本 |
| 飞书出现 Agent 回复而非系统回复 | Gateway 没有在 Agent delivery 前拦截斜杠文本；检查 `IsGatewayCommand` metadata 和 `ForwardToAgent` |
| 回复 `not implemented yet` | 后端仍加载旧 `SystemCommandHandler`；重新构建/重启后发送新消息 |
| Context 显示 unavailable | 查 Agent provider/model 绑定与模型 `maxContextTokens`，再查 `[SystemStatus] context health unavailable` |
| 重启后 Context 显示 `0 used`，但会话有历史 | provider/snapshot/Memory active messages 均缺失时应回退到最近 500 条 canonical `ChatMessages`，回复中的 source 应为 `canonical_chat_transcript`；否则检查 `ICompactionChatMessageStore.GetRecentForSessionAsync` 和 Conversation ID |
| Session/子代理显示 warning | 查 `ISessionStateManager` 读取异常；Runtime fallback 只用于部分展示，不能代替 canonical 状态 |
| Web/飞书数值不同 | 两端不应自行计算；确认都命中同一 conversation、agent 和 `ISystemStatusSnapshotProvider` |

针对性回归：

```powershell
dotnet test .\Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore `
  --filter "FullyQualifiedName~SystemCommandHandlerTests"
dotnet test .\Tests\PuddingAgent.IntegrationTests\PuddingAgent.IntegrationTests.csproj --no-restore `
  -p:OutDir="$env:TEMP\pudding-status-feishu-tests\" `
  --filter "FullyQualifiedName~FeishuCommandInterceptionTests"
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore `
  -p:OutDir="$env:TEMP\pudding-status-runtime-tests\" `
  --filter "FullyQualifiedName~ContextCompactionContentSummaryTests.GetHealthAsync_UsesCanonicalTranscript"
npm --prefix .\Source\PuddingPlatformAdmin run jest -- --runInBand `
  src/pages/chat/hooks/useChatState.selection.test.tsx
```

### 11.18 `dev-up.py --rebuild --restart --auto-yolo` 只剩 Backend 或启动失败

先确认监督器和四个子进程是否属于同一个启动周期：

```powershell
python .\dev-up.py --status
Get-Content .\data\logs\dev-up-$(Get-Date -Format yyyy-MM-dd).log -Tail 200
rg -n "Full rebuild|exited with code|restarting|YoloSignal|Notification|checkpoint" `
  .\tmp\dev\backend.out.log .\tmp\dev\backend.err.log
```

`--restart` 必须先停止 `tmp/dev/supervisor.pid` 指向的旧监督器，再停止 Backend、Codex MCP、Frontend
和 Proxy。若升级前的监督器还没有 PID 文件，启动器只沿已跟踪子进程反查父命令行，并仅终止明确运行
`dev-up.py` 的父进程。若只停止子进程，旧监督器会把计划内退出误判为崩溃，在新实例执行 full rebuild 时
抢先普通构建并重启 Backend，最终表现为新构建失败或只剩孤立 Backend。

`--rebuild` 成功后，监督器应直接启动 `Source/PuddingAgent/bin/Debug/net10.0/PuddingAgent.dll`，不能再次
触发普通 build。`--auto-yolo` 的成功证据是：

```text
[YoloSignal] Watching <repo>\yolo.signal
[YoloSignal] Activated YOLO mode via file signal
```

消费后仓库根的 `yolo.signal` 应被删除；`checkpoint.json` 即使带 UTF-8 BOM 也应被更新。
`--auto-yolo` 不得调用 Workspace Message API，也不得创建 Agent Turn/Message Fabric delivery；否则聊天页会出现
“重启完成，YOLO 已授权”的交互队列，并无意义地唤醒 Agent。启动确认只看监督器日志、Runtime mode 和 checkpoint。

### 11.19 OpenAI-compatible 模型重复调用工具，最终触发 MaxToolCalls

若页面长时间显示“复杂推理”，先确认是单次 Provider 卡顿，还是模型因为看不到上一轮工具结果而循环。
以下组合能够直接识别工具调用 ID 兼容故障：

```powershell
rg -n "\[ToolInvocation\].*callId= |\[LlmInvocation\] Repaired invalid tool-call history|msgCount=" `
  .\tmp\dev\backend.out.log
```

典型故障证据：

- 同一主 Session 的 `[ToolInvocation]` 持续显示空 `callId=`；
- `incompleteToolRounds` 和 `orphanToolMessages` 每轮增长；
- 后续 `[LlmInvocation]` 的 `msgCount` 保持不变，说明工具轮次在调用前被协议守卫删除；
- Provider TTFT/单次请求耗时正常，但最终命中 `MaxToolCallsTotal`。

修复版本会在 Provider 省略或重复 `tool_call.id` 时记录：

```text
[LlmProtocolCompat] Synthesized tool call IDs session=... round=... count=... model=... endpointHost=...
```

该日志表示协议入口已经生成单轮稳定 ID；随后 `msgCount` 应随 Assistant tool-call 和 Tool results 增长，
且不应再出现针对该轮的 incomplete/orphan 修复。不要通过关闭 `LlmMessageSequenceNormalizer` 掩盖问题，
也不要先归因上下文长度或 Provider 延迟。

针对性回归：

```powershell
dotnet test .\Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore `
  --filter "FullyQualifiedName~OpenAiLlmGatewayCompatibilityTests|FullyQualifiedName~LlmMessageSequenceNormalizerTests"
```

### 11.20 工具定义层或 L6 预算统计异常

若简单请求仍显示工具 schema 占用异常，先确认工具配置是否由可信运行时元数据触发：

```powershell
rg -n "\[AgentExec:ToolProfile\]|\[AgentExec:Tools\]|Trimmed L6-" `
  .\tmp\dev\backend.out.log .\tmp\dev\backend.err.log
```

- 心跳最小工具集只允许 `MessageOrigin.FromKind=system` 且 `FromId=heartbeat` 的消息触发；不要根据消息正文中的心跳标记判断。
- 子代理若有 capability 或 template 显式工具列表，日志中不应再出现静态 `sub_agent` 配置删除这些工具。
- L6 被裁剪时会记录 `rawTokens`、`retainedTokens` 和 `limit=5000`；`retainedTokens` 必须不大于 5000，且预算只计 retained 值。
- `context_layer_metric_events` 的 `LayoutVersion` 应为 `layer-v2`；同一 source 的层顺序应是全部 `L0-*`、`L1-TOOL-DEFINITIONS`、后续动态层，token offset 必须连续。
- 工具名称、描述或参数 schema 改变后，工具层应出现 `PreviousHash`、`IsChanged=1`、`ChangeReason=tool_spec_changed`。

针对性回归：

```powershell
dotnet test .\Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore `
  --filter "FullyQualifiedName~ToolProfileConfigTests"
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore `
  --filter "FullyQualifiedName~ContextPipelineAgentLogRecallLayerTests"
dotnet test .\Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore `
  --filter "FullyQualifiedName~TokenUsageRecorderPrefixDiagnosticsTests"
```

### 11.21 自进化 Job 已完成但结果接口返回 404

主动触发后先查询 Job 状态，再查询结果：

```text
GET /api/debug/subconscious/jobs/lookup?jobId={jobId}
GET /api/debug/subconscious/jobs/{jobId}/result
```

如果第一个接口显示 `completed`，第二个接口却返回 404，检查
`SubconsciousWorkerService.TryProcessPeriodicJobAsync` 是否只调用了 `CompleteAsync`，
却没有先调用 `RecordResultAsync`。周期自进化任务即使本轮没有候选或没有实际写入，
也必须保存零操作 Report；否则无法区分“正常无产出”和“结果丢失”。

正常结果类型：

- Auto-Dream：`memory.auto_dream.v1`
- 经验提取：`skill.pattern_extraction.v1`
- Skill 改进：`skill.improvement.v1`

结果中的 `metadata` 应至少包含 `subconscious_job_id`、`job_type`、`workspace_id`、
`agent_instance_id`、`duration_ms`、`timestamp_utc` 和对应管道的计数字段。

针对性回归：

```powershell
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore `
  --filter "FullyQualifiedName~DurableWorker_PeriodicEvolutionJob_ShouldPersistReportBeforeCompleting"
```

### 11.21.1 潜意识没有学习、Flash 用量异常或历史日志未生成日摘要

先确认宿主加载了真实潜意识链路，而不是只确认 Worker 存活：

```powershell
python .\dev-up.py --status
Invoke-RestMethod http://localhost/health
$log = Get-ChildItem D:\data\logs\system\pudding-*.log |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
rg -n "SessionCompressedMemoryMaintenanceHook subscribed|Resolved Agent role route|SubconsciousWorker" $log.FullName
```

必须出现 `SessionCompressedMemoryMaintenanceHook subscribed event=session.compressed`。若 Debug API
发布 Hook 后一直没有 Job，优先检查主宿主是否注册该 Hosted Service；`EventIngressBridge` 不负责转发
Hook 生命周期事件。若 Wiki Job 返回旧版 F5 dry-run 或 `missing_required_field`，检查主宿主是否注册
`MemoryWikiPageUpdateService` 与 `WikiPageWriteEntry`。

使用诊断脚本逐条验证，不要直接调用 Orchestrator 绕过队列：

```powershell
$agentId = "default.global_general-assistant.6a8"

# 指定日期日志 -> memory/daily/{day}.md；同一源哈希重跑应 skipped=1
python .\Tools\Diagnostics\subconscious_debug.py daily-summary `
  --agent-instance-id $agentId --day 2026-07-30 --timeout-seconds 180

# 成功轨迹 -> Skill，随后基于完整 SKILL.md 改进
python .\Tools\Diagnostics\subconscious_debug.py evolution `
  --agent-instance-id $agentId --action extract_patterns --request-id debug-extract-001 --wait
python .\Tools\Diagnostics\subconscious_debug.py evolution `
  --agent-instance-id $agentId --action improve_skills --request-id debug-improve-001 --wait

# session.compressed -> 持久 Job -> Wiki page update
python .\Tools\Diagnostics\subconscious_debug.py hook-session-compressed `
  --session-id debug-session --agent-id $agentId `
  --source-compaction-id debug-compaction-001 `
  --memory-note "一条可验证的长期事实" --wait
```

正常的 LLM 路由日志应同时包含 `agent`、`role=subconscious`、`provider` 和 `model`。Token 明细应为
`SourceType=subconscious_memory`，并使用实际 workspace；每日摘要的 `SourceId` 前缀是
`llm:daily-summary:`，`SessionId` 是 `daily-summary:{day}`。若角色配置缺失或 registry 无对应模型，
任务应失败或跳过，不能静默退回硬编码 Flash/平台默认配置。

若主会话构造 Context 时出现 `workspace=memory agent=subconscious-memory provider=subconscious
model=subconscious`，通常不是 role-scoped recall judge，而是旧 `MemoryLibraryConvenience.SmartSearchAsync`
歧义分支偷偷启动了无 Agent 身份的后台深度探索。主宿主存在 `ILLMConfigResolver` 时该隐式调用必须
关闭；记忆写入语义去重则可从 `ExperiencePackage.AgentInstanceId` 解析 `subconscious` 角色。

经验提取显示 `No candidates found` 时，不要只看最近 N 条成功 Command。轨迹源应在有界窗口内先
读取成功 Command，再过滤至少两步、配对完整且 `exitCode=0` 的工具链，最后截取所需候选；否则
近期的纯聊天/单工具任务会长期饿死较早的黄金路径。

P1 起，自进化是无人审批的自值守流程。经验提取会从现有 Skill 的 `source-turn:*` 标签或旧版
`- Turn:` 证据中构造已处理集合；日志出现 `Suppressed N already-processed verified trajectories` 表示
重复轨迹已在调用 Flash 前被抑制。新候选的 `skill-admission` 只接受高置信度 `create/merge/skip`，
无效 JSON、目标不存在或低置信度自动记为 `deferred_count`，本轮不写入。

`improve_skills` 会先运行 `skill-consolidation`。只有 Flash 置信度至少 0.92，且工具指纹完全一致、
名称/描述相似度与来源证据门禁通过，才会更新规范 Skill 并把重复项设为 `Enabled=false`。结果中的
`consolidated_count`、`disabled_duplicate_skill_ids` 可用于审计；被禁用 Skill 的 manifest 应包含
`superseded` 与 `superseded-by:{canonicalSkillId}`，文件仍然存在。若两个 Skill 只共享工具但意图不同，
它们应保持启用。恢复时通过 `AgentSkillFileService.SetEnabledAsync` 或对应管理 API 重新启用，不要重建文件。

正常完成一次去重/评估后，启用 Skill 的 tags 会出现与 manifest 当前版本一致的
`dedup-reviewed:{version}` / `self-evaluated:{version}`。前者只写给已安全合并或被 Flash 明确判为 distinct
的 Skill，不确定项不写水位并在后续周期重试。下一周期同版本应直接跳过对应 Flash 调用；
若 marker 版本落后，说明 Skill 在上次审查后被创建、合并或改写，应自动重新审查。不要为了让任务显示为
“有操作”而移除水位，否则会恢复每 4 小时重复评估的 Token 空转。

若进程在 LLM 调用期间重启，Job 可能暂时保持 `processing`。租约未过期时这是正常现象；租约过期后
`GetStatsAsync` 必须把它视为 pending backlog，`Processing`/workspace/session 并发计数只包含仍有效
的租约。否则 `MaxGlobalConcurrentJobs=1` 会让过期 Job 自己占住门禁，Worker 永久无法重新租用。

针对性回归：

```powershell
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore `
  --filter "FullyQualifiedName~MemoryLlmInvocationClientUsageTests|FullyQualifiedName~SubconsciousRecallPipelineTests|FullyQualifiedName~SubconsciousWorkerServiceTests|FullyQualifiedName~ConversationSkillEvolutionTrajectorySourceTests"
dotnet test .\Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore `
  --filter "FullyQualifiedName~AgentRuntimeProfileResolverTests|FullyQualifiedName~AgentDailySummaryServiceTests|FullyQualifiedName~SubconsciousDebugApiControllerTests"
dotnet test .\Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore `
  --filter "FullyQualifiedName~SubconsciousJobQueueTests|FullyQualifiedName~SkillEvolutionDeduplicationServiceTests"
```

### 11.22 飞书显式语音未出现，或原始 `voice` 围栏不符合预期

飞书 TTS 是独立出站投递，不应通过重跑 Agent 修复。先按以下链路定位：

```text
committed succeeded terminal with voice fence
  -> FeishuTtsProjection (typed audio delivery)
  -> ConnectorDeliveryDispatcher
  -> IVoiceSynthesisService / configured ITtsProvider
  -> ManagedOggOpusTranscoder
  -> Feishu file upload
  -> Feishu audio reply
```

普通终态回复不会自动生成语音。确认 Agent 最终回复包含完整、非空、独占行的 `voice` 围栏，
或成功调用了 `send_voice`；同时确认渠道 manifest 的
`feishu.ttsRepliesEnabled=true`，`ttsVoice` 属于当前默认 TTS 模型支持的音色，并在修改后
重启 Connector。V1 会把 Agent 的完整 Markdown（包括 `voice` 围栏）先显示在 CardKit/文字回复中，
随后追加语音；这不是标记泄露。失败时按同一个 audio message/delivery ID 查询：

如果 Agent 完全没有按协议输出，先验证实际 ContextPipeline 的系统提示已经包含 canonical 规则，
且旧的 `voice.enabled` 提示已移除：

```powershell
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore `
  --filter "FullyQualifiedName~ContextPipelineLayerTests.AssembleAsync_Includes_Canonical_Feishu_Voice_Protocol"
rg -n "Voice output protocol|voice\.enabled|voice\.tts_text" `
  .\Source\PuddingRuntime\Services\SystemPromptBuilder.cs `
  .\Source\PuddingRuntime\Services\ContextPipeline.cs
```

系统提示要求从当前 `pudding-message` 的 `channel_type=feishu` metadata 判断渠道；该字段由
`AgentExecutionService.BuildOriginMetadata` 从受信任的 Message Origin 写入。不要让 Agent
根据用户正文猜测渠道，也不要把飞书目标 ID 写入提示词。

```powershell
rg -n "\[SendVoiceTool\]|\[FeishuVoiceDebug\]|\[VoiceTts\]|\[FeishuTts\]|voiceDirective=|\[ConnectorDelivery\].*(Retrying|Dead-lettered|Delivered)|Feishu audio" `
  .\tmp\dev\backend.out.log .\tmp\dev\backend.err.log
```

| 证据 | 结论 |
|---|---|
| 普通回复没有 `ttsMessage=` | 正常；只有显式围栏或 `send_voice` 才生成语音 |
| 围栏终态日志没有 `voiceDirective=True` | 围栏为空、未闭合、不是独占行，或 Command 非 succeeded |
| 围栏原文显示后没有追加语音 | 渠道 TTS 未开启、语音正文超过 1000 字符，或 audio delivery 仍在 retry/dead-letter |
| CardKit 显示 `voice` 围栏原文 | V1 预期行为；运行 `FakeFeishuStreamingCard_ProjectsDeltasAndFinalizesThroughDurableDelivery` 可验证原样投影 |
| `send_voice` 返回 current turn/route 错误 | 工具不在 Feishu main Conversation Turn，或受信任 gateway metadata 不完整 |
| `send_voice` 返回 streaming text started | 文字卡片已经开始；改为在最终回复使用混合 `voice` 围栏 |
| `TTS provider ... not found or disabled` | `D:\data\config\voice\providers.json` 默认 Provider/模型不可用 |
| `[VoiceTts] Audio materialized` 后转码失败 | Provider 返回的实际文件不是请求声明的 WAV，或 WAV 声道/内容不受支持 |
| `[FeishuTts] durationMs` 显示数小时但语音实际很短 | Provider 使用未知长度的流式 WAV 头；时长必须按实际读取的 PCM 样本数计算，并运行 `TranscodeAsync_StreamingWavLengthSentinel_UsesActualSamplesForDuration` |
| `[FeishuTts] Audio prepared` 后 `audio upload failed` | 查飞书文件上传权限、限流与 OpenAPI code/msg |
| 上传成功后 `audio send failed` | 查 reply API 权限、原 `message_id` 和 `file_key`；不得重新合成来掩盖回复失败 |
| 文本 delivery 为 delivered、audio delivery 为 retrying | 正常的故障隔离；Agent Command 应保持 succeeded |

不经过 LLM 验证真实飞书语音链路时，先登录取得管理员 JWT，再预览频道最近的可信入站路由：

```powershell
$baseUri = "http://localhost"
$login = Invoke-RestMethod -Method Post -Uri "$baseUri/api/login/account" `
  -ContentType "application/json" `
  -Body (@{ username = "admin"; password = "Admin@123"; type = "account" } | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($login.token)" }
$channelId = "feishu-default.global_general-assistant.6a8"
Invoke-RestMethod -Headers $headers `
  -Uri "$baseUri/api/workspaces/default/debug/feishu-voice/channels/$channelId/route"
```

路由存在后，显式确认真实发送，并按返回的 `messageId` 查询状态：

```powershell
$request = @{
  text = "Pudding 飞书语音链路调试成功。"
  confirmSend = $true
  idempotencyKey = "manual-voice-debug-001"
} | ConvertTo-Json
$queued = Invoke-RestMethod -Method Post -Headers $headers `
  -ContentType "application/json" -Body $request `
  -Uri "$baseUri/api/workspaces/default/debug/feishu-voice/channels/$channelId/send"
Invoke-RestMethod -Headers $headers `
  -Uri "$baseUri/api/workspaces/default/debug/feishu-voice/messages/$($queued.messageId)"
```

调试 API 不能接收 `chat_id` 或飞书 `message_id`，只会复用指定频道最近的可信 Gateway 入站
Command。route 返回 409 时，先在目标飞书会话给机器人发一条消息；不要通过修改数据库或手填目标绕过。

针对性回归：

```powershell
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore `
  --filter "FullyQualifiedName~ManagedOggOpusTranscoderTests|FullyQualifiedName~DashScopeTtsProviderTests"
dotnet test .\Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore `
  --filter "FullyQualifiedName~VoiceSynthesisServiceTests|FullyQualifiedName~ConversationReplyProjectionWorkerTests|FullyQualifiedName~ChannelConfigurationFileServiceTests|FullyQualifiedName~FeishuVoiceDebugControllerTests"
dotnet test .\Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore `
  --filter "FullyQualifiedName~AgentReplyVoiceDirectiveTests"
dotnet test .\Tests\PuddingAgent.IntegrationTests\PuddingAgent.IntegrationTests.csproj --no-restore `
  --filter "FullyQualifiedName~SendVoiceToolTests|FullyQualifiedName~FakeFeishuStreamingCard_ProjectsDeltasAndFinalizesThroughDurableDelivery"
dotnet test .\Tests\HarnessAgent.Core.Tests\HarnessAgent.Core.Tests.csproj --no-restore `
  --filter "FullyQualifiedName~FeishuClientReplyTests"
```

### 11.23 飞书入站语音无法识别、Agent 假装听见或 `asr` 路径被拒绝

先按同一 `connectorId/messageId/audioArtifactId/runId` 区分物化、分流和识别三段：

```text
Feishu message_type=audio + file_key
  -> GET message resource type=file
  -> ManagedOggOpusTranscoder (Ogg/Opus -> 16 kHz mono PCM WAV)
  -> AudioArtifactStorageService
  -> MessageGateway / canonical user message
  -> ExecutionRunCoordinator
     -> current Provider+Model has audio tag: native input_audio
     -> no audio tag: attached-audio path notice -> asr tool -> configured ASR provider
```

先查脱敏日志，不打印音频、转写正文或飞书用户 ID：

```powershell
rg -n "\[Feishu\] Audio materialized|\[AudioArtifact\] Stored|\[MessageGateway\] Ingress accepted|\[VoiceAsr\]|\[AsrTool\]" `
  D:\data\logs
```

Audio Artifact 位于
`D:\data\workspaces\<workspaceId>\audio-artifacts\audio-*.wav/.json`。WAV 应为 16 kHz、
单声道、16-bit PCM；不要把飞书下载到的 Ogg/Opus 政名为 `.wav`。同一
`connectorId + message_id + file_key` 重投应复用同一 Artifact，资源请求只发生一次。

分流只看 `D:\data\config\llm.providers.json` 中当前冻结的精确 Provider+Model
`capabilityTags`。带 `audio` 时请求应包含 `input_audio`，不会自动出现 `[AsrTool]`；没有标签时
Agent 必须先调用 `asr`。不要只给同名的其它 Provider 模型加标签，也不要因 ASR 配置存在就误判
主模型具有原生听觉。

文本模型路径下，`D:\data\config\voice\providers.json` 必须配置启用的
`defaultAsrProviderId/defaultAsrModelId`。`asr` 只接受平台 notice 中当前 Workspace Artifact 的
精确绝对路径；相对路径、手写路径、其它 Workspace 文件和非 PCM WAV 被拒绝是安全边界，不应放宽。

| 现象 | 判断 |
|---|---|
| 没有 `[Feishu] Audio materialized` | `file_key` 解析、`type=file` 下载、资源格式或托管 Opus 解码失败；事件应非 200 让飞书重投 |
| 有 Artifact、没有 `Inbound accepted` | Gateway durable acceptance 失败；不要手工重复投递 Agent |
| 文本模型没有 `[AsrTool]` 却描述录音 | 检查 Audio input protocol 是否进入最终 system prompt，以及 Agent 是否收到 `[Attached audio notice]` |
| `path is not an authorized audio artifact` | 模型没有原样使用 notice 路径，或 Artifact 不属于当前 Workspace；不要开放任意文件读取 |
| `No default ASR provider configured` | 修复 `voice/providers.json` 默认 ASR Provider/模型 |
| `[VoiceAsr]` 后无 `[AsrTool]` 成功 | Provider 返回空转写、网络错误或工具调用失败；Agent 必须如实说明，不能猜测 |
| 带 `audio` 标签仍走 `asr` | 当前执行冻结的 Provider/Model 与加标签对象不一致，或服务尚未重启加载新配置 |

聚焦验收：

```powershell
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore `
  --filter "Name=TranscodeAsync_OggOpus_ProducesMono16KhzPcmWav|Name=ChatAsync_TextOnlyModel_DoesNotSerializeHistoricalAudioArtifact|Name=ChatAsync_AudioModel_SerializesResolvedAudioArtifact|Name=AssembleAsync_Includes_Canonical_Feishu_Voice_Protocol"
dotnet test .\Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore `
  --filter "FullyQualifiedName~AudioArtifactStorageServiceTests|FullyQualifiedName~AudioTranscriptionServiceTests|Name=BuildAudioMessageTextAsync_RoutesByExactPrimaryModelCapability"
dotnet test .\Source\PuddingWebApiTests\PuddingWebApiTests.csproj --no-restore `
  --filter "FullyQualifiedName~AsrToolTests"
dotnet test .\Tests\PuddingAgent.IntegrationTests\PuddingAgent.IntegrationTests.csproj --no-restore `
  --filter "FullyQualifiedName~FeishuInboundAudioTests|FullyQualifiedName~FeishuInboundImageTests"
```

运行中的 PuddingAgent 锁住默认输出时，不要据此判断测试失败；使用独立 `OutDir` 构建测试项目，
再对临时目录中的测试 DLL 执行 `dotnet vstest`。

### 11.24 Agent 看不到图片生成工具，或飞书没有收到生成图片

生成和投递是两段独立链路：

```text
generate_image
  -> configured IImageGenerationProvider
  -> Ark /images/generations
  -> immediate HTTPS download
  -> workspace Vision Artifact
send_image
  -> trusted current Feishu Command route
  -> typed vision_image Message Fabric delivery
  -> Feishu image upload
  -> msg_type=image reply
```

先确认 `D:\data\config\llm.providers.json` 顶层 `imageGeneration` 指向启用的
Provider/Model。普通/组图默认模型 `doubao-seedream-5-0-260128` 应带
`sequential-image-generation`，精细编辑模型 `doubao-seedream-5-0-pro-260628` 应带
`image-editing`，二者都必须带 `image-generation`。不要在日志、命令行历史或调试响应中输出
API Key。

如果模型完全看不到工具，检查当前 Agent 实例 manifest 的 `allowedToolIds` 同时包含
`cap-doubao-search`、`cap-import-image`、`cap-generate-image` 与 `cap-send-image`；只改 preset
不会自动改变已经实例化的 Agent。重启后用 `GET /api/capabilities` 验证 `doubao_search`、
`import_image`、`generate_image`、`send_image` 已注册，再检查最终 system prompt
只包含一份 `Image generation and Feishu delivery protocol:`。

```powershell
rg -n "\[RemoteImageImport\]|\[ImportImageTool\]|\[ImageDirective\]|\[ImageGeneration:Ark\]|\[ImageGeneration\]|\[GenerateImageTool\]|\[SendImageTool\]|\[FeishuImage\]|\[ConnectorDelivery\].*(Retrying|Dead-lettered|Delivered)|Feishu image" `
  D:\data\logs
```

| 证据 | 结论 |
|---|---|
| `No default image generation provider/model is configured` | `imageGeneration` 绑定缺失或服务未重启 |
| `not an enabled image generation model` | Provider/Model ID 不匹配、已禁用或缺少 capability |
| `No configured image generation model provides capability 'image-editing'` | `mode=precision` 但未配置 Pro/其它精细编辑模型 |
| `Reference image artifact ... was not found` | Agent 构造了 ID，或没有复制 Attached image notice 中当前 Workspace 的精确 `vision-*` ID |
| Pro 报组图/web search 不支持 | 精细编辑使用 `mode=precision` 且 `imageCount=1`；连贯组图/联网内容改用 `mode=sequence/default` |
| Pro 返回 ``sequential_image_generation is not supported`` | Pro 请求体不应携带 `sequential_image_generation`（即使值为 `disabled`）；该字段及 options 仅在支持组图的模型请求中发送 |
| Lite 报 fast/尺寸不支持 | Lite 只用 standard，档位为 2K/3K/4K；Pro 档位为 1K/2K |
| Ark HTTP 4xx | 检查模型 ID、账号权限、配额与请求参数；不要记录 Bearer Key |
| Ark 成功但 Artifact 未创建 | 临时 URL 下载失败、非 HTTPS、格式不是 JPEG/PNG/WebP 或超过 50 MiB |
| `import_image` 拒绝 URL | 只接受公共 HTTPS；检查内嵌凭据、跳转后的协议、私网/DNS rebinding 或非图片响应 |
| `[ImageDirective] Rejected` | `image` fence 不是当前 Workspace 的精确 `vision-*`/localPath，或引用的 Artifact 不存在 |
| `send_image ... current turn/Feishu-originated` | 工具不在 Feishu main Conversation Turn，或可信 Gateway metadata 不完整 |
| `[FeishuImage] Prepared delivery copy` | 原图超过飞书 10 MiB 上传边界，已在 C# 层生成 JPEG 投递副本；原始 Artifact 未改动 |
| upload 失败 | 检查飞书图片上传权限、格式、投递副本是否仍超过 10 MiB 与 OpenAPI code/msg |
| upload 成功、reply 失败 | 检查原消息是否仍可回复；只重试 delivery，不要重新生成 |
| delivery 为 retrying、Artifact 存在 | 预期的故障隔离；Agent Command 和已付费生成结果应保持不变 |

不经过 Agent 验证真实 Pudding → Ark → Artifact → 飞书链路时，登录管理员 API，先预览可信路由：

```powershell
$baseUri = "http://localhost"
$login = Invoke-RestMethod -Method Post -Uri "$baseUri/api/login/account" `
  -ContentType "application/json" `
  -Body (@{ username = "admin"; password = "Admin@123"; type = "account" } | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($login.token)" }
$channelId = "feishu-default.global_general-assistant.6a8"
Invoke-RestMethod -Headers $headers `
  -Uri "$baseUri/api/workspaces/default/debug/feishu-image/channels/$channelId/route"
```

路由存在后显式确认一次真实付费生成和发送，并轮询返回的 `messageId`：

```powershell
$request = @{
  prompt = "一只戴黄色围巾的布丁猫，简洁插画，浅色背景"
  mode = "default"
  size = "2K"
  outputFormat = "png"
  optimizePromptMode = "standard"
  imageCount = 1
  watermark = $true
  confirmSend = $true
} | ConvertTo-Json
$queued = Invoke-RestMethod -Method Post -Headers $headers `
  -ContentType "application/json" -Body $request `
  -Uri "$baseUri/api/workspaces/default/debug/feishu-image/channels/$channelId/generate-and-send"
Invoke-RestMethod -Headers $headers `
  -Uri "$baseUri/api/workspaces/default/debug/feishu-image/messages/$($queued.messageId)"
```

调试 API 不接受 `chat_id`、`message_id` 或 Connector ID。route 返回 409 时，先在目标飞书会话给
机器人发一条消息。参考图精细编辑时，把 Attached image notice 中的 `vision-*` 放进
`referenceArtifactIds`，将 `mode` 改为 `precision`；坐标必须写进 prompt，例如
`<bbox>120 180 640 760</bbox>`，范围为 0~999。

联网参考图用 `doubao_search` 结果里的精确图片 HTTPS URL 调 `import_image`，再把返回的
`artifactId` 放进 `generate_image(mode=precision)`。若只需把已有 Artifact 展示/发送，可用：

````markdown
```image
vision-0123456789abcdef0123456789abcdef
```
````

整个终态只有这个 fence 时应只发图片、不创建文本卡片；混合普通文本时飞书应先发送去除 fence 后
的文字，再按顺序追加图片。网页则在 fence 原位置加载当前 Workspace 的受控 Artifact API。URL、
相对路径、任意本地文件与跨 Workspace 路径都应保持为普通代码块或被飞书投影拒绝。

Agent 使用终态 Markdown 时，V1.6 应先显示原始 fence，再追加图片：

````markdown
```ImageGeneration
mode: precision
size: 2K
references: vision-0123456789abcdef0123456789abcdef

把图 1 <bbox>120 180 640 760</bbox> 区域内的左侧人物换成机器人，其他区域保持不变。
```
````

没有出现 `[ImageGenerationDirective]` 日志通常表示 fence 未闭合、正文为空、header 非法、
Command 非 succeeded、并非飞书来源，或同一 Turn 已成功调用 `send_image` 触发抑制。聚焦回归：

```powershell
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore `
  --filter "FullyQualifiedName~VolcengineArkImageGenerationProviderTests|FullyQualifiedName~ContextPipelineLayerTests"
dotnet test .\Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore `
  --filter "FullyQualifiedName~ImageGenerationServiceTests|FullyQualifiedName~RemoteImageArtifactImportServiceTests|FullyQualifiedName~ConversationReplyProjectionWorkerTests"
dotnet test .\Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore `
  --filter "FullyQualifiedName~AgentReplyImageGenerationDirectiveTests|FullyQualifiedName~AgentReplyImageDirectiveTests"
dotnet test .\Tests\PuddingAgent.IntegrationTests\PuddingAgent.IntegrationTests.csproj --no-restore `
  --filter "FullyQualifiedName~SendImageToolTests|FullyQualifiedName~FeishuImageUploadPreparationServiceTests"
dotnet test .\Tests\HarnessAgent.Core.Tests\HarnessAgent.Core.Tests.csproj --no-restore `
  --filter "FullyQualifiedName~FeishuClientReplyTests"
```

## 11.9 Agent Benchmark 诊断

先 dry-run 检查服务端当前识别出的 deterministic cases；该操作不会调用付费模型：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\run_benchmarks.py --dry-run
```

单题 smoke：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\run_benchmarks.py `
  --case workspace-markdown-summary --label local-smoke
```

run 元数据和评价快照位于 `D:\data\runtime\benchmark-runs\`。出现 `unscored` 时先检查该 case 是否有 artifact oracle；不要把 Session diagnostics 的启发式分数当作任务完成。Token 为 0 时检查 `TokenUsageEvents` 的 `SessionId/ParentSessionId`，角色为空时检查 `sub_agent_runs.task_planning_metadata_json` 是否包含 `role_in_plan/profile_id`。

Benchmark Turn 必须在 ChatMessage/Command metadata 中保留 `excludeFromLearning=true`。经验→SKILL 意外收录基准轨迹时，先过滤该字段；不要通过按模型名猜测角色来修正统计。

## 11.10 PuddingDesktop 发布与 Workbench 静态资源诊断

Desktop 发布前先生成 Workbench：

```powershell
pnpm --dir .\Source\PuddingPlatformAdmin run build
dotnet publish .\Source\PuddingDesktop\PuddingDesktop.csproj -c Release --no-restore
```

Desktop 发布产物必须同时包含：

- `PuddingDesktop.exe`；
- `Microsoft.Windows.SDK.NET.dll`（`WebView2CompositionControl` 需要）；
- `core/PuddingAgent.exe`；
- `core/wwwroot/admin/index.html`。

缺失静态资源时先检查
`Source/PuddingPlatformAdmin/dist/index.html`，再检查可执行宿主是否导入
`PuddingHost/Build/PuddingHostContent.props`。`PuddingHost` 类库自身不导入该 props，
否则 Web SDK 可能在 `DiscoverPrecompressedAssets` 报同一 `dist` 文件 key 重复。

使用隔离的系统 Temp 配置启动真实 Desktop smoke，禁止复用 `D:\data`：

```powershell
.\TestScripts\start-phase1a-desktop-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase1a-win11-preview
```

窗口底部显示 `0.0.0.0:<configured-port>` 监听端点；运行中心悬停可查看同端口 Loopback 控制地址。确认以下路径均有非零响应体：

```powershell
$base = 'http://127.0.0.1:<configured-port>'
Invoke-WebRequest "$base/health/ready" -UseBasicParsing
Invoke-WebRequest "$base/admin/" -UseBasicParsing
Invoke-WebRequest "$base/admin/index.html" -UseBasicParsing
```

如果 `/admin/index.html`、CSS 或 JS 返回 `200` 但 `RawContentLength=0`，检查
`PuddingWebApplicationExtensions.MapPuddingApplication` 是否重新启用了
`MapStaticAssets()`。Desktop 的嵌套 `core/` 发布布局必须通过
`AppContext.BaseDirectory/wwwroot` 的 `PhysicalFileProvider` 提供静态文件，并用物理
`admin/index.html` 处理 SPA fallback。

如果 WebView2 报 `RedirectFailed`，检查是否显式映射了 `/admin` 到 `/admin/`。
ASP.NET Core 路由默认忽略末尾斜杠，这种映射会同时匹配 `/admin/` 并形成重定向循环；
让 `UseDefaultFiles` 单独处理即可。

如果标准 WebView2 覆盖标题栏或导航栏，确认 Workbench 使用
`WebView2CompositionControl`，项目 TFM 为
`net10.0-windows10.0.17763.0`。如果启动异常包含缺失
`Microsoft.Windows.SDK.NET, Version=10.0.17763.10`，检查发布目录是否包含
`Microsoft.Windows.SDK.NET.dll`。CompositionControl 创建后必须在
`EnsureCoreWebView2Async` 前设为 `Visible`，否则可能一直停留在初始化遮罩。

Desktop 在 Core 和 Serilog 之前发生的启动/XAML/WebView2 异常写入：

```text
%LOCALAPPDATA%\Pudding\logs\desktop.log
```

smoke 脚本通过 `PUDDING_DESKTOP_HOME` 将该日志重定向到
`<smokeRoot>/desktop-home/logs/desktop.log`。该环境变量只控制 Desktop 自身配置目录，
Core Token、端口和 DataRoot 仍必须通过配置文件与启动参数传递。

如果 Desktop 中出现“系统初始化”，但日常开发环境已经初始化，先看窗口底部的
`数据` 路径。`start-phase1a-desktop-smoke.ps1` 会故意创建新的系统 Temp DataRoot，
该目录没有 `runtime/bootstrap-state.json`，因此显示初始化向导是正确行为；它不能
代表 `D:\data` 的初始化状态。使用真实数据验证时必须先停止 `dev-up.py`，避免两个
Core 同时访问 `D:\data`，再让 Desktop 的 `desktop.json` 指向 `D:\data`。已初始化
环境的正确首屏是登录页，认证完成后进入 Workbench `/` 产品首页；如果仍进入
`/bootstrap`，依次检查窗口底部 DataRoot、`/api/bootstrap/status` 和浏览器 UDF。

Desktop 项目切换到带 Windows 版本的 TFM 后，如果构建提示
`project.assets.json` 缺少 `net10.0-windows10.0.17763.0`，先执行：

```powershell
dotnet restore .\Source\PuddingDesktop\PuddingDesktop.csproj
dotnet build .\Source\PuddingDesktop\PuddingDesktop.csproj --no-restore
```

这是旧 NuGet assets 的目标框架缓存，不应通过降低 TFM 或移除
`WebView2CompositionControl` 解决。导航 XAML 改名后若 IDE 仍报告 `navLogs`，先用
`rg -n "navLogs" Source/PuddingDesktop` 核对磁盘源码；当前导航字段是
`navWorkbench`、`navCore`、`navStorage`、`navSettings`，磁盘已无旧引用时重新加载项目或重建即可。

如果 Desktop Workbench 的 `GET /api/workspaces/{workspaceId}/agents/status` 返回 500，
先用响应中的 `errorId` 搜索 `D:\data\logs\error` 和 `D:\data\logs\system`。若堆栈落在
`PlatformApiClient.GetSessionsAsync`，并显示连接 `localhost:5000` 被拒绝，说明 Core 已经
按 Desktop 配置监听，但内部控制面请求仍在使用错误的默认地址。检查
`PuddingControllerAddressRewriteHandler` 是否已注册到 `PlatformApiClient`，以及
`PuddingApplicationHost.CaptureBoundAddresses` 是否已把实际地址写入
`IPuddingServerAddressAccessor`。

HttpClient 的起始日志可能在 DelegatingHandler 执行前显示原始
`http://localhost:5000/...`，不能仅凭这一行判定重写失败。有效验收证据是后续
`Sending HTTP request` 指向配置端口的 `127.0.0.1:<port>` 本机控制地址，下游请求返回 200，
并且原始 Agent 状态接口也返回 200。

发布报 `NETSDK1152` 且路径同时出现两个
`Microsoft.CodeAnalysis.Workspaces.MSBuild` 版本时，查各被引用项目的传递依赖：

```powershell
dotnet list .\Source\PuddingMemoryEngine\PuddingMemoryEngine.csproj package --include-transitive
dotnet list .\Source\PuddingPlatform\PuddingPlatform.csproj package --include-transitive
dotnet list .\Source\PuddingCodeIntelligence\PuddingCodeIntelligence.csproj package --include-transitive
```

EF Design 和运行时 Code Intelligence 必须解析到同一 Roslyn Workspace 版本；
不要通过禁用 `ErrorOnDuplicatePublishOutputFiles` 隐藏冲突。

## 11.11 PuddingDesktop Storage 统计与旧日志清理诊断

Storage 页面显示的是 DataRoot 中文件的**逻辑大小**，不是 NTFS 精确物理占用；顶部磁盘条来自 `DriveInfo`，表示整个卷的已用/可用空间。两者口径不同，不能用分类大小之和反推磁盘已用空间。

Storage 定向验证必须使用系统 Temp 下的隔离 DataRoot，禁止让自动化测试或清理 smoke 指向 `D:\data`：

```powershell
dotnet test .\Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo
dotnet publish .\Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release --no-restore `
  -o .\.tmp-build\phase1b-storage-preview
.\TestScripts\start-phase1a-desktop-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase1b-storage-preview
```

扫描出现 Warning 时按页面给出的路径检查访问权限、扫描中消失的文件和 Junction/Reparse Point。扫描器不会跟随链接，也不会因为单个目录无权访问而让窗口崩溃。分类采用 first-match：`browser/downloads`、`screenshots`、`traces` 必须先于 Browser UDF；各分类文件数和逻辑大小之和应等于总计。

V1 日志清理边界固定为：

- 只允许真实 `<DataRoot>/logs`，DataRoot 不能是空路径、相对路径、盘符根目录或 Reparse Point；
- 只处理 `.log`、`.jsonl`、`.txt`、`.gz`、`.zip`，且 `LastWriteTimeUtc` 早于 24 小时 cutoff；
- UI 必须先 Preview，再内联确认；执行前重新检查路径、长度、创建/修改时间和 cutoff；
- 已变化、已消失、正在占用、越界或扩展名不允许的文件跳过/失败，不能扩大为递归通用删除；
- 只移除 logs 下已经为空的真实子目录，不删除 logs 根目录；完成后立即重扫。

启动即出现 `XamlParseException` 且提示只读属性不能 `TwoWay` 绑定时，检查 `ProgressBar.Value` 等控件是否显式使用 `Mode=OneWay`。该问题可以通过编译但会在真实窗口加载时失败，因此 Storage 改动必须保留发布包视觉 smoke。

WPF 会生成 `PuddingDesktop_*_wpftmp.csproj`。若自定义 `BaseOutputPath` 后出现临时项目找不到 `PuddingCore.dll`，先回到普通项目输出或只用 `dotnet publish -o <仓库临时目录>` 隔离发布产物；不要把 `BaseOutputPath`、`OutDir` 或测试输出指向运行时 DataRoot。

## 11.12 PuddingDesktop 运行中心、单实例与自动恢复诊断

运行中心的进程职责分为两层：`CoreProcessSupervisor` 只管理一次 Core 启停和进程树，`DesktopRuntimeOrchestrator` 管理异常恢复、退避、熔断和用户意图。默认策略是 2s/4s/8s 退避，60 秒窗口内允许 3 次恢复，继续失败进入 `CoreCircuitOpen`。用户点击“停止”、配置无效或 DataRoot 缺失都不得自动拉起。

DesktopChild 的监听端口来自 `<DataRoot>/config/system.json` 的 `desktop.core.port`，必须为 `1–65535`，默认 `8080`。进程命令行应包含 `--urls http://0.0.0.0:<port>`，`PUDDING_DESKTOP_READY` 则报告 `http://127.0.0.1:<port>` 给 Desktop 控制链路。若启动失败，先用 `Get-NetTCPConnection -State Listen -LocalPort <port>` 判断端口占用，再看运行中心最近 stderr 中的 Kestrel bind 错误；系统不会静默回退到随机端口。局域网访问还需确认 Windows 防火墙允许该入站端口。

定向验证：

```powershell
dotnet build .\Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo
dotnet test .\Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo
dotnet publish .\Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release --no-restore `
  -o .\.tmp-build\phase1b-runtime-preview
.\TestScripts\start-phase1a-desktop-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase1b-runtime-preview
```

上述 Desktop build/test/publish 必须串行执行。`dotnet build PuddingDesktop` 与引用同一 WPF 项目的 `dotnet test PuddingDesktop.Tests` 若并行共享默认 `obj`，可能在 `Microsoft.WinFX.targets` 报 `RG1000` 和重复 `mainwindow.baml`；串行重跑即可，不要因此修改 XAML 资源名或清理 DataRoot。

真实故障恢复 smoke 必须使用脚本创建的系统 Temp `DesktopHome` 和 DataRoot。终止进程前同时核对 Core 的 `ParentProcessId`、`ExecutablePath`、`--data-root` 参数与隔离目录，禁止对真实 `D:\data` Core 或名称匹配的一组进程执行批量 Kill。正常结果是：旧 Core 退出后运行中心先进入“等待自动恢复”，随后出现新的 PID，监听端点仍为同一个配置端口；点击“停止”后至少等待一个最大退避周期，Desktop 仍存活但 Core 子进程数保持为 0。

Desktop 使用本地命名 `Semaphore` 保证单实例，并通过仅当前 Windows 用户可访问的 Named Pipe 发送激活信号。发现第二个窗口或第二个 Core 时，检查两个启动进程是否使用同一版本的 Desktop 和同一 `PUDDING_DESKTOP_HOME`；开发中的旧版本不参与新版单实例协议。实例发现文件位于：

```text
<DesktopHome>/desktop.instance.<instance-key>
```

默认关闭按钮只隐藏主窗口，Core 和外部 HTTP API 会继续运行；这不是退出失败。使用托盘菜单“退出 Pudding”才会执行明确退出并停止 Core/WebView2。托盘图标在 Explorer 重启后由 `TaskbarCreated` 消息重建。若托盘初始化失败，检查 `%LOCALAPPDATA%\Pudding\logs\desktop.log` 中的 `[Desktop] Tray initialization failed`，窗口仍应保持可用。若菜单文字在浅色弹出层上不可见，检查 `DesktopTrayIconService` 的独立 `Background` / `Foreground`，并确认 `MenuItem.Header` 使用显式前景色的 `TextBlock`：字符串 Header 会创建隐式 `TextBlock`，其全局深色主题样式会覆盖 MenuItem 继承色，造成白底白字。原生 `ComboBox` 也应通过显式 `ItemTemplate` 使用 `NativeLightPopupTextBrush`。若标题栏最小化、最大化、关闭图标过小或不可见，检查 `TitleBarButtonStyle` 的 Fluent 图标字号，以及其 `ContentPresenter` 是否显式传递 `Foreground`、`FontFamily` 与 `FontSize`。

从 Visual Studio 启动 Desktop 后，若 Core 在 Ready 前连续退出并报告 `DirectoryNotFoundException: Source\PuddingAgent\wwwroot`，说明 WPF 启动环境错误地把 ASP.NET Core 的 Development 静态资源清单行为传给了子进程。`CoreProcessSupervisor` 必须对 DesktopChild 显式设置 `ASPNETCORE_ENVIRONMENT=Production` 和 `DOTNET_ENVIRONMENT=Production`，并把工作目录设为 Core 可执行文件目录；Workbench 由输出或发布目录中的物理 `wwwroot` 提供。`PuddingDesktop/Properties/launchSettings.json` 不应包含 ASP.NET Core URL 或环境变量。

从 Visual Studio 直接启动 `PuddingDesktop.csproj` 时，Desktop 不再通过 ProjectReference 隐式构建 Core；WPF 与 ASP.NET Core 保持独立进程/项目边界。开发态解析顺序仍是显式配置、发布包 `core/PuddingAgent.exe`、Desktop 同目录产物、`Source/PuddingAgent` 旧布局兜底，因此只重新构建 Desktop 可能继续启动旧 Core。若运行中心反复退出，且异常文案与当前源码不一致（例如旧二进制仍要求 loopback，而 Desktop 已传入 `http://0.0.0.0:<port>`），先核对日志中的实际 `ExecutablePath`、文件时间和完整启动参数；自动恢复只会重启同一产物，不会编译源码。应在 Core 完全停止后，通过引导重建入口 `/desktop/bootstrap/start`（或对应 UI 操作）执行 `stop → dotnet build Source/PuddingAgent/PuddingAgent.csproj → restart`。验收必须同时看到 `/desktop/bootstrap/status` 的 `coreState=Ready`、新的 Core PID/产物时间，以及 `http://127.0.0.1:<port>/health` 返回 `healthy`；不能只看 build exit code。DesktopChild 不是 `dotnet watch`，源码修改不会热替换到已运行进程。

Desktop 默认关闭到托盘且是单实例：窗口消失不代表进程退出。若旧实例仍在托盘，VS 启动的新进程只会激活旧实例，旧 Core 也会继续运行，看起来就像新后端没有生效。调试新构建前先从托盘选择“退出 Pudding”，再确认 `PuddingDesktop.exe` / 其子 `PuddingAgent.exe` 已退出；不要同时用 `dev-up.py` 和 Desktop 访问同一个 DataRoot。

运行中心“生成诊断包”只在用户点击时写入 `<DataRoot>/diagnostics/`。ZIP 应只包含运行快照、最近 Core 输出和配置键名；出现 ControlToken、Authorization、Cookie、API Key 或 Secret 值即视为缺陷。不要把完整 `system.json` 或 WebView2 UDF 直接加入诊断包。

“登录 Windows 后启动”只在用户保存 Desktop 设置时更新 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，不应在普通启动或测试构造期间隐式修改注册表。后台启动使用 `--background`；窗口创建和配置错误仍然完成，随后隐藏到托盘，不能因 Core 启动失败终止 Desktop。

运行中心的多行日志框若只显示成一行，先检查 `App.xaml` 的全局 `TextBox` 样式：普通输入框默认 `Height=34`，日志控件必须显式使用 `Height=Auto`、足够的 `MinHeight` 和顶部内容对齐。`PART_ContentHost` 应保持 Stretch，并通过 `HorizontalContentAlignment` / `VerticalContentAlignment` 承接控件设置；不要把 ScrollViewer 自身设为 Top，否则它仍可能按单行内容高度测量。该问题能通过编译，必须在重启 Desktop 后做实际窗口检查。

## 11.13 Phase 2A Agent Browser 与 Desktop Bridge 诊断

先区分三个互不等价的状态：窗口底部 `Core` 表示子进程/健康状态，Agent Browser 右栏 `Bridge Status` 表示认证 WebSocket 状态，Workbench Ready 表示 `/admin/` 的 WebView2 导航完成。Core 可以运行而 Workbench 尚未打开；Browser 在 Core 停止时也应保持可用。

标准验证命令：

```powershell
dotnet test .\Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet test .\Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo
dotnet publish .\Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release -o .\.tmp-build\phase2a1-final-preview --nologo
.\TestScripts\start-phase2a1-browser-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase2a1-final-preview -KeepArtifacts
```

smoke 只使用 `%TEMP%\PuddingAgent\phase2a1-browser-<guid>`，不得改指向 `D:\data`。脚本拒绝在另一个 Desktop 运行时启动，报告 Desktop/Core PID、两个 UDF 和退出后的残留子进程。Workbench UDF 是 `<DataRoot>/browser/workbench/user-data`，Agent Browser UDF 是 `<DataRoot>/browser/agent-browser/user-data`；两者相同即为缺陷。

Bridge 排查顺序：

1. `Core 运行中` 但 Bridge 为 `Disconnected`：先核对 Core 是 `--desktop-child`、监听地址是配置的 `0.0.0.0:<port>`、Ready 控制地址是同端口 Loopback，并检查 `system.json` 的 ControlToken 是否存在；Token 只允许进入 `X-Pudding-Desktop-Token` Header，禁止写入 UI、异常或诊断包。
2. 一直 `Connecting`：检查 Desktop 是否先启动 Receive Loop 再发送 Hello；HelloAck 前不能进入 Connected，也不能有第二个 Receive Loop。
3. 立即 `Failed`：Host 只接受 Loopback WebSocket、DesktopChild 模式、正确 Token 和匹配协议版本。用 `DesktopBrowserBridgeAuthenticationTests` 与 `DesktopBrowserBridgeHandshakeTests` 区分 401/403、非 WebSocket、首消息错误和协议拒绝。
4. 静默连接 45 秒后仍不掉线：检查 heartbeat watchdog 是否可取消阻塞 Receive。测试必须使用 fake clock，不等待真实 45 秒。
5. Restart 后旧错误覆盖新连接：检查 connection generation。generation N 的 Receive/finally/pending 完成不得改变 N+1；旧命令断线后不得重放。
6. pending 命令在断线后悬挂：应返回 `browser_bridge_disconnected`；Dispatcher handler 被移除时应返回 `browser_not_available`。

Tab/Surface 排查顺序：

- `BrowserWorkspaceController` 是 `Tabs`、`Activities`、`ActivePageId`、`AgentTargetPageId` 和导航状态的唯一事实源；View 不得再次使用 `DataContext=this` 或维护复制集合。
- active tab 只决定可见 Surface，Agent target 只决定无显式 PageId 的命令目标。切换 active tab 不得改 target；关闭 target 后命令返回 `page_not_found`，不能偷偷回退 active tab。
- 每次创建 Page 必须创建一个 Surface；`WpfBrowserSurfaceHost.ActivateAsync` 只让目标 Surface `Visible/IsHitTestVisible`。若页面创建永久等待，检查新 `WebView2CompositionControl` 是否在 `EnsureCoreWebView2Async` 前被设为 `Collapsed`；初始化阶段应使用 `Hidden` 保持在 WPF layout，完成后再由 Controller 激活。
- Workbench 只在其页面可见时初始化。若从 Agent Browser 启动 Core 后长期停在 `Workbench 加载中`，检查是否又对父级为 Collapsed 的 Workbench 调用了 `EnsureCoreWebView2Async`。
- Activity 只保存动作名、Page 摘要、时间、结果和错误码，最多 100 条；不得显示完整 Arguments、脚本、Token、Cookie 或表单值。

发布窗口在 `InitializeComponent` 前后退出时读取：

```text
<DesktopHome>/logs/desktop.log
```

`XamlParseException` 提示 `#AARRGGBB` 不是 `Foreground` 有效值，通常是把 `*Color` 资源直接绑定到了 Brush 属性，应改用对应 `*Brush`。`desktop.json` 的 `closeBehavior` 使用 `MinimizeToTray` 或 `ExitAndStopCore` 字符串；无法反序列化时检查 `DesktopCloseBehavior` 的 `JsonStringEnumConverter`。退出阶段 Browser/WebView2 释放必须有界，正常明确退出的脚本结果应为 Desktop exitCode 0 且 `remainingChildProcessIds=[]`。

## 11.14 Phase 2A-2 Remote Browser 与 Agent Tools 诊断

先确认工具是否应该存在。`browser_context`、`browser_tabs`、`browser_navigate` 只在以下两个条件同时满足时注册：

```text
PuddingHostOptions.Mode == DesktopChild
PuddingHostOptions.BrowserAutomationEnabled == true
```

普通 Console/dev Host 看不到 Browser Tools 是正确隔离，不应通过注册空实现或全局打开能力“修复”。DesktopChild 仍看不到工具时检查 `AddPuddingApplicationServices(hostOptions)` 是否把同一个 `PuddingHostOptions` 传到 `AddDesktopBrowserAutomation()`，并运行：

```powershell
dotnet test .\Tests\PuddingHost.Tests\PuddingHost.Tests.csproj `
  --no-restore --nologo `
  --filter "FullyQualifiedName~BrowserBridgeServiceCollectionExtensionsTests"
```

工具错误按稳定 `error.code` 排查：

1. `browser_not_available`：Desktop 尚未为 Browser Workspace 安装 Dispatcher，通常是 DataRoot 未 Ready、Browser 初始化失败或 Desktop 正在退出。
2. `browser_bridge_disconnected`：Core Broker 没有已完成 HelloAck 的当前 Desktop 连接。先看 Agent Browser 的 Bridge Status，再检查 generation、watchdog 和 Core Restart 日志。
3. `browser_context_not_found` / `browser_page_not_found`：重新调用 `browser_context list` 或 `browser_tabs list`，不要从旧 Core proxy 缓存推测 Desktop 状态。
4. `browser_operation_not_supported`：调用了 Phase 2A-2 尚未开放的 Snapshot、Locator、输入、Evaluate、CDP、Cookie 或文件能力；这是明确边界，不是 Bridge 失败。
5. `browser_invalid_arguments`：检查 action 枚举以及 ContextId、PageId、Url 必填组合。
6. `browser_deadline_exceeded` / `browser_cancelled`：检查 Tool cancellation、Bridge command `DeadlineUtc` 和 Desktop WebView2 是否仍在 UI 线程响应；调用方取消应继续表现为 `OperationCanceledException`。

最短自动化闭环：

```powershell
dotnet test .\Tests\PuddingBrowser.AgentTools.Tests\PuddingBrowser.AgentTools.Tests.csproj `
  --no-restore --nologo
dotnet test .\Tests\PuddingHost.Tests\PuddingHost.Tests.csproj `
  --no-restore --nologo `
  --filter "FullyQualifiedName~BrowserAgentToolBridgeIntegrationTests|FullyQualifiedName~RemoteBrowserRuntimeTests"
```

第二条测试证明 Tool → `IBrowserRuntime` → Remote proxy → Broker → 认证 WebSocket → Desktop result，不需要真实模型或外网。若它通过而真实 Agent 看不到工具，检查 Agent capability：新通用助手默认包含 `cap-browser-context`、`cap-browser-tabs`、`cap-browser-navigate`，既有 Agent 不会被升级过程静默扩权，必须在配置界面选择对应能力。

Core Restart 后 Tab 消失通常不是 Remote proxy 的预期行为。`RemoteBrowserRuntime.DisposeAsync()` 不发送 close；Desktop 仍拥有 Context/Page。新 Core 应先执行 `context.list` 并恢复代理。若 Tab 真被关闭，搜索显式 `context.close`/`page.close` Activity，而不是给 proxy Dispose 添加兼容性恢复。

Agent Activity 只允许记录动作名、Context/Page 摘要、时间、结果和错误码。不得为了诊断写入完整 tool arguments、脚本、URL query secret、Cookie、Token 或表单值。

Phase 2A-2 发布 smoke 继续使用：

```powershell
.\TestScripts\start-phase2a1-browser-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase2a2-minimal-preview `
  -KeepArtifacts
```

正常明确退出必须同时满足 Desktop exitCode 0 和 `remainingChildProcessIds=[]`。本 smoke 只证明 Desktop/WebView2/Bridge/退出表现；真实模型是否正确选择 Browser Tool 必须另做 DeepSeek 可见 smoke，不能由集成测试替代。

## 11.15 Phase 2A-3 Snapshot、Locator、Interact 与 Wait 诊断

DesktopChild 启用 Browser Automation 后应有七项 Browser Tool。若只有三项导航工具，先检查运行的 Core 是否来自最新 Release，以及 Agent 是否显式拥有以下新增能力；既有 Agent 不会被升级过程静默扩权：

```text
cap-browser-snapshot
cap-browser-locate
cap-browser-interact
cap-browser-wait-for
```

典型调用顺序是 `browser_snapshot` → 使用返回 ref 或 `browser_locate` → `browser_interact` → `browser_wait_for`/新 Snapshot。ref 格式为 `v{PageVersion}-n{sequence}`。导航后出现 `stale_element_reference` 是正确保护，必须重新 Snapshot；禁止去掉版本校验或自动回退到文本/CSS Locator。

稳定错误按 code 排查：

1. `browser_element_not_found`：Locator 为 0 个匹配；重新 Snapshot，检查动态 DOM 和 PageId；
2. `browser_locator_ambiguous`：多个匹配但没有 `Nth`；缩小 role/name/text 或明确索引；
3. `browser_element_not_visible` / `browser_element_disabled`：页面状态尚未就绪；先 Wait，不能强制 JS click；
4. `stale_element_reference`：PageVersion 已变化；重新 Snapshot，不重复已经提交的动作；
5. `browser_operation_not_supported`：Frame/复合 Has、drag、upload、Evaluate、CDP、Cookie 等不在 Phase 2A-3；
6. `browser_invalid_arguments`：检查 interaction action 的 Locator、text、values、checked 组合。

click、press 或表单动作可能已经提交并触发导航。Desktop 和 Agent Tool 在交互成功后不会重新查询旧 Locator，返回的 `Element` 可以为空；调用方应 Wait 或新 Snapshot。若 Activity/Tool result 中看到 fill/type 的原始值、完整 Locator、页面正文、ControlToken 或 Cookie，视为敏感信息泄漏缺陷。

确定性与真实 WebView2 验收：

```powershell
dotnet test .\Tests\PuddingBrowser.AgentTools.Tests\PuddingBrowser.AgentTools.Tests.csproj --no-restore --nologo
dotnet test .\Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet test .\Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo
dotnet build .\Tests\PuddingBrowser.TestSite\PuddingBrowser.TestSite.csproj --no-restore --nologo
dotnet build .\Tests\PuddingBrowser.WebView2.Smoke\PuddingBrowser.WebView2.Smoke.csproj --no-restore --nologo
.\TestScripts\start-phase2a3-webview2-smoke.ps1 -HoldSeconds 0
```

正常 smoke 输出包含 `phase2a3-webview2-smoke-passed`、`pageVersion=2`、`finalContainsSaved=true` 和 `staleCode=stale_element_reference`。脚本只使用 `%TEMP%\PuddingAgent\phase2a3-webview2-*`；WebView2 子进程可能在 WPF 退出后短暂锁定 UDF，因此清理有界重试，最终锁定只报告 Warning，不能掩盖页面断言结果。测试进程退出后若仍有 Pudding/TestSite 进程，按脚本输出的精确 PID、ExecutablePath 和 Temp DataRoot 核对所有权，禁止按名称批量终止用户的 WebView2/浏览器进程。

真实 DeepSeek smoke 不能由上述集成测试代替。只有用户明确选择测试 Agent/DataRoot 后才能执行；不得读取、复制或回显 `D:\data` 中的 LLM Secret。需要保留的证据是脱敏 Tool 顺序、provider/model/role、Bridge Activity、最终页面和退出结果，不包含表单值、Token、Cookie 或 API Key。

## 11.16 用户消息无输出与过期心跳执行诊断

聊天页出现用户消息、Core `/health` 仍为 200，但长期没有 `thinking/delta/terminal` 时，不要先归因前端或 Provider。按同一 `conversationId` 对齐四组事实：

1. `chat_execution_commands` / Turn / Run：确认消息已受理，区分 `pending` 与 `running`，记录 `commandId/turnId/runId`；
2. `session_event_log`：确认该 run 是否只有 `turn.accepted/turn.started`，以及同 Agent 另一会话最近执行的首条 thinking 是否来自 `system:heartbeat`；
3. `message_deliveries`：检查心跳 delivery 的 `attempt_count/lease_until/claimed_by_execution_id/status`，重点查“旧 execution lease 过期 → recovery/reclaim → 新 execution Busy 后 ACK，但旧 execution 后续仍产生日志”；
4. `runtime_activity` 与 `AgentExecutionStateRegistry`：若 Agent 实际持续跑工具而 registry 仍显示 idle，说明存在绕过 `RuntimeAgentDispatcher` 的入口。

这个组合表明卡点在执行互斥和 delivery fencing，不是 SSE 渲染。当前不变量是：

- 所有用户 Turn、Agent 消息和心跳必须经 `RuntimeAgentDispatcher` 共用 `TryBegin/Complete`；用户 Turn 遇 Busy 等待，心跳遇 Busy 丢弃；
- 普通用户/Agent 消息到达时取消同 workspace/agent 的活动心跳；心跳是可抢占的低优先级工作；
- Message Fabric 长执行每 2 分钟续 5 分钟租约，终态或回复前再校验一次；`RenewLeaseAsync=false` 后旧执行必须取消且不得 ACK/retry/dead-letter/reply；
- 非空 `executionId` 必须与 `ClaimedByExecutionId` 完全相等，owner 被回收清空也不能让旧 execution 回写。

关键日志：

```text
[TurnExecutorAdapter] Waiting for agent availability
[MessageDeliveryDispatcher] User activity interrupted active heartbeat
[MessageDeliveryDispatcher] Delivery lease ownership lost; cancelling stale runtime execution
[MessageDeliveryDispatcher] Discarded stale execution before delivery mutation
```

定向回归：

```powershell
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore --nologo `
  --filter "FullyQualifiedName~MessageDeliveryDispatcherTests|FullyQualifiedName~TurnExecutorAdapterTests"
dotnet test .\Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --nologo `
  --filter "FullyQualifiedName~MessageFabricStoreTests"
```

## 11.17 Desktop 重启 Core 固定 60 秒失败，SessionChunk 回填反复从头开始

若 `desktop/bootstrap/start` 的结果显示 `buildExitCode=0`，但约 60 秒后仍为
`coreRestarted=false`，同时每次手工启动都能看到 `[SessionChunkBackfill] start`、若干
`progress`，却永远没有 `completed`，先检查 Hosted Service 是否阻塞宿主启动：

- DesktopChild 只有在 `await app.StartAsync()` 返回后才输出 `PUDDING_DESKTOP_READY`；
- `IHostedService.StartAsync` 若直接等待完整历史扫描，Ready 信号会被回填时长阻塞；
- Desktop 的 Core Supervisor 到达 `startupTimeoutSeconds` 后会终止该子进程，所以下次启动
  又从 `lastId=0` 扫描，看起来像回填卡死或反复重启。

修复边界是让一次性长回填继承 `BackgroundService`，在 `ExecuteAsync` 首先异步让出执行，
并用 stopping token 支持宿主退出；不能靠单纯增大 Desktop 启动超时掩盖。验收必须同时满足：

1. `/desktop/bootstrap/status` 在超时前进入 `coreState=Ready`；
2. Core PID 跨过原来的启动超时仍存活；
3. 日志最终出现 `[SessionChunkBackfill] completed`；
4. `SessionChunkVectors` 计数持续增长且重启幂等跳过已有 `MessageId`。

定向回归：

```powershell
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore --nologo `
  --filter "FullyQualifiedName~SessionChunkBackfillServiceTests"
```

## 12. 修改后的最低验收

```powershell
dotnet build .\Source\PuddingAgent\PuddingAgent.csproj --no-restore
git diff --check
python .\dev-up.py --restart
```

然后确认：

- 健康检查为 200；
- 针对性测试通过；
- 浏览器唯一消息端到端成功；
- 延迟历史同步后消息仍存在；
- 刷新后消息仍存在；
- 当前启动周期 Error 数为 0；
- `Source/code_map.md` 和相关 ADR 已同步；
- 未覆盖用户已有的无关工作区修改。

---

## 11.18 诊断表保留期裁剪（Diagnostics:Retention）

platform.db 的 append-only 诊断明细此前零裁剪机制，库会持续增长（2026-08-06 实测 2.48 GiB）。当前后台白名单只包含 `telemetry_metric_events`、`context_layer_metric_events` 和 `runtime_activity`。

- 服务：`PuddingPlatform/Services/Diagnostics/DiagnosticRetentionService.cs`（BackgroundService，Task.Yield 起步不阻塞宿主）。
- 配置节 `Diagnostics:Retention`：`Enabled` / `RunIntervalHours`(24) / `StartupDelaySeconds`(60) / `BatchSize`(5000) / `BatchDelayMs`(100) / `Tables:{表名:{Enabled,RetentionDays}}` / `Vacuum:{Enabled}`。
- 建议保留期：telemetry_metric_events、context_layer_metric_events 和 runtime_activity 默认 14 天；也可由 Storage 页按 7/14/30/90 天显式预览清理。
- 安全红线：`session_event_log` 与 `conversation_events` 都是权威执行事实源，不在后台或手动清理白名单；不能把“已有投影”误当成“可以删除 replay 事实”。
- SQLite 无 DELETE...LIMIT：用 rowid 子查询分批删，批间限速；时间戳为 "O" 格式字符串，字典序比较安全。
- VACUUM 默认关闭：约 2.5 GiB 库 VACUUM 需要等量临时空间与较长锁，建议借 bootstrap 的 Core 停止窗口手动执行。
- 验证：`PuddingPlatformTests/Services/DiagnosticRetentionServiceTests.cs`（4 用例：过期裁剪/禁用跳过/权威表跳过/白名单防注入）。

## 11.19 Chat 首屏、渐进消息与滚动性能

先区分网络 payload、React 重算和滚动提交三个层面，避免只凭视觉卡顿判断：

1. 检查 `GET /api/workspaces/{workspaceId}/agents/{agentId}/conversation`：active run 的 `processItems` 最多 64 条，但 `processSummary` 应保留完整的思考/工具计数。
2. 检查 `GET /api/conversations/{conversationId}/bootstrap?messageLimit=1`：`subAgentEvents` 最多 500 条；截断后 run 的终态由 `subAgentRunStatuses` 对账。
3. 折叠过程面板不应执行 rounds/trace chips 构建；展开后才允许这部分 CPU 开销。
4. 高频 scroll 事件只有在 `atBottom`、`nearTop` 或 `followMode` 改变时才提交 React state；首屏布局稳定后 100ms 贴底轮询应在约 500–800ms 内结束。
5. 请求带 `Accept-Encoding: br, gzip` 时，动态 JSON 和前端静态资源应出现 `Content-Encoding`，用于确认 Host 响应压缩已生效。

前端可使用 `?perf=1`（或 `localStorage.pudding_perf=1`）打开现有性能埋点，重点对比 conversation/bootstrap 响应字节数、首屏提交次数和滚动期间长任务。后端改动只有在 Desktop/Core 外部重启到新构建后才会生效，不能用承载当前 Agent 的旧进程作为验收依据。

生产 bundle 基准（2026-08-09 第二批修复）：Chat 首始 chunk 从约 804KB 降到 333KB，Markdown 增强块约 472KB 按需加载；全局主包从约 2.08MB 降到 1.89MB。若后续构建中 Chat chunk 再次包含 `katex`、`react-markdown` 或 `parse5`，检查 `MessageItem.tsx` 是否重新出现静态 Markdown import；若主包重新包含 `SettingDrawer`，检查 `app.tsx` 是否恢复了 Pro Components 值导入。

第三批基准：主包 `1,867,778` bytes，Chat 首始 chunk `303,521` bytes，二者名义合计 `2,171,299` bytes；相对第二批的 `2,221,844` bytes 再减少约 2.3%。子代理检查器 `20,374` bytes、摄像头输入 `8,909` bytes、会话诊断 Drawer `8,845` bytes 均为首次使用加载；完整性能诊断另在 async chunk。曾试验 Umi `granularChunks`，虽然 `umi.js` 降到约 1.67MB，但 HTML 新增同步 `framework.js` 约 220KB，合计没有下降且多一个阻塞请求，因此已撤销。后续比较必须读取 `dist/chat/index.html` 的全部同步 script，不能只看构建日志里的 `umi.js` 单文件大小。

第四批基准（管理壳路由隔离）：关闭 Umi 全局 `layout`，把管理页挂到异步 `src/layouts/AdminLayout` 父路由，并从 `src/app.tsx` 移出 ProLayout、管理端头像/操作区和 SettingDrawer。生产主包为 `1,373,107` bytes，Chat 首始 chunk 为 `303,537` bytes，名义合计 `1,676,644` bytes；相对第三批减少 `494,655` bytes（22.78%）。`dist/chat/index.html` 仍只有一个业务同步脚本 `umi.fbc8a3fa.js`，生成路由中不再出现 `plugin-layout` / `ant-design-pro-layout`；`AdminLayout` 自身入口 chunk 为 `4,949` bytes，其 Pro 依赖只在管理路由加载。历史搜索 Modal 还必须以 `historyModalOpen` 作为挂载条件，否则虽然声明为 `React.lazy`，仍会在 Chat 首载立即请求其 chunk。

第五批基准（Workspace Studio 退役）：删除 Phaser、2D Canvas、Studio 页面/路由/场景模型和 Web 静态场景资源后，生产 JS 总量从 `6,226,888` bytes 降到 `4,959,485` bytes（减少 20.35%），完整 `dist` 从 `71,755,862` bytes 降到 `29,001,583` bytes（减少 59.58%）。删除前 `vendors_1-async.9781b13d.js` 中 Phaser 占 `1,201,323` bytes，Studio 页面 chunk 占 `60,429` bytes；删除后两者、`workspace-studio/index.html` 和 Web `assets/agent-sprites` 均不存在。角色精灵素材迁移到 `Source/PuddingDesktop/Assets/AgentSprites/`，且未配置为 Desktop 发布内容。因为此前 Studio 已是独立异步路由，Chat 首载仅减少 `2,839` bytes；排查此类功能时要同时比较全量 JS、完整 dist、静态资源和首屏同步脚本，不能把安装包瘦身误报为首屏收益。

管理侧栏如果把 `home`、`appstore`、`thunderbolt` 等英文名称直接显示在菜单文字前，而不是显示图标，说明 Umi 全局 Layout 关闭后路由图标转换链丢失。检查 `src/layouts/AdminLayout/menuIcons.ts` 是否覆盖 `config/routes.ts` 的全部 `icon` 名称，并运行 `src/utils/adminRoutes.test.ts`；修复必须留在异步 AdminLayout 边界，不能为恢复图标重新启用全局 Layout 插件。生产构建中 AdminLayout 入口增加到约 `7.23 KB`，仍只在管理路由加载。

管理顶栏出现 Ant Design Pro 示例头像时，先检查 `/api/currentUser` 的 `data.avatar`，而不是在 ProLayout 上叠加 CSS。默认值应由 `PuddingPlatform/Controllers/Api/AuthApiController.cs` 返回 `/admin/assets/images/me.png`，开发 Mock 也必须同步；该文件位于 Web `public/assets/images/`，生产构建后应能在 `dist/assets/images/me.png` 找到，避免运行时依赖第三方示例资源。

浏览器 smoke：源码开发栈下 `/admin/chat` 可加载长会话，滚轮离开底部会出现“回到底部”且点击后恢复贴底；`/admin/llm-resource-pool` 与 `/admin/voice-models` 均能加载管理侧栏、顶栏和数据页并完成 SPA 路由切换。验证结束后执行 `python dev-up.py --down`，避免开发 Core 与 Desktop 争用同一个 DataRoot。

## 11.20 LLM 模型走错 Chat Completions / Responses / Anthropic Messages 协议

如果 Responses 模型返回 403，且响应体出现 `object: "chat.completion"`，说明请求实际到达了旧的 `/chat/completions` 路径。不要只检查 Provider 的 BaseUrl；按同一个 `providerId/modelId` 对齐以下事实：

1. `D:\data\config\llm.providers.json` 的目标模型必须有 `protocol: "responses"`；Provider 本身不应存在 `protocol` 字段。
2. Admin 的 LLM 资源池模型列表与新增/编辑抽屉应显示同一个模型协议；Provider 列表和 Provider 表单不显示协议。
3. Core 启动时配置校验会拒绝缺失协议或 `openai` / `responses` / `anthropic` 之外的值，不能用默认 Chat Completions 静默回退。
4. 查看 `[DirectLlm] REQUEST/STREAM` 或 `[ControllerLLM] CALL/STREAM` 日志中的 `provider/model/protocol/endpoint`：`openai` 应进入 `/chat/completions`；`responses` 应进入 `/responses` 并发送 `store=false`；`anthropic` 应进入 `/messages` 并使用 `x-api-key` 与 `anthropic-version`。
5. 若配置正确但日志仍显示 `protocol=openai`，核对运行中的 `PuddingAgent.exe` 是否加载了新构建；Desktop 托盘旧实例或未完成的 Core 重启会继续运行旧路由代码。

协议的唯一事实源是模型配置。请求覆盖、Provider 字段和 Provider 默认协议都不能参与路由；混合 Provider 应使用不同模型定向证明 `openai → /chat/completions`、`responses → /responses`、`anthropic → /messages`。OpenCode Go 的 Qwen 若误走 Chat Completions 会返回 format 不兼容；若 `/messages` 返回 missing API key，检查是否误用了 Bearer 而非 `x-api-key`。

## 11.21 Storage 数据库与索引管理 API

Storage 页面数据库明细必须来自 Core，Desktop 只做 HTTP 投影。排查“只显示数据库总量、没有明细或清理按钮不可用”时：

1. 确认 Desktop 的 Core 状态为 Ready，且当前实例已外部重启到包含 `StorageManagementController` 的新构建；旧托盘实例不会热加载本次改动。
2. 用 Admin JWT，或在 DesktopChild 的 Loopback 请求中携带 `X-Pudding-Desktop-Token`，调用 `GET /api/admin/storage/databases`。非 Loopback ControlToken 和非 admin JWT 都应被拒绝。
3. 日志筛选 `[Pipeline] GET/POST /api/admin/storage/databases` 与 `[StorageMaintenance]`。执行日志包含 `previewId`、deletedRows、droppedIndexes、removedScopes、bytesBefore/bytesAfter 和 compacted，不记录 Token、SQL payload 或用户内容。
4. 如果数据库总量准确但表级大小显示不可用，检查响应 Warning 是否说明 SQLite 无 `dbstat`；这不影响数据库文件/WAL/页面/freelist 和行数统计。不要为页面刷新运行全库 `dbstat`，数 GB B-tree 可能超过 120 秒。
5. Preview 只接受 `diagnostics.telemetry`、`diagnostics.runtime-activity`、`platform.duplicate-indexes`、`code-index.obsolete-scopes`，有效期十分钟。传入表名、路径或 `session_event_log` 必须返回 400。
6. 清理后行数下降但文件大小未下降时，检查执行结果 Warning：空间不足、活动写事务或锁竞争会让 checkpoint/VACUUM 失败。安全删除不会回滚；释放到操作系统的字节数以 `bytesBefore/bytesAfter` 为准，不能用 deletedRows 推断。
7. `session_event_log`、`conversation_events`、ChatMessages 和记忆始终应显示受保护。若它们出现可清理按钮，视为阻断性回归。

测试使用系统 Temp 的隔离 DataRoot：`StorageMaintenanceServiceTests` 验证白名单、权威表保护、重复索引和失效代码作用域；`StorageManagementAuthorizationTests` 验证两条授权路径；`CoreStorageManagementClientTests` 验证 Desktop 路由与 Token Header。禁止用自动测试直接清理 `D:\data`。

## 11.22 Desktop 构建成功但新路由仍是空 404 / Layout PUT 被 SQLite writer 阻塞

`POST /desktop/bootstrap/start` 返回构建成功和 Core Ready 后，仍必须调用本次变更的业务路由。若旧路由可用，而新增路由返回无 JSON body 的空 404，按最终入口产物诊断：

1. 比较 `Source/PuddingPlatform/bin/<Configuration>/<TFM>/PuddingPlatform.dll` 与 `Source/PuddingAgent/bin/<Configuration>/<TFM>/PuddingPlatform.dll` 的时间、长度和 SHA-256；运行进程的 Module Path 也必须落到后者。
2. 项目输出已更新但入口副本仍旧时，说明增量构建没有刷新传递依赖。通过 Desktop Bootstrap API 原子停止 Core，执行 `dotnet build Source/PuddingAgent/PuddingAgent.csproj --no-restore --no-incremental --nologo`，确认两份 DLL 哈希一致，再通过 API 启动 Core。
3. 最终以新增业务路由的 JSON 状态码验收；`/admin/orchestration` 的 200 可能只是 SPA fallback，不能证明 Controller 已加载。ControlToken 只放 `X-Control-Token`，不得输出到日志或回复。

Layout PUT 若在非法 `baseRevisionId` 上仍等待到客户端超时，检查 `SqliteAgentOrchestrationStore.SaveLayoutAsync` 是否在读取不可变 Revision 之前就开启了 serializable transaction。SQLite 会把它视为潜在写事务并等待无关 writer，导致本应立即返回的 4xx 被锁竞争拖住。正确边界是：

- 先用只读连接校验 immutable base Revision、GraphId、nodeId 和 parentNodeId；
- 再用短 serializable transaction 读取当前 layout revision，并执行 CAS INSERT/UPDATE；
- 用一个持有未提交写事务的测试证明缺失 Revision 仍能在两秒内返回 NotFound。

2026-08-10 运行态基准：修复前缺失 Revision 的 PUT 超过 15 秒超时；修复并由 Desktop 加载新入口产物后，同一路由 31 ms 返回 `404 orchestration.layout_base_revision_not_found`。定向 Orchestration 测试为 18/18。

## 11.23 Token 月度统计明显小于 Provider 官网 Usage

先确认比较口径：页面默认“全部 Provider”，官网若按 DeepSeek API Key 筛选，页面也必须选择
`providerId=deepseek` 和相同月份。DeepSeek 官网 Usage 月度导出是共享 API Key 的最终账单；
Pudding 本地只能统计实际收到 usage 的调用。

如果页面远小于官网，不要先改价格或人工补总数，按三层事实对账：

1. `TokenUsageEvents` / `TokenUsageStats` 是会话、角色、上下文归因投影，不能当作 Provider
   请求计费账本。统计它们的 `source_type/provider_id/model_id`，确认是否只覆盖了主 Agent
   `usage.recorded` 或少量 subagent run 汇总。
2. `runtime_activity(component=llm_gateway,status=succeeded)` 是成功网关请求索引。非流式活动的
   metadata 应含 prompt/completion/cache usage；流式请求必须与同 workspace/session 下按时间排序的
   `session_event_log(event_type=usage)` 一一配对。数量不等时不得按最近时间猜配。
3. `llm_gateway_usage_events` 才是 Admin 月度/趋势的本地计费事实：同一 `source_id` 唯一，
   一次 Provider usage 一行。页面应显示 `dataSource=local_gateway`；旧月份无该表事实时才显示
   `legacy_projection`。

通过 `POST /api/stats/tokens/rebuild?yearMonth=yyyy-MM` 重建时关注
`gatewayActivitiesScanned/gatewayEventsCreated/gatewayFactsSkipped/errors`。重建只替换能成功覆盖的
`runtime-activity:*` sourceId，不能删除活动缺失的实时事实；早期 `llm:*` 行用 operation、
workspace、session、provider、model、startedAt 和 token 数的完整身份去重。

2026-08-09 运行态证据：旧投影是 4,567 次、651.3M Tokens、¥93.4425；重建 8 月
15,817 个成功网关活动、`gatewayFactsSkipped=0/errors=0` 后，DeepSeek 为 12,073 次、
1,360,951,650 Tokens、¥377.508061。同期官网按该 API Key 为 12,171 次、
1,365,533,317 Tokens、¥381.22。剩余 98 次/4,581,667 Tokens 来自本地没有 usage 的取消、
失败或同 Key 外部调用，不能伪造为本地明细；需要完全一致时导入官网月度 CSV 再做独立对账。

## 11.24 Runtime 移除 Platform 引用后的跨层编译错误

当 `PuddingRuntime` 出现 `CS0246`，缺少 `PlatformDbContext`、Platform Entity 或具体 Platform Service 时，先检查 `PuddingRuntime.csproj` 的依赖方向。不要为了消除错误重新添加 `PuddingPlatform` 项目引用，否则会恢复 Runtime → Platform 的反向耦合。

按依赖性质处理：

1. Runtime 自有能力（例如文件工具的大文件分块读取）直接移动到 Runtime，并同步测试命名空间。
2. Runtime 只需要调用行为时，把最小接口和跨层 DTO 放入 `PuddingCore`，由 Platform 实现并在 Host/Agent 组合根注册接口别名；不要把 EF Entity 暴露给 Runtime。
3. Runtime 需要诊断数据时，在 Core 仓储契约增加投影查询，由 Platform 仓储完成 EF 查询；Runtime 不直接解析 `PlatformDbContext`。
4. 完成后依次构建 `PuddingRuntime`、`PuddingPlatform`、`PuddingHost`、`PuddingAgent`，并运行涉及迁移接口的 Runtime/Platform 定向测试。构建输出放在 `.tmp-build`，测试结果放在 `.tmp-test-out`。

本次边界实例：`SubAgentTool → ISubAgentPool → SubAgentPool`、`AgentDiagnosticsTool → ITokenUsageEventRepository → TokenUsageEventRepository`；`FileChunkService` 归属 Runtime。若 Runtime 源码仍出现 `using PuddingPlatform` 或 `PuddingPlatform.*`，视为分层回归。
