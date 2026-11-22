# HarnessAgent.Core
> 源指纹: Source/HarnessAgent.Core/**=30-740b6f9b1021 · 条目数: 29 · 最近整理: 2026-11-22
> 定位：独立的 .NET 10 Harness 骨架（BCL + Playwright）。模型提供者、MCP 双向、记忆书库、连接器与桌面自动化。
> 无 ProjectReference：不引用任何 Pudding 程序集，也不是宿主装配点。

## 入口与配置

模型侧从 `Provider/IModelProvider.cs` 进入，注册与解析在 `Provider/ProviderRegistry.cs`；聊天链在 `Middleware/ChatPipeline.cs`。
飞书与邮件连接器各自带取材策略（`Connectors/Feishu/FeishuFileRoots.cs`、`Connectors/Email/EmailRecipientPolicy.cs`），
允许根与白名单可由 `PUDDING_FEISHU_FILE_ROOTS` / `PUDDING_EMAIL_RECIPIENTS` 整体覆盖。

## 文件

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Browser/BrowserControl.cs` | Playwright 浏览器自动化薄封装（导航与取文本） | `BrowserControl`、`LaunchAsync`、`ConnectAsync`、`GoToAsync` | — | — |
| `Compaction/CompactionEngine.cs` | 按上下文窗占用比判定压缩档位与尾部预算 | `CompactionEngine`、`CompactionDecision`、`CompactionTier`、`CompactionThresholds` | — | `ShouldCompact` 只在 Aggressive 及以上为真；占用比而非绝对 token 数才是判据 |
| `Computer/CodexIntegration.cs` | 把 Codex 桌面操作包装成命令注入与重启 | `CodexIntegration`、`SendCommandAsync`、`FullRestartWorkflowAsync` | `Computer/SelfHealRestart.cs` | — |
| `Computer/ScreenCapture.cs` | 显示器枚举与多屏位图采集 | `ScreenCapture`、`MonitorInfo`、`GetMonitors` | `Computer/WindowAutomation.cs` | — |
| `Computer/SelfHealRestart.cs` | 按缓存的窗口布局向 Codex 输入框注入命令 | `SelfHealRestart`、`CodexLayout`、`RestartResult`、`ExecuteRestartAsync` | `Computer/WindowAutomation.cs` | 输入框与按钮偏移是按某次窗口尺寸写死的像素，换机器或缩放窗口前必须先 Discover 覆写 `codex-layout.json` |
| `Computer/WindowAutomation.cs` | user32 的窗口查找与鼠标键盘注入封装 | `WindowAutomation`、`FindWindowByTitle`、`Focus`、`TypeText` | `Computer/SelfHealRestart.cs` | — |
| `Connectors/Email/EmailRecipientPolicy.cs` | 邮件收件人白名单判定 | `EmailRecipientPolicy`、`Resolve`、`Evaluate` | `Connectors/Feishu/FeishuFileSourcePolicy.cs` | 未在白名单内的地址一律拒绝；白名单外没有"临时放行"分支 |
| `Connectors/Feishu/FeishuClient.cs` | 飞书 HTTP API 的鉴权与消息投递封装 | `FeishuClient`、`SendMessageAsync`、`ReplyTextAsync`、`UploadFileAsync` | `Connectors/Feishu/Models.cs` | token 缓存提前五分钟失效重取；去掉提前量会在临界点拿过期 token 发送失败 |
| `Connectors/Feishu/FeishuFileRoots.cs` | 解析文件投递的允许根 | `FeishuFileRoots`、`Resolve`、`OverrideVariable` | `Connectors/Feishu/FeishuFileSourcePolicy.cs` | 允许根只在此处解析一次并传给取材策略；两处各解析一次会让投递与取材判据分叉 |
| `Connectors/Feishu/FeishuFileSourcePolicy.cs` | 文件投递的取材许可与凭据拦截 | `FeishuFileSourcePolicy`、`Evaluate`、`IsCredentialLike` | `Connectors/Feishu/FeishuFileRoots.cs` | 凭据拦截按名字前缀与扩展名覆盖变体；精确名单会漏放行 |
| `Connectors/Feishu/FeishuFileSplitPlanner.cs` | 超过单文件上限时的分片规划 | `FeishuFileSplitPlanner`、`Plan`、`PartCount`、`MaxSingleFileBytes` | `Connectors/Feishu/FeishuFileTypes.cs` | 30MB 是飞书单文件硬限；分片必须带编号，接收方按同一编号重组 |
| `Connectors/Feishu/FeishuFileTypes.cs` | 按文件名推断 file_type 与分段 Content-Type | `FeishuFileTypes`、`Resolve`、`FeishuFileDescriptor` | `Connectors/Feishu/FeishuClient.cs` | — |
| `Connectors/Feishu/FeishuPostContentConverter.cs` | 飞书富文本 post 转 Markdown | `FeishuPostContentConverter`、`ConvertToMarkdown`、`SelectPayload` | `Connectors/Feishu/MessageMapper.cs` | — |
| `Connectors/Feishu/FeishuRecipientResolver.cs` | 收件人兜底判定：显式 open_id 优先于 chat_id | `FeishuRecipientResolver`、`Resolve` | `Connectors/Feishu/FeishuClient.cs` | 收件人类型与 receive_id_type 必须同源；混用会被飞书拒收 |
| `Connectors/Feishu/FeishuWebSocket.cs` | 飞书长连接的帧接收与分片合并 | `FeishuWebSocket`、`ConnectAsync`、`ProtobufFrame` | `Connectors/Feishu/FeishuClient.cs` | 分片按消息 id 合并后才是完整事件；只读单帧会得到截断的负载 |
| `Connectors/Feishu/MessageMapper.cs` | 飞书事件到内部消息格式的字段映射 | `MessageMapper`、`ExtractText`、`ExtractImageKey`、`ExtractFileKey` | `Connectors/Feishu/Models.cs` | — |
| `Connectors/Feishu/Models.cs` | 飞书配置与请求响应 DTO | `FeishuConfig`、`SendMessageRequest`、`SendMessageResponse`、`UploadFileResponse` | `Connectors/Feishu/FeishuClient.cs` | — |
| `Mcp/JsonRpc.cs` | JSON-RPC 2.0 报文的序列化与分类 | `JsonRpc`、`Request`、`Response`、`ErrorResponse`、`Notification` | `Mcp/McpClient.cs`、`Mcp/McpServer.cs` | 请求、响应、通知与错误必须按 id 与 error 字段区分；少了通知分支会被当成空响应 |
| `Mcp/McpClient.cs` | MCP 客户端的握手与工具调用 | `McpClient`、`InitializeAsync`、`ListToolsAsync`、`CallToolAsync` | `Mcp/McpTypes.cs` | 握手未完成时能力集为空，此时调用工具没有意义 |
| `Mcp/McpServer.cs` | MCP 服务端：注册工具并按 JSON-RPC 分派 | `McpServer`、`RegisterTool`、`HandleRequestAsync` | `Mcp/JsonRpc.cs` | — |
| `Mcp/McpTypes.cs` | MCP 的工具与资源描述类型 | `McpTool`、`McpResource`、`McpServerCapabilities`、`IMcpTransport` | `Mcp/McpClient.cs` | — |
| `Memory/MemoryEntry.cs` | 记忆条目与关系图谱的类型定义 | `MemoryEntry`、`MemoryBook`、`MemoryKind`、`MemoryRelation` | `Memory/MemoryLibrary.cs` | — |
| `Memory/MemoryLibrary.cs` | 进程内的记忆书库与文本检索 | `MemoryLibrary`、`CreateBook`、`AddEntry`、`Search` | `Memory/MemoryEntry.cs` | 纯内存实现且不落盘；持久记忆不走本类 |
| `Middleware/ChatPipeline.cs` | 聊天调用的中间件链与内建中间件 | `ChatPipeline`、`Use`、`ExecuteAsync`、`ChatMiddlewareDelegate` | `Provider/IModelProvider.cs` | — |
| `Provider/ChatMessages.cs` | 聊天消息与模型响应的消息契约 | `ChatMessage`、`ChatCompletionRequest`、`ChatCompletionResponse`、`ToolCall` | `Provider/IModelProvider.cs` | — |
| `Provider/IModelProvider.cs` | 统一的模型提供者接口 | `IModelProvider` | `Provider/ProviderRegistry.cs` | — |
| `Provider/ModelDescriptor.cs` | 模型的上下文窗与计价等元数据 | `ModelDescriptor` | `Provider/ProviderRegistry.cs` | — |
| `Provider/ProviderRegistry.cs` | 提供者注册与按模型 id 解析 | `ProviderRegistry`、`Register`、`ResolveModel`、`ListModels` | `Provider/IModelProvider.cs` | — |
| `Tools/GitHubCli.cs` | gh 命令行封装的 issue 与 PR 操作 | `GitHubCli`、`ListIssuesAsync`、`CreateIssueAsync`、`CreatePrAsync` | — | 依赖本机已登录的 gh；凭据缺失属于调用前置条件，不是空结果 |
