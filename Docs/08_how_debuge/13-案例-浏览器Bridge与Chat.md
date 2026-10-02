# 案例：浏览器 Bridge 与 Chat

> 本文档是 [How-Debuge 调试与诊断手册](README.md)（主索引）的主题分册，由原根目录 `How-Debuge.md` 于 2026-10-02 按主题拆分而来。
> 新增本主题的经验请直接追加到本文件；跨主题内容请回到主索引选择分册。

## 11.34 PuddingDesktop 调试模式（源码前后端 + 80 端口反向代理）

2026-08-23 新增。Desktop 设置页新增「调试模式（开发者）」卡片，启用并重启 Core 后：

```
Desktop(127.0.0.1:80) ──后端前缀(/api /swagger /health /healthz /metrics /assets /connectors /session-events)──> Core(源码构建, 8080)
                    └──其余路径(SPA fallback: /admin/xxx 无扩展名 → /admin/)──> 前端 dev server(pnpm start:dev, 8000)
```

- 后端：`dotnet build Source/PuddingAgent/PuddingAgent.csproj` 后以 `bin/Debug/net10.0/PuddingAgent.exe --desktop-child` 启动（Development 环境），监督/Ready 握手/健康检查/优雅关停全部复用现有 CoreProcessSupervisor。
- 前端：`cmd /c pnpm run start:dev -- --host 127.0.0.1 --port 8000`（cwd=Source/PuddingPlatformAdmin；node_modules 缺失时先自动 `pnpm install`）。
- 代理：Desktop 内 `DesktopReverseProxy`（HttpListener，`Source/PuddingDesktop/Debug/`），SSE 逐块 Flush 转发，WebSocket（HMR）全双工中继；路由语义与 dev-up.py 的 Python 代理逐条对齐。
- Workbench WebView2 导航到 `http://127.0.0.1/admin/`（代理），不再直连 Core；Storage/Browser Bridge/健康检查仍走 Core 真实地址。

### 开启方式

1. 系统设置 → 调试模式（开发者）→ 勾选启用，可编辑仓库根目录（留空自动向上解析）、前端端口（8000）、反向代理端口（80）、两个超时；保存。
2. 点「重启 Core」。配置持久化在 `%LOCALAPPDATA%\Pudding\desktop.json` 的 `debug` 节（`debug.frontendWorkingDirectory`/`debug.backendProjectPath` 可手改覆盖自动推导）。
3. 浏览器访问 `http://127.0.0.1/admin/user/login`（80 端口统一入口），前端改动 HMR 即时生效。

### 端口互斥（必须先停止 dev-up）

代理 80、前端 8000 与 dev-up 管理的端口完全冲突；同一 DataRoot 也禁止两个 Core 同时访问数据库。调试模式前先 `python dev-up.py --down`。

### 日志与错误位置

| 位置 | 内容 |
|---|---|
| 运行中心「最近 Core 输出」 | `[build]` 后端构建输出、`[Debug]` 调试组件启动、`[frontend]` 前端 stdout（FrontendDevSupervisor 环形缓冲，进程失败时错误信息自带最后 40 行） |
| 状态机 | 新增 `DebugFailed` 状态（红点 + 「调试组件失败」），构建失败/pnpm 失败/80 被占都会进入 |
| DesktopDiagnosticLog | `DebugProxyLoop`/`DebugProxyRequest`（代理请求级异常） |

### 常见症状

- 调试代理无法监听 127.0.0.1:80 → dev-up 的 Python 代理或 IIS 占用；`dev-up.py --down` 后重启 Core。
- DebugFailed 且错误带 `exit code` → `dotnet build` 失败，看运行中心日志尾部 `[build]` 行定位编译错误。
- DebugFailed 且错误带 `pnpm` → pnpm 不在 PATH，或 install/启动失败，错误自带前端日志尾部。
- 页面 502 `Proxy error` → 上游（Core 或前端 dev server）未就绪或已退出；Core 起来前 /api 短暂 502 属正常。
- HMR 不生效 → 确认页面确实经 80 端口访问（WebSocket 需经代理中继），而不是直连 8000。

