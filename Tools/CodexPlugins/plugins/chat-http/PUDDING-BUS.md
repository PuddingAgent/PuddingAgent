# Chat HTTP 接入 PuddingAgent 总线

开启 PuddingAgent 外部 API，在「系统配置 → 外部 Agent」为一个明确指定的 Codex 会话签发 AK/SK。准备一个由当前用户控制的私有 JSON 文件。SK 只在签发时显示一次。每个外部 Agent 使用一份独立凭据和一个会话。

```json
{
  "baseUrl": "http://127.0.0.1:8080",
  "workspaceId": "default",
  "agentId": "<管理页返回的 Agent ID>",
  "ak": "<AK>",
  "sk": "<SK>",
  "sessionId": "<Codex 会话 UUID>",
  "chatPort": 47931
}
```

在 Chat HTTP 插件目录运行以下命令。将 `$configPath` 设为私有配置文件路径。订阅进程负责注册、续租和持久游标。结束订阅时按 Ctrl+C。发送命令可在另一个终端执行。

```powershell
node .\bridge.mjs start
node .\pudding-bus.mjs run $configPath
```

```powershell
node .\pudding-bus.mjs status $configPath
node .\pudding-bus.mjs agents $configPath
$operationId = [guid]::NewGuid().ToString()
node .\pudding-bus.mjs send $configPath $targetAgentId $operationId $textFile
node .\pudding-bus.mjs delivery $configPath $messageId
$taskOperationId = [guid]::NewGuid().ToString()
node .\pudding-bus.mjs delegate $configPath $targetAgentId $taskOperationId $textFile
node .\pudding-bus.mjs result $configPath $messageId
```

`send` 和 `delegate` 从 UTF-8 文件读取正文。重试时复用操作 ID 和正文。接收端将文本通知和委派任务送到配置中的会话。通知在进入 Codex 持久队列后推进游标。委派在对应 Codex 轮次结束后回写结果，再推进游标。发送端查询 `delegate` 返回的 `messageId` 获取状态与答复。

`agents` 同时列出同一工作区的内置 Agent 和已注册的外部适配器。发送给内置 Agent 的文本可用 `delivery` 查询持久接收，委派通过内置 Agent 的已绑定主会话执行。

具备消息工具权限的内置 Agent 可用 `list_agents` 获取地址，用 `send_message` 的 `to=agent:<外部 Agent ID>` 发文本，或加 `intent=delegate` 委派任务。Codex 的终态以被动通知送回发送 Agent 的主会话。

订阅按收件顺序处理。委派未得到终态时会暂缓其后的消息。Codex 若仍由另一个客户端驱动，请让原客户端完成本轮并消费队列。若订阅进程突然退出，原租约到期后才能重新注册。接收游标保存在 Chat HTTP 的用户状态目录。要迁移到另一会话，请为新会话签发独立的注册凭据。

外部 Agent 接口和鉴权合同见 PuddingAgent 管理页的 OpenAPI 文档。可用 [隔离集成测试](../../../../TestScripts/test-codex-chat-http.mjs) 验证实际 app-server 与 Core。
