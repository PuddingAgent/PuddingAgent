# Codex 会话 HTTP

Windows 本机的应用可以用 HTTP 读取 Codex 聊天、向指定聊天投递消息，以及创建、选择、改名、归档和恢复聊天。默认监听 `127.0.0.1:47931`。需要 Node.js 22.17+ 和支持 `thread/queue/add` 的 Codex CLI。本机验证版本为 `0.167.0-alpha.3.2`。

## 启动与停止

在已安装插件的 Codex 聊天中使用 `$chat-http`，指定“启动本地 HTTP 服务并绑定这个聊天”。也可从插件目录执行以下命令。启动器在后台运行，首次绑定启动环境的 `CODEX_THREAD_ID`。从普通终端启动时可通过 `PUT /v1/current` 选择聊天。重复 start 返回现有监听器的状态。用同一端口的 stop 关闭服务。

```powershell
node .\bridge.mjs start
node .\bridge.mjs status
node .\bridge.mjs stop
```

可在命令末尾指定 `--port 47932`。服务使用独立的 Codex app-server 客户端，通过 stdio 通信并复用用户的本机会话存储。终端启动时，桌面版 `codex.exe` 需在 PATH 中，也可以用 `CODEX_APP_RUNTIME_CLI_PATH` 指定其路径。

## 收发消息

从当前 Windows 用户的连接文件读取服务地址和令牌。每次重新启动都会更换令牌。服务要求 Bearer 鉴权，只监听 IPv4 回环地址。

```powershell
$c = Get-Content "$env:LOCALAPPDATA\CodexChatHttp\connection-47931.json" -Raw | ConvertFrom-Json
$headers = @{ Authorization = "Bearer $($c.token)" }
$base = $c.baseUrl

# 最近 10 轮中的用户与助手消息，按时间正序排列
(Invoke-RestMethod "$base/v1/messages?limit=10" -Headers $headers).messages

# 向已绑定聊天发送 UTF-8 文本
$body = @{ text = '请总结本次讨论。' } | ConvertTo-Json
Invoke-RestMethod "$base/v1/messages" -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
```

HTTP 202 会返回 `sessionId`、`messageId`、`queueId` 和 `status`。状态 `queued` 表示 Codex 已持久接收消息。状态 `started` 表示服务观察到该消息启动，并附有 `turnId`。用 `GET /v1/sessions/{id}/queue` 检查队列，用 `GET /v1/sessions/{id}/messages` 读取回复；可按 `turnId` 和 `role` 关联结果。超时或连接中断后先查询队列和历史，再决定是否重发。

客户端可以在 POST 消息时提供 UUID 格式的 `messageId`。同一会话中，以相同 ID、文本和投递方式重试会返回原回执。更改请求内容会返回 409。服务在确认结果前异常退出时，重试会返回 409，保留原请求供查询。幂等记录保存在用户私有目录，仅包含请求摘要和回执。

**正在桌面使用这个聊天时**，使用默认投递方式。通过此服务创建或加载的聊天在空闲时开始处理，忙碌时排队。另一个进程持有的桌面聊天由原客户端消费队列，可能要在桌面打开聊天并从队列发送。HTTP 服务中的 `runtimeStatus` 描述所连 app-server 看到的状态。接续请求遇到尚未确认结束的桌面轮次时，回执带有 `wakeup: external_turn_unfinished`，消息保留在队列。

**要让 HTTP 服务接续一个已结束本轮工作的聊天并生成回复**，可以在一次 POST 中传 `{"text":"继续","delivery":"resume"}`。它以相同会话 ID 载入历史，并处理待发消息。也可对已经排队的消息调用 `POST /v1/sessions/{id}/resume`。交接期间请由一个客户端驱动会话，处理结束后可以在桌面重开聊天查看历史。HTTP 客户端需要交互式工具审批的任务请在桌面处理。

## 接口

以下路径中的 `{id}` 为 Codex 本机会话 UUID。请求体为 UTF-8 JSON 对象。创建会话的初始工作目录为启动服务时的工作目录。

| 方法 | 路径 | 请求或结果 |
| --- | --- | --- |
| GET | `/v1/sessions` | 列出聊天。用 `archived=true` 列归档，支持 `limit` 和 `cursor` |
| POST | `/v1/sessions` | `{"text":"第一条消息"}` 创建并启动，返回 201、会话 ID 与首轮 ID |
| GET | `/v1/sessions/{id}` | 会话标题、预览、时间及运行状态 |
| PATCH | `/v1/sessions/{id}` | `{"title":"新标题"}` |
| POST | `/v1/sessions/{id}/archive`、`/unarchive` | 归档或恢复该会话，两个操作均在会话 ID 后追加 |
| GET、PUT | `/v1/current` | 查询绑定，或以 `{"sessionId":"…"}` 选择默认目标。绑定作用于此监听器的生命周期 |
| POST | `/v1/messages`、`/v1/sessions/{id}/messages` | `{"text":"…"}` 投递到默认或指定聊天；可选 `delivery` 为 `queue`（默认）或 `resume` |
| GET | `/v1/messages`、`/v1/sessions/{id}/messages` | `messages` 为用户和助手文本，`turns` 为对应的轮次状态。附件显示类型，助手消息含可用的 `phase` |
| GET | `/v1/sessions/{id}/queue` | 查看仍在等待的消息，支持分页 |
| POST | `/v1/sessions/{id}/resume` | 在此服务接续会话，处理已排队的消息 |

列表和消息历史的 `limit` 为 1～50（默认 20）。消息历史按轮分页，第一页为最近一批，页内消息按时间正序。用 `nextCursor` 获取上一批历史，直到返回 null。持续读取新回复时重新请求第一页。归档当前会话会清空该监听器的绑定。切换绑定会影响同一端口上的全部客户端，多客户端独立工作时请使用显式 ID。

插件使用 Codex 的 JSON-RPC 协议及本机 CLI 提供的队列方法。[OpenAI Docs · Codex App Server](https://developers.openai.com/codex/app-server/)