## 11.35 DeepSeek 缓存命中率日报与 miss 归因（>99% 验收）

验收口径见《上下文Token效率缓存命中与分级压缩优化设计方案》§15.3：连续 7 个完整自然日、按 Token 加权总命中率
`>99%`；单日输入 ≥10M 的模型分组各自 `>99%`。日常检查直接运行：

```bash
python TestScripts/deepseek-cache-hitrate.py            # 最近 7 天逐日/分模型/归因/Top miss
python TestScripts/deepseek-cache-hitrate.py --days 3   # 短窗口
```

要点：
- 总量与命中率一律以 `llm_gateway_usage_events` 为准（与服务商账单对账，覆盖 ~99.8%）。`TokenUsageEvents`
  只用于归因：`PrefixChangeReason` 取值含 `session_rehydrated`（进程重启/内存会话过期后重水合，2026-08-25 起）、
  `system_prompt_changed`、`tool_spec_changed`、`memory_changed`、`few_shot_changed`、`prefix_hash_changed`。
- 归因桶规则：无 reason 的请求按 `miss/输入` 分为 full-rebuild（≥80%）、half-rebuild（40–80%）、partial
  （10–40%）、incremental（<10%）。incremental 是健康底噪；full/half-rebuild 桶占比升高说明重水合或窗口
  重建回潮。
- Top miss 表定位到具体会话后：`session_rehydrated` 或凌晨/早晨时段的大 miss 属于 provider 缓存过期后的
  重水合全量重传，检查水合预算（`runtime.execution.json` → `context.reassembly`）；白天成对出现
  `system_prompt_changed`/`tool_spec_changed` 属于前缀漂移，查 `composition_snapshot` 指标与 L1-TOOLS 层。
- 委派视觉（`vision-helper:{sessionId}`）与潜意识（`subconscious:{sessionId}` / `subconscious-memory`）调用
  是独立 sessionId，其结构性低命中不应计入主会话前缀漂移；报表第 3 节按 reason 分组时它们自带桶。

## 11.36 Agent 可见新消息却继续执行上一轮请求

典型症状：Chat UI 已显示新的长用户消息，但 Agent 的第一段 reasoning 仍复述更早的短命令（例如继续
列任务）；用户会感觉 Agent “停留在上一轮”。不要只凭 UI 归因给前端，也不要只查 `ChatMessages`。

按同一身份链排查：

1. 从新用户消息取得 `conversationId/messageId`，关联 `Command → Turn → Run → Trace`；确认
   `turn.accepted` 的 `userMessageId` 正是新消息，而不是旧消息或 steering。
2. 查该 Trace 的上下文活动：水合 `history_count`、`agent.history.inject_secrets` 的
   `message_count/system_user_message_count`、最终 `agent.llm.prepare`。若 `turn.accepted` 正确且网关
   收到预期消息数，说明不是“前端没发出”或“Runtime 丢了整条请求”。
3. 比对当前 `ChatMessages.Content` 的长度/SHA-256 与水合历史；再按同一 session 比较 platform DB
   `ChatMessages.Id/CreatedAt` 最大值和 memory DB 中 `Source=chat_transcript` 的稳定 MessageId 高水位、
   `CreatedAt` 最大值。若 platform 已前进而 memory 停在数小时前，即使当前输入围栏存在，冷启动仍会把旧命令
   作为最近历史交给模型；这属于 canonical 转录冷水合缺口，不是前端丢消息，也不能归因于模型随机性。
4. 2026-08-25 起，实际 provider 输入必须含最后一个
   `[CURRENT USER TURN input_sha256=…]…[/CURRENT USER TURN input_sha256=…]`；typed `ContentParts`
   同样必须有带同一 hash 的首尾围栏。
   `agent.llm.prepare`（Streaming）或 `subagent.llm.started`（Buffered）应有
   `current_message_id/current_input_sha256`。secret injection、软压缩或硬预算裁剪若移除了围栏，
   Runtime 应在 provider 前抛出 `Outbound LLM history is missing the accepted current user turn`，而不是
   静默发送旧历史。

