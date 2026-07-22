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
SmartWorkflowToolBase(originToolId/model/limits)
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
  不是单个工具死锁；应检查 Prompt 是否要求自包含结果，以及角色的 round/timeout 上限；
- 标记为 `ReadOnly` 的 Smart 工具仍拿到 `file_write/shell/spawn_sub_agent`：描述符与
  capability 白名单不一致，会放大耗时和副作用，必须改为显式只读白名单。

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
4. 如果报告已有五段仍被拒绝，检查每段是否真的有内容；`SUMMARY` 少于 40 字符或
   `EVIDENCE` 少于 60 字符也会失败。
5. 不要在主 Agent 侧把短结果补写成成功报告，也不要自动无限重试。修正对应角色的
   Prompt/模型后重新调用，避免悄悄重复消耗 Token。

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

- 默认预算应为 1800 秒；
- 只有 Planner 执行快照设置 `AllowSubDelegation=true`；
- Planner capability whitelist 只包含 `smart_explore`；
- Explorer 的下一层委派开关必须为 false；
- `DelegationDepth >= MaxDelegationDepth` 时，`PuddingToolRegistry` 必须在调用前拒绝。

正确终态不变量：

```text
deadline reached  => timed_out
explicit cancel   => cancelled
terminal totals   => 与此前持久 run 事件一致
terminal count    => 1
```

### 7.7 主 Agent 显示“异常”，子代理预算却还没有结束

典型症状：Smart 工具声明 1800 秒，但主 Agent 在约 1200 秒先进入“异常”；子代理
检查器仍显示 Running，或者子代理刚完成而父 Turn 已写
`runtime_execution_failed / 执行超时 (1200s)`。

这不是“把子代理超时再加长”可以解决的问题。根因是父 Turn 与子代理分别从各自调用
时刻计算相对 timeout：子预算晚于父预算，父取消令牌会在子代理提交结果前切断整条
工具链。

诊断顺序：

1. 查 `execution_commands / execution_runs / execution_journal`，先确认父 Turn 的
   `terminal_code` 和实际耗时；`执行超时 (1200s)` 表示父级预算先耗尽。
2. 查活动 Agent 的 `manifest.json.maxElapsedSeconds`，不要只看 Smart 工具常量。
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
6. 正确终态必须为 `execution_timeout`，而不是
   `runtime_execution_failed` 或普通 `cancelled`。

当前预算不变量：

```text
parent deadline = Turn 启动时冻结一次
shared ceiling  = 1800s
smart_plan cap  = 600s / 48 rounds / read-only
smart_explore   = 180s / 32 rounds / read-only
parent reserve  = 120s
child deadline <= parent deadline - reserve
downstream      = 只能收紧，禁止放宽
```

### 7.8 `ResponseEnded` 后 Agent 联系人持续显示“异常”

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

1. **中等富文本会话仍被虚拟化**：检查 `chat-message-viewport-content` 的 `data-virtualized`。少于 80 个 timeline row 应为 `false`；80-199 个 row 只在全部为 compact 稳定短行时虚拟化；否则高 Markdown/tool row 会经历估高到实测的短暂覆盖。
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

回归测试至少覆盖“终态 cursor + user-only 快照强制全量追平”和“本地终态回复覆盖 user-only
canonical 快照且不显示等待占位”两个场景。浏览器验收必须在不刷新页面的前提下观察运行气泡和
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
