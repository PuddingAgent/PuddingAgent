# Pudding 外部工作空间、Agent 与消息 API 设计与使用说明

> 状态：Source Implemented / Desktop Loopback Smoke Passed  
> 日期：2026-08-31  
> 权威决策：[ADR-082](../07架构/96ADR-082Pudding外部工作空间Agent消息APIADR.md)  
> 认证基线：[ADR-075](../07架构/90ADR-075第三方任务看板AccessToken与外部APIADR.md)

## 1. 能力概览

External API v1 现在除任务看板外，还提供授权工作空间发现、Agent 目录和 Agent 文本消息异步执行。推荐客户端流程：

```text
GET token -> GET workspaces -> GET agents -> POST message
                                      -> 202 + Location -> GET receipt until terminal
```

“发送成功”分两层：`deliveryStatus` 是消息基础设施状态，`executionStatus` 是 Agent Turn 状态。客户端不得在
`deliveryStatus=accepted` 时把业务任务标记为已完成。

## 2. 准备 Access Token

External API 默认关闭。管理员在 `<DataRoot>/config/system.json` 的 `externalTaskApi` 中显式启用，并在 Admin
“Access Token 管理”页面创建 Token。按调用需要选择：

- 只发现目录：`workspaces.read` + `agents.read`；
- 只向已知 Agent 发消息：`messages.send`；
- 完整发现与发送：三个 scope；
- workspace allow-list 只勾选集成实际需要访问的工作空间。

Secret 只在创建响应显示一次。以下示例用环境变量占位，禁止把真实 Token 放进源码、URL、日志或截图。

```powershell
$headers = @{ Authorization = "Bearer $env:PUDDING_EXTERNAL_TOKEN" }
Invoke-RestMethod -Headers $headers -Uri "https://pudding.example/api/external/v1/token"
```

## 3. 目录接口

### 3.1 列出授权工作空间

```http
GET /api/external/v1/workspaces
Authorization: Bearer <token>
```

只返回 Token workspace allow-list 与当前数据库工作空间的交集。工作空间已删除时不会因为 claim 残留而继续出现。

### 3.2 列出 Agent

```http
GET /api/external/v1/workspaces/default/agents?enabledOnly=true
Authorization: Bearer <token>
```

`enabledOnly=true` 同时排除 disabled 与 frozen Agent。单项详情使用
`GET /api/external/v1/workspaces/{workspaceId}/agents/{agentId}`。

## 4. 发送消息

```http
POST /api/external/v1/workspaces/default/agents/default.global_general-assistant/messages
Authorization: Bearer <token>
Idempotency-Key: ticket-20260831-0001
Content-Type: application/json

{"content":"请检查任务队列并给出当前状态。"}
```

限制：正文去除首尾空白后必须非空，最大 65,536 字符；目标 Workspace/Agent 必须存在、启用且未冻结；
`Idempotency-Key` 必填，最长 128 字符且不含控制字符。

典型响应：

```json
{
  "messageId": "extmsg-...",
  "workspaceId": "default",
  "agentId": "default.global_general-assistant",
  "deliveryStatus": "queued",
  "executionStatus": null,
  "reply": null,
  "deliveries": [{ "deliveryId": "...", "status": "queued", "attemptCount": 0 }],
  "acceptedAtUtc": "2026-08-31T10:00:00Z",
  "completedAtUtc": null,
  "statusUrl": "/api/external/v1/workspaces/default/agents/default.global_general-assistant/messages/extmsg-..."
}
```

HTTP 状态是 `202 Accepted`，响应 `Location` 与 `statusUrl` 指向同一个 receipt。

## 5. 查询投递与执行回执

```http
GET /api/external/v1/workspaces/default/agents/default.global_general-assistant/messages/extmsg-...
Authorization: Bearer <same-token>
```

字段解释：

| 字段 | 说明 |
|---|---|
| `deliveryStatus` | `queued` / `delivering` / `retrying` / `accepted` / `failed` / `unknown` |
| `executionStatus` | canonical `ChatExecutionCommand` 状态；尚未创建 Turn 时为 null |
| `conversationId` | 执行实际落入的 canonical Conversation；只用于关联，不是客户端可指定参数 |
| `reply` / `replySummary` / `replyIsError` | 仅在 canonical terminal event 可用时出现 |
| `completedAtUtc` | Agent Turn 的终态时间；仅凭 delivery accepted 不会填充 |

同一 Token 的同路由、同 key、同请求体可安全重试且不会再次投递。改正文仍复用 key 返回
`external.idempotency_conflict`。另一个 Token 即使拥有相同 workspace 和 `messages.send`，也查不到这条 receipt。

## 6. 错误处理

External API 复用稳定错误结构：

```json
{ "code": "agent.unavailable", "message": "Agent 已停用或冻结，不能接收外部消息。", "traceId": "..." }
```

常见状态：

- `400 external.invalid_request`：正文、幂等键或 HTTPS 门禁不合法；
- `401/403`：Token 无效、撤销、到期、scope 不足或 workspace 越权；
- `404 workspace.not_found|agent.not_found|message.not_found`：资源不存在或 receipt 不属于当前 Token；
- `409 workspace.unavailable|agent.unavailable|external.idempotency_*`：资源不可接收或幂等冲突；
- `429`：预留给 per-token RateLimiter，当前生产门禁尚未完成。

## 7. 当前实施与未完成项

源码已实现 controller、稳定 DTO、三个 scope/Policy、Admin scope 选择和聚焦后端测试。外部 Connector 请求显式携带
服务端 `canonical_turn=true`，Dispatcher 将其转交 ADR-059 Conversation Turn；receipt 因而能按 ingress messageId
读取 running/terminal command，而不是从直接 Runtime 的可变消息日志猜回复。External API/Token 聚焦测试 7/7、
Dispatcher canonical 路由聚焦测试 2/2，Desktop 点火构建 0 错误且加载产物哈希一致。

2026-08-31 Desktop Loopback 真实消息 smoke 已通过：workspaces/agents 均 200，发送/幂等重放均 202 且 messageId
一致，receipt 观察到 `running -> succeeded`、真实 conversationId、`EXTERNAL_API_SMOKE2_OK` 和
`replyIsError=false`；临时 Token 已撤销。仍未完成：非 Loopback HTTPS 部署、per-token RateLimiter、OpenAPI 快照、
SSE/Webhook 完成通知和 P4 运维收口。因此此文档不能作为公网生产可用证明。