判定分支：`turn.accepted` 指向旧 ID → 查 Dispatcher/ExecutionRunCoordinator；ID 正确但门禁报错 → 查
ContextBudget/历史投影；围栏与 hash 均正确而模型仍答旧任务 → 查实际 gateway request/provider 响应，
但必须先排除 platform/memory 高水位差。新构建在 DB 历史水合前应记录
`Canonical transcript synchronized before DB hydration`；缺少该日志或同步失败后仍读取 memory DB 都是回归。
修复 Runtime 后必须重启 Core 才能验证，新源码不能由承载它的旧进程自证已加载。

还要检查两个时序不变量：

- 当前 user 虽已先写入 `ChatMessages`，但 provider history 中只能出现一次，且必须是带
  `[CURRENT USER TURN input_sha256=…]` 的版本。若同一正文同时出现无围栏 DB 行与围栏行，检查
  `ChatMessageRow.turnId/messageId → MessageEntity.Metadata → BuildContextFromDbSnapshotAsync` 的当前 Turn 排除。
- `turn.completed` 之前 assistant 可能尚未物化到 `ChatMessages`。Streaming 后处理不得用此时的 DB 快照覆盖
  已包含 assistant/tool 的 live history；如果下一轮又缺少刚完成的 assistant，检查 `TrimHistoryAsync` 是否记录了
  pre-projection DB 覆盖，或自动压缩后是否漏合并当前 live Turn。自动压缩后的 live 尾部必须从 opening/closing
  配对且携带 64 位输入 hash 的当前轮围栏开始；若围栏缺失却仍把最后一条历史 user 合并为当前轮，属于 fail-closed
  门禁失效，会重新激活旧指令。

纯图片消息的 `Content` 可以为空；排查高水位时同时看 `content_parts_json`。若 after-Id 查询只筛非空正文，
或者 canonical hash 只覆盖正文，图片轮会被静默跳过或互相误判为同源。

## 11.37 Agent Harness 适配、低效工具循环与首块等待诊断

模型反复猜工具名、把“无匹配”当失败或在完整子代理报告后继续一轮时，先区分 Harness 协议不匹配、
工具真实失败、Runtime 循环控制和 Provider 等待，不要只看最终 `tool_result`：

1. 日志 `[ToolInvocation] Harness compatibility adapted tool=A->B callId=...` 表示统一执行入口已把熟悉调用归一化；
   `tool.harness_compatibility` 指标可按 `requested_tool/canonical_tool/adaptation_kind/adapter_version` 聚合别名命中。
2. `rg/grep/findstr` exit code 1 且无缺失命令诊断是 `status=NoMatch`，不是失败；应改变 query/path，不能原样重试。
   exit code 2、`CommandNotFoundException`、`command not found` 仍是真失败。
3. `subagent.output_contract.completed` 表示模型已输出完整五段报告但漏掉 Runtime JSON 信封，Runtime 在无 native
   tool call 且非结构化输出时同轮收口；显式结构化 `CONTINUE/WAIT/FAILED` 不得自动完成。
4. LLM 慢先比较 `llm.rate_limit.wait` 与 `llm.stream.provider_first_chunk_wait`：前者高是本地 provider/model
   并发排队，后者高而前者低才是 Provider 建连/首块慢。`chat_stream` metadata 同时带
   `rate_limit_wait_ms/stream_first_chunk_wait_ms/first_chunk_received/stream_no_chunks`。
5. Windows 下**窄范围精确匹配**优先 `search_grep`；**全仓检索先走索引工具**
   （`code_symbol_search` / `code_explore` / `file_search`），需要全仓**文本**检索时用 `git grep -n`
   ——`search_grep` 有 **2000 文件枚举上限**，硬扫会得到 partial 覆盖或**假阴性**
   （详见本文件顶部「全仓检索别用 search_grep 硬扫」一条）。需要真实 Bash/管道时显式 `shell=wsl`。当前 WSL 不保证安装 `rg`，
   每个 run 最多探测一次 `command -v`，不得让 Agent 自动 `apt install`。
