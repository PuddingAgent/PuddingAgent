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
| `tmp/dev/supervisor.out.log` | 守护进程启动、重启、健康检查 |
| `tmp/dev/supervisor.err.log` | 守护进程异常 |
| `tmp/dev/backend.out.log` | 后端控制台日志，排查启动与聊天链路的首选 |
| `tmp/dev/backend.err.log` | 后端进程级错误和启动失败 |
| `tmp/dev/frontend.out.log` | 前端编译与开发服务器输出 |
| `tmp/dev/frontend.err.log` | 前端编译、模块加载错误 |
| `tmp/dev/proxy.out.log` | 反向代理请求与上游状态 |
| `tmp/dev/proxy.err.log` | 端口占用、代理连接失败 |
| `tmp/dev/health.status.json` | 最近一次健康探测结果 |

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
Get-Content .\tmp\dev\health.status.json
```

如果健康检查不是 200，先检查启动、编译、DI 和端口，不要先调试聊天 Controller：

```powershell
Get-Content .\tmp\dev\backend.err.log -Tail 200
Get-Content .\tmp\dev\backend.out.log -Tail 400
Get-Content .\tmp\dev\supervisor.err.log -Tail 100
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

`SubAgentIndicator` 不允许通过恢复 5 秒轮询“修复”Running。轮询只会掩盖
事件链断点；应修复产生、归档、投影或 reducer 中真实断裂的一层。

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
| 消息滚动突然跳动 | 前端 `useMessageViewportRuntime`：检查 `data-virtualized`、row 实测高度、历史 prepend anchor、`followMode` 和每帧 scroll 读取次数 |
| Memory 缺表 | `MemoryDbInitializer`、`MemoryLibraryDbInitializer`、`Program.cs` 启动顺序 |

### 10.1 Chat 滚动跳变诊断

先区分三类原因：

1. **短会话仍被虚拟化**：检查 `chat-message-viewport-content` 的 `data-virtualized`。少于 40 个 timeline row 应为 `false`；否则高 Markdown/tool row 会经历估高到实测的总高度校正。
2. **历史前插未恢复锚点**：滚动到顶部加载旧消息前后，记录第一条可见 row 的 `data-viewport-item-id` 和相对 viewport top。二者应保持不变；不能只比较 `scrollTop`。
3. **贴底抢滚动**：用户阅读历史时 `followMode` 必须为 `off`；仅 `user-send`、手动回底部或 pinned 模式允许写入底部位置。

性能检查：

- 连续触发多个 `scroll` event，同一 animation frame 内 `scrollHeight` 只能读取一次；
- 短时间线连续上下滚动时 `scrollHeight` 应保持稳定；
- 历史 prepend 后，正常文档流的 `scrollTop` 增量应等于新增内容高度，第一条可见 row 的屏幕位置不变；
- 不要在 `MessageList`、`useChatState` 或子组件再注册第二套滚动修正逻辑。

## 11. 测试诊断

### 11.1 DI 接口未注册导致 Null Service

**症状**: 测试中 `GetService<TInterface>()` 返回 null，但 `TImplementation` 已注册。

**根因**: `services.AddSingleton<ConcreteType>()` 只注册具体类型，不注册接口。`GetService<IInterface>()` 返回 null。

**修复**: 使用 `services.AddSingleton<IInterface, ConcreteType>()` 注册接口映射。

**检索关键词**: `Sequence contains no elements`, `IChatTranscriptWriter`, `transcriptWriter is null`

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
