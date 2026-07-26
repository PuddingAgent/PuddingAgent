# MCP 客户端集成（V1）

## 1. 范围

Pudding V1 作为 MCP Client，把 Workspace 中启用的 MCP Server 工具接入统一 `IPuddingTool` 执行链。实现基于官方 .NET SDK `ModelContextProtocol.Core 1.4.1`，支持 Streamable HTTP、legacy SSE、HTTP 自动探测和本地 stdio 子进程；V1 不实现 MCP Resources、Prompts、Sampling 或 Pudding-as-MCP-Server。

MCP 不是聊天渠道。飞书/Web 等 Connector 负责消息收发，MCP 只负责向 Agent 提供受治理的外部工具。

## 2. 配置

MCP Server 复用 Workspace Skill CRUD，`skillType` 固定为 `MCP`。示例：

HTTP Server：

```json
{
  "endpoint": "http://127.0.0.1:3100/mcp",
  "transport": "streamable_http",
  "allowPrivateNetwork": true,
  "connectionTimeoutSeconds": 15,
  "callTimeoutSeconds": 60,
  "maxResultChars": 262144,
  "maxReconnectionAttempts": 5,
  "bearerTokenSecretId": "kv-mcp-example"
}
```

- `endpoint` 只接受绝对 HTTP/HTTPS URL，禁止在 URL 中嵌入凭据。
- 公网 Server 必须使用 HTTPS。
- loopback、内网、链路本地和保留地址只有在可信本地开发场景显式设置 `allowPrivateNetwork=true` 才可访问。
- Bearer Token 只能引用 KeyVault ID；Skill 配置不存储明文密钥。
- 配置采用严格 JSON，未知字段直接拒绝，避免拼写错误静默失效。

本地 stdio Server（以 Codex 为例）：

```json
{
  "transport": "stdio",
  "command": "codex",
  "arguments": ["mcp-server"],
  "workingDirectory": "E:\\github\\AgentNetworkPlan\\PuddingAgent",
  "connectionTimeoutSeconds": 30,
  "callTimeoutSeconds": 3600,
  "maxResultChars": 262144,
  "shutdownTimeoutSeconds": 5
}
```

- `command` 是单个可执行文件路径或 PATH 中的裸命令名，不接受拼接后的 shell 命令行；参数必须逐项写入 `arguments`。
- `workingDirectory` 如提供，必须是绝对路径且在连接时存在。
- stdio 不允许同时配置 `endpoint` 或 HTTP Bearer Token。
- SDK 直接启动并托管子进程，不经过 `cmd.exe`/PowerShell；Session 释放时终止对应进程。
- 子进程不会继承 Pudding 的完整环境变量，只传递 SDK 提供的 OS/runtime 白名单，避免把宿主中的其他云凭据泄漏给第三方 Server。
- stdio Skill 等价于允许宿主启动本地程序，只能由受信任管理员配置。`command` 和 `arguments` 不得由 Agent 输入动态生成。

管理端在创建、修改、禁用或删除 MCP Skill 后立即触发该 Workspace 的连接重建。运行状态可通过 `GET /api/workspaces/{workspaceId}/skills/{skillId}/runtime-status` 查询。

## 3. 生命周期与工具发现

`McpConnectionManager` 为每个启用的 Workspace MCP Skill 持有独立 SDK Client Session：

1. 后台服务启动时读取所有启用的 MCP Skill。
2. SDK 完成 `initialize` / `notifications/initialized` 协商。
3. 使用 `tools/list`（含 SDK 分页处理）获取工具快照。
4. 收到 `notifications/tools/list_changed` 后原子替换该 Server 的工具快照。
5. Skill 变更时先构造新 Workspace 快照，再替换旧快照并释放旧 Session。
6. 宿主退出时释放所有 Session；拥有会话的 Streamable HTTP Client 会关闭远端 Session，stdio Client 会关闭托管子进程。

连接或发现失败采用 fail-closed：状态显示 `Unavailable`，对应工具不进入注册表，也不会复用陈旧工具定义。