6. 相同 canonical 工具、相同参数且失败结果未变化时，第二次结果应带
   `runtime_status=execution_stalled`；之后原样调用应在 Agent Loop 内阻断，不再出现新的底层
   `tool.execution`。改变参数、错误结果发生变化或执行成功会重置指纹。若仍反复执行，比较
   `tool.call` 与 `tool.execution` 的 callId/参数 hash，确认运行进程是否已加载新构建。
7. 2026-08-26 新构建起，`TokenUsageEvents` 的 `agent_llm` 行由 Agent Loop 从
   `RuntimeExecutionIdentity` 直写 `ParentSessionId/SubAgentId/TurnRound/ToolCallCount/ToolNames`；不得再用
   session 名称里的 `-sub-` 推断角色。`TurnRound` 为零基轮号，展示轮数时加一。
8. `SubAgentManager` 不再写 `source_type=sub_agent:*` 的终态 Token 汇总；终态 usage 仍在 sub-agent run
   摘要中。分析部署前历史数据时，旧 `sub_agent:*` 行与逐轮 `agent_llm` 是重复口径，禁止相加；不修改
   历史库，按部署时间切片或只选 `agent_llm`。
9. `tool.call` 是 Agent Loop 调用事实，新构建同时覆盖 Buffered 和 Streaming；`tool.execution` 是统一底层
   执行事实。被 `execution_stalled` 预阻断的调用可以有 `tool.call`，但不应新增 `tool.execution`，二者差值
   是循环控制证据，不是遥测丢失。
10. Streaming 的 direct Token 行应早于对应 `usage.recorded` 投影出现；`ConversationProjector` 只在 direct
    行缺失时补记。若同一调用仍出现两行，比较 direct `sourceId=session:trace:round` 与 eventId 行的时间，
    再检查新构建是否确实先写 TokenUsage、后发布 usage SSE。fallback 的未知 `ToolCallCount` 应为 NULL。

代码和单测通过只说明新构建可用；必须由进程外控制器重启 Core，再用新会话检查上述日志/指标和主、子代理
真实 smoke，才能证明当前产品进程已加载修复。

## 11.39 插嘴（Steering）已受理但 Agent 没有改变方向

插嘴不是取消：当前正在执行的工具调用或模型请求会自然结束，Runtime 在下一次 LLM 请求前注入；如果它在
可能成为最终回复的模型请求期间到达，新构建会在回复结束后的安全边界继续同一个 Turn。按以下证据链排查：

1. 前端检查 `chat.steering.submit/submitted/submitFailed`。`Enter` 是普通排队，`Ctrl/Cmd+Enter` 或本地队列
   闪电按钮才是插嘴；后端投递队列项不能直接转换，以免原投递和 Steering 重复执行。
2. 网络请求必须是
   `POST /api/v1/conversations/{conversationId}/turns/{turnId}/steering`，带 `X-Workspace-Id`。`202` 表示已写入
   Runtime 消费队列；`409` 表示 Turn 已终态或 Workspace/Agent 围栏不匹配。旧 workspace/session 路由的
   `404` 或 canonical 路由的 `501` 都说明当前客户端或 Core 仍是旧构建。
3. 查 `session_steering_messages` 的
   `steering_id/session_id/target_turn_id/agent_id/source_queue_item_id/status/consumed_round`：
   `pending` 长期不变说明 Runtime 未到安全边界或未加载新构建；`consumed` 说明已进入模型历史，不等于模型
   一定采纳指令。Runtime 只消费与当前 `RuntimeExecutionIdentity.TurnId` 精确匹配的行，旧库升级时无法绑定
   Turn 的历史 pending 行会被置为 `expired`，不得猜测后注入下一轮。同一 `source_queue_item_id` 的网络重试
   应返回相同 `steering_id`；若产生多行，检查是否绕过了 canonical Handler 或运行的是旧构建。
