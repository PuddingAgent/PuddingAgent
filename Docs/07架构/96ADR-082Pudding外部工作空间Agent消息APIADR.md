# ADR-082：Pudding 外部工作空间、Agent 与消息 API

> 状态：Accepted（源码实现与 Desktop Loopback 真实模型 smoke 已通过；远程部署门禁待验收）  
> 日期：2026-08-31  
> 决策范围：外部工作空间目录、Agent 目录、向 Agent 发送消息、异步执行回执  
> 依赖：[ADR-075 第三方任务看板 Access Token 与外部 API](90ADR-075第三方任务看板AccessToken与外部APIADR.md)  
> 使用说明：[Pudding 外部工作空间、Agent 与消息 API 设计与使用说明](../Features/Pudding外部工作空间Agent消息API设计与使用说明.md)

## 1. 背景

ADR-075 已建立 opaque Access Token、独立认证 scheme、scope/workspace 双重授权、HTTPS 门禁和
`/api/external/v1` 版本边界，但资源范围只覆盖 Task。集成系统仍无法发现已授权工作空间与 Agent，也无法把消息
送入 Pudding 的 canonical Agent 执行链。

本 ADR 扩展同一个 External API v1，不新增第二套凭据、会话状态机或 Agent 执行器。

## 2. 决策

### 2.1 复用 External Access Token 安全边界

新增三个互不隐含的 scope：

- `workspaces.read`：列出 Token allow-list 内仍存在的工作空间并读取安全详情；
- `agents.read`：列出或读取已授权工作空间内的 Agent 安全目录投影；
- `messages.send`：向指定 Agent 发送文本，并读取**当前 Token 自己发送的消息**的投递/执行回执。

不存在 `*` scope。路由中的 `workspaceId` 继续由 `ExternalWorkspaceRequirement` 与 Token workspace claim 做
ordinal 精确校验；`GET /workspaces` 没有路由参数，只返回 claims 与数据库实体的交集。

### 2.2 外部 DTO 是稳定、安全投影

Workspace DTO 不返回成员、访问策略或 UserProfile。Agent DTO 不返回 System Prompt、MainSessionId、工具授权细节
或 Secret；只返回目录识别、启停/冻结、能力 ID 与首选 provider/model 等非凭据字段。

外部合同不得直接序列化 EF Entity 或内部 `WorkspaceAgentDto`。

### 2.3 消息必须进入 canonical Message Fabric

`POST .../agents/{agentId}/messages` 不直接调用 LLM，也不伪造 Admin 用户或内部会话请求。服务端构造：

- sender：`connector/access-token:{tokenId}`；
- target：路径指定的唯一 Agent；
- `audience=direct`、`visibility=private`、`contentType=text`；
- 服务端 metadata：`source=external.api`、`intent=ask`、`requires_response=true`、`canonical_turn=true`。

随后统一经过 `IMessageSystem -> MessageDeliveryDispatcher -> Conversation acceptance -> Agent Turn`。调用方不能指定
内部 session、priority、handling mode、任意 metadata 或广播目标。

### 2.4 采用异步 Receipt，不把 Delivery ACK 当作执行完成

发送返回 `202 Accepted`、`Location` 和 message receipt。`deliveryStatus=accepted` 只表示 Message Fabric 已把消息
交给 canonical Conversation 接受链；只有 `executionStatus` 进入终态且 receipt 出现 `completedAtUtc`/`reply`，才表示
Agent Turn 结束。

状态查询通过 ingress `messageId` 精确关联 `ChatExecutionCommand.MetadataJson`，再由 `TerminalSequence` 读取
canonical `ConversationEvent` 并使用统一终态 formatter 生成 reply。不得从可变前端状态或“最后一条 Agent 消息”猜测回复。

### 2.5 写入口强制幂等与所有权隔离

发送必须带 `Idempotency-Key`（1..128、无控制字符）。幂等身份包含 token、HTTP method、canonical route、key 和请求体；
同 key 同请求重放原 receipt，不再次发送；同 key 不同正文返回 409。

message status 只允许查到 `RoomMessage.FromKind=connector && FromId=access-token:{currentTokenId}` 的消息，因此持有同一
workspace scope 的另一个 Token 也不能读取其回复。

## 3. 外部合同

| Method | Route | Scope | 语义 |
|---|---|---|---|
| GET | `/api/external/v1/token` | 已认证 | whoami、scope/workspace 自检（ADR-075 已有） |
| GET | `/api/external/v1/workspaces` | `workspaces.read` | 列出 Token allow-list 内存在的工作空间 |
| GET | `/api/external/v1/workspaces/{workspaceId}` | `workspaces.read` | 工作空间安全详情 |
| GET | `/api/external/v1/workspaces/{workspaceId}/agents?enabledOnly=false` | `agents.read` | Agent 安全目录 |
| GET | `/api/external/v1/workspaces/{workspaceId}/agents/{agentId}` | `agents.read` | Agent 安全详情 |
| POST | `/api/external/v1/workspaces/{workspaceId}/agents/{agentId}/messages` | `messages.send` | 幂等异步发送文本 |
| GET | `/api/external/v1/workspaces/{workspaceId}/agents/{agentId}/messages/{messageId}` | `messages.send` | 当前 Token 自有消息的投递/执行回执 |

所有端点继续受 `ExternalApiGateFilter` 控制：默认关闭；非 Loopback 明文 HTTP 拒绝。

## 4. 明确不做

- 不开放任意聊天历史、内部 Session 创建/切换或 MainSessionId；
- 不开放 Agent Prompt、完整 Tool Grant、Provider Secret 或 Workspace 成员/权限；
- 不提供同步长连接等待 LLM 完成、广播发送、客户端优先级和任意 metadata；
- 不提供消息删除、重写、冒充用户或冒充另一个 Agent；
- 本阶段不宣称 RateLimiter、SSE/Webhook 回调、OpenAPI 快照和公网部署已完成。

## 5. 验收门禁

源码验收必须覆盖：allow-list、安全 DTO、Agent enabled filter、Token actor、Message Fabric 路由、幂等重放、
Delivery/Execution 分离、canonical terminal reply、禁用 Agent 和缺少幂等键。产品验收还必须在加载新 Core 的进程中：

1. 显式启用 External API 并创建最小 scope Token；
2. 经 HTTPS 或 Loopback 调用目录与发送接口；
3. 轮询 Location，观察 queued/accepted 与 execution terminal 的真实转换；
4. 核对 Agent UI/canonical Conversation 中只有一次用户输入和一次执行；
5. 验证另一个 Token、越权 workspace、撤销 Token 和明文远程 HTTP 均 fail closed。

2026-08-31 Desktop Loopback 验收已确认：目录接口 200，发送与幂等重放 202 且复用同一 messageId；Delivery 从 queued
推进到 accepted，canonical command 从 running 进入 succeeded，receipt 返回真实 conversationId、terminal reply
`EXTERNAL_API_SMOKE2_OK` 与 `replyIsError=false`。测试 Token 已撤销。此结论不覆盖非 Loopback HTTPS、RateLimiter、
OpenAPI、跨机访问和长期负载。
