# ADR-017：WebSocket连接器与网关鉴权层

**状态**: draft → ready
**日期**: 2026-05-16
**决策者**: Lead Agent (Claude 4.7)

## 背景

当前系统缺连接器主机（ConnectorHost），旧 GatewayAdapterHost 仅兼容 IPuddingGatewayAdapter。需要新增 WebSocket 连接器和网关鉴权层。

核心链路：
```
连接器(WS/MQTT/HTTP) → ConnectorHost → 网关鉴权 → 事件系统 → LLM会话层 → LLM执行引擎
                                                                      ↓
                                                                   SSE推送 → 前端多卡片UI
```

## 决策

### 1. ConnectorHost：新连接器生命周期管理

**新建** `Source/PuddingAgent/Services/ConnectorHost.cs`：
- 注册/启动/停止 IPuddingConnector
- 提供回调：OnEventReceived → EventIngressBridge → 事件管道
- 提供日志注入
- 管理连接器诊断查询 API

与 `GatewayAdapterHost` 分离（后者保留兼容旧 `IPuddingGatewayAdapter`）。

### 2. WebSocket连接器

**新建** `Source/PuddingAgent/Connectors/WebSocketConnector.cs`：
- 实现 `IPuddingConnector`
- 使用 `System.Net.WebSockets`（ASP.NET Core 内置）
- 连接器能力：Receive + Send + Stream
- 生命周期：StartAsync 启动 WS 监听 → OnEventReceived 推送 PuddingIngressEnvelope
- 入站消息格式：`{"type":"chat","content":"...","sessionId":"..."}`
- 出站：`ConnectorMessage.Target = connectionId`，广播给特定 WS 连接

### 3. 网关鉴权层

**新建** `Source/PuddingGateway/GatewayAuthService.cs`：

| 渠道 | 鉴权方式 | 实现 |
|------|---------|------|
| WebSocket / HTTP API | SM2 签名 | 请求头 `X-SM2-Signature` + `X-SM2-Timestamp`，用 hyfree.GM `GMService.SM2VerifySign` 验证 |
| 邮箱 | 白名单 | 配置 `Gateway:EmailWhitelist` |
| 飞书 | 白名单 | 配置 `Gateway:FeishuWhitelist` |

**SM2签名流程**：
1. 客户端：`signature = SM2Sign(timestamp + body, privateKey)`
2. 请求头：`X-SM2-Signature: <hex>` `X-SM2-Timestamp: <unix_ms>`
3. 网关：`SM2VerifySign(timestamp+body, signature, publicKey)` → 允许/拒绝
4. 时间窗口：5分钟内有效（防重放）

**新建** `Source/PuddingGateway/Models/ConnectionIdentity.cs`：
- ConnectorId, SourceType, AuthenticatedUser, Roles

### 4. 前端多卡片改造

修改 `Source/PuddingPlatformAdmin/src/pages/chat/`：

**types.ts** 新增：
```typescript
interface ChatSource {
  sourceId: string;      // "agent-xxx" | "ws-conn-xxx" | "webhook-xxx"
  sourceType: 'agent' | 'websocket' | 'webhook' | 'email' | 'mqtt';
  displayName: string;   // "AI 助手" | "WebSocket 用户" | "Webhook"
  avatarEmoji: string;   // "🤖" | "🔌" | "🪝" | "📧"
  avatarColor: string;   // 头像背景色
}
```

**useChatState.ts** 改动：
- `done` 事件后，下次 `metadata` 或 `delta` 自动创建新 ChatTurn
- 新 ChatTurn 绑定 `ChatSource`（从 metadata.sourceId 推断）
- `mapEventToTurn` 增加 sourceId 匹配

**MessageList.tsx** 渲染：
- 每个 ChatTurn 左侧显示来源头像 + 名称
- 按时间排序，不同来源的卡片交错显示

### 5. 测试客户端

**新建** `TestScripts/PuddingWsTest/Program.cs`：
- C# 控制台程序
- 使用 `System.Net.WebSockets.ClientWebSocket`
- 支持 SM2 签名（引用 hyfree.GM）
- 命令：`connect <url>` `send <message>` `disconnect`
- 接收并打印 SSE 风格的推送消息

## 影响范围

| 模块 | 文件 | 操作 |
|------|------|------|
| PuddingCore/Platform | IPuddingConnector.cs | 已有，不修改 |
| PuddingAgent/Connectors | WebSocketConnector.cs | **新建** |
| PuddingAgent/Services | ConnectorHost.cs | **新建** |
| PuddingGateway | GatewayAuthService.cs | **新建** |
| PuddingGateway/Models | ConnectionIdentity.cs | **新建** |
| PuddingAgent/Program.cs | DI注册 | 修改 |
| PuddingPlatformAdmin | types.ts, useChatState.ts, MessageList | 修改 |
| TestScripts | PuddingWsTest/Program.cs | **新建** |

## 风险

- SM2 签名在 WebSocket 握手阶段无法注入 HTTP 头（WS 升级后无 HTTP 头）→ 方案：首条消息携带认证帧 `{"type":"auth","signature":"...","timestamp":...}`
- 多客户端并发连接 → Channel 容量需调整

## 验收标准

1. WebSocket 客户端连接 → 发消息 → Agent 回复 → 客户端收到 SSE 推送
2. 无SM2签名 → 网关拒绝（401）
3. 白名单外邮箱 → 网关拒绝
4. 前端多个来源卡片可区分显示
5. Agent done 后新消息自动创建新卡片