4. 日志链应依次出现 `[Steering] accepted`、`[SessionSteering] Consumed`、
   `[AgentExec:Steering] Injected`；若插嘴在最后一次模型生成期间到达，还应出现
   `[AgentExec:Steering] Continuing after ... late steering message(s)`。
5. `steering.injected` canonical event 和 `agent.steering.inject` activity 是实际注入证据；只看到 HTTP 202
   不能证明 Runtime 已消费。源码构建通过后仍需由进程外控制器重启 Core，再用新 Turn 做产品内 smoke。

## 11.40 本地待发消息在 Turn 结束后长时间仍显示“排队中”

先用同一 `sessionId/turnId` 对齐 Runtime、Journal、Coordinator 和 Worker 的终态时间，再查下一次
`SubmitTurn`/首个 `llm_gateway started`。若后端已四层 `Completed`、期间没有第二次 Submit，但随后又在无
用户操作时突然发送，这是前端本地队列 drain 的唤醒问题，不是 Agent 仍在运行或后端排队。

`useMessageInteractionQueue` 的 drain effect 必须读当前 render 的 `turns`，并订阅
`pendingSendQueue`。`useChatState` 在队列 hook 之后才用 effect 同步 `turnsRef.current`；如果 drain 读
ref，终态 render 会看到上一帧 `streaming`，而 ref 同步本身不会触发下一次 render，最终表现
为等到轮询或无关 UI 更新才“碰巧出队”。回归测试应专门复现“队列 hook 先运行、外层后同步
`turnsRef`”的 effect 顺序，不得用人为的第二次 busy→idle 切换才使断言通过。

## 11.38 u1s1 Provider：模型列表正常但推理返回 403

`https://api.u1s1.io/v1/models` 使用账号 API Key 可以返回模型列表，不代表同一 Key 可以从任意 OpenAI
兼容客户端发起推理。2026-08-26 实测无 u1s1 客户端凭据的 `/chat/completions` 返回
`403 / u1s1_client_only`；u1s1 官方首页同时声明当前所有 Token 只接受来自 u1s1 客户端的请求。

排查时依次区分：

1. `/models` 非 200：检查 endpoint、账号 Key 和网络；不得在日志、终端输出或诊断包中回显 Key。
2. `/models` 为 200、推理为 `u1s1_client_only`：模型 ID、Bearer 和 OpenAI JSON 并非根因；当前 Pudding
   没有 u1s1 浏览器批准的设备凭据与逐请求签名合同。
3. 不要猜测私有 header、冒充官方客户端或复制其他设备凭据。只有 u1s1 发布稳定的第三方客户端合同后，
   才能在 provider-specific gateway 中实现；不能把签名逻辑塞进通用 OpenAI gateway。
4. 直接编辑 `D:\data\config\llm.providers.json` 后，当前 Core 的 `PuddingFileLlmConfigService` 不会自动
   重新加载；必须由进程外控制器重启 Core，再检查管理端 Provider/Model 列表。

## 11.41 长会话 Chat CPU、内存与 DOM 持续增长

浏览器任务管理器中的高 CPU/内存只能证明页面进程有压力，不能直接区分事件投影、React 提交、Markdown、
布局或 WebView2 旧资源。先部署明确的新前端构建，再用同一会话按以下证据链判断：

1. 开启 Chat perf 诊断，检查 `chat.executionFlow.incrementalFlush`。正常单 Agent 流式阶段
   `changedTurnCount` 通常为 1，`pendingEvents` 在帧后为 0；若每个事件都随历史 Turn 数增长，说明仍在运行
   旧的“全事件 × 全 Turn”投影路径或静态资源未更新。
2. 重复 replay/bootstrap 事件应只出现 `chat.event.duplicateSkipped`，不得同时出现新的 incremental flush。
   session 切换后索引 stats 应从当前会话重新累计，不能保留上一会话 turns/events。