## 4. Pudding 工具治理

每个远端工具由 `McpPuddingTool` 适配为 `IPuddingTool`：

- Tool ID 使用 `mcp__{server}__{tool}` 的稳定哈希命名空间，只含字母、数字和下划线。
- MCP JSON Schema 的完整结构保存在 `RawJsonSchema`，构造 LLM function schema 时原样传递；扁平参数投影仅供现有管理界面展示。
- 远端 description、annotations 和执行结果均视为不可信内容。
- 所有 MCP 工具固定为 `High` 权限、`RequiresNetwork`、`MainAgentOnly`，即使远端声明 read-only 也仍经过 Pudding 运行时审批。
- 调用前后二次校验 `WorkspaceId`，禁止跨 Workspace 发现或调用。
- 参数必须是 JSON Object；调用有独立超时，结果有字符上限，MCP `isError=true` 映射为 Pudding 失败。
- 文本结果直接返回；结构化或多模态 Content 以 JSON envelope 返回，避免丢失协议信息。

启用 Workspace MCP Skill 代表 Workspace 级能力授权，工具会加入该 Workspace Agent 的候选能力，但实际调用仍由统一 Capability Policy、Firewall、审批和审计链决定。

## 5. Codex 任务语义

`codex mcp-server` 会发现两个工具：

- `codex`：使用 `prompt`、`cwd`、`model`、`sandbox`、`approval-policy` 等参数创建新的 Codex thread。
- `codex-reply`：使用首个调用返回的 `structuredContent.threadId` 和新 `prompt` 继续已有 thread。

Pudding 的 MCP 结果格式化器会保留完整 `structuredContent`，因此 `threadId` 会进入 Tool Result 和会话历史；Agent 后续必须使用同一 `threadId` 调用 `codex-reply`，不能把最终文本当作任务身份。当前 V1 由 Agent 在同一 Pudding 会话内编排该 ID；跨会话的 Codex 任务目录和独立任务管理 API 不在本轮范围内。

## 6. 网络与进程安全

公网模式使用自定义 `SocketsHttpHandler.ConnectCallback`：每次实际建立连接时重新解析 DNS，只连接公网地址，从而覆盖初始连接、重连和 HTTP Redirect，并阻断 DNS 解析到 loopback、RFC1918、CGNAT、链路本地、文档/基准测试网段、IPv6 ULA 等地址。显式 `allowPrivateNetwork=true` 会关闭这项限制，因此只能用于受信任的本地 Server。

stdio 模式不经过网络 SSRF 防线；其安全边界是管理员配置、直接进程启动、参数数组、受限环境变量、Workspace 工具隔离以及统一 High-risk 审批链。MCP Server 的 stdout 必须只输出 JSONL 协议消息，诊断信息必须写 stderr。

## 7. 验证

- `Tests/Mcp.Cli`：严格 HTTP 假 Server 验证初始化生命周期、`tools/list` 分页、`tools/call`、Session/Protocol Header 和 Session DELETE；`--stdio-server` 提供协议纯净的 fake Codex JSONL 子进程。
- `McpConnectionManagerTests`：验证本地 Streamable HTTP 发现/执行/禁用/Workspace 隔离，以及真实 stdio 子进程的 `codex` → `threadId` → `codex-reply` 往返。
- `McpServerConfigTests`：严格 HTTP/stdio 配置、SSRF 地址判定、命令/参数边界、原始 JSON Schema 和稳定 Tool ID。
- `WorkspacePuddingToolRegistryTests`：Workspace 动态源隔离及重复 Tool ID 防线。

## 8. 后续版本

OAuth 动态注册、Resources/Prompts、细粒度 Agent-to-MCP 绑定、跨 Pudding 会话的 Codex 任务目录、Codex app-server 流式 thread/turn 投影和 MCP 管理 UI 状态展示留到 V2。新增能力必须继续通过 Workspace 隔离和统一工具治理边界，不能直接绕过 `IPuddingToolExecutionService` 调用 SDK。