3. 在 Elements/Performance 面板检查 `data-testid="turn-content-stream"`：单个超长 Turn 默认只挂载最新 40 个
   内容块；展开的 `activity-group` 默认只挂载最新 24 个行为节点。较早节点只在点击“加载较早”后渐进增加。
   若一打开就出现数百 `toolcall-row`，运行的仍是旧前端。
4. `data-testid="chat-message-viewport-content"` 的 `data-virtualized=true` 表示消息级 virtualizer 已启用。
   若单条 Turn 很重但仍卡顿，优先查 Turn 级窗口；若大量独立消息同时挂载，查 render weight 与 viewport 阈值。
5. React Profiler 中更新一个活动 Turn 时，历史 `MessageRow` 不应提交。若全部行一起 commit，检查是否又把
   全局 selector/revision 当成 MessageRow prop；正确实现传入本 Turn 的具体 Projection 对象并按引用比较。
6. 代码门禁：运行 `executionFlowProjectionIndex.test.ts`、`TurnContentStream.test.tsx`、
   `MessageRow.memo.test.ts`，再执行 `npm run build`。全量 `tsc` 若仍命中既有 Admin Shell/DevPanel 基线，需确认
   输出中没有本节涉及文件的新错误，不能把基线失败描述为通过。

## 11.42 子代理只有 Browser Tools 并连续 `browser_not_available`

`No authenticated Desktop connected` 只说明 Browser Bridge 当时无认证 Desktop；如果任务本应读文件，
不要直接把根因归结为“重连 Desktop”。按以下顺序对齐证据：

1. 从父会话 `spawn_sub_agent` 的 tool event 记录 `sessionId/turnId/runId/subSessionId/template_id/permission_mode/tools`，
   先确认父代理要求的能力和实际委派参数。
2. 读 `<workspace>/agents/<agent>/runs/<runId>/events.jsonl` 的第一轮 LLM 请求，以 function schema/
   `Total: N tools available` 为可用工具事实；提示词中出现工具名不等于 schema 可调用。
3. 汇总同 Run `tools.jsonl` 的 `tool/success/error/args_hash`。如果同类错误很多而 args hash 持续变化，
   说明精确 tool+args 重试跟踪无法止损，必须再查 Runtime 失败族熔断。
4. 沿 `BuiltInAgentTemplates -> SubAgentTool.ResolveTemplate/BuildChildCapability -> CapabilityPolicy.GetAllEffectiveToolNames
   -> ToolExposurePlanner -> LLM schemas` 追踪。Low 投影的权威是 V2 `DefaultToolNames + RequiresGrantToolNames`，
   不能用把 V1 `AllowedToolNames` 求并后的测试代替实际投影验证。
5. 全库搜索同全名 `BuiltInAgentTemplates`；它必须只存在于 PuddingCore。Host 本地同名类会让
   Host/UI 测试与 Runtime 分别看到两份策略，造成构建通过但产品行为漂移。
6. 查 `RuntimeControlService` 的 `windowErrorCount/sameFingerprintCount/trigger`。精确调用第二次由
   `FailedToolCallTracker` 转 `execution_stalled`；参数改变但 kind+component+归一化错误不变时，
   第 5 次应触发 `same_failure_fingerprint` 熔断。配置总量阈值必须小于等于内部保留容量。
7. Buffered 终止应归档为 Failed 并携带 `Session fuse triggered` 摘要；若只看到 Cancelled，
   检查 `OperationCanceledException` 分支是否在通用取消之前读取 RuntimeControl Faulted 状态。

源码修复后的最低回归：Core 模板/RuntimeControl 定向测试、Runtime 提示/参数变化失败族测试、
WebApi 模板单程序集测试、`dotnet build PuddingRuntime --no-restore`。当前运行中的 Desktop/Core 不会自动加载新 DLL，
必须由进程外控制器重启后，再用新子代会话检查它能否首先看到 `search_tools`、并发现/调用 `file_read`。

