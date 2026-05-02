# task37 — 连接器基础设施与 Webhook 连接器实现

最后更新：2026-05-02

## 背景

根据 [task36 调研](task36-event-trigger-and-subagent.md) 的 V1 实施计划，P0 第一步是实现连接器（Connector）基础设施层——以新接口 `IPuddingConnector` 替代旧的单向 `IPuddingGatewayAdapter`，并实现首个连接器 `WebhookConnector` 作为最小可行链路的入站端点。

## 前置依赖

- [task36 调研完成](task36-event-trigger-and-subagent.md) — 连接器接口设计与整体架构已确定
- 依赖任务：task-20260502-001（单进程宿主骨架）、task-20260502-005（P2P）、task-20260502-008（Runtime）

## 目标

1. 定义 `IPuddingConnector` 接口及相关模型（`PuddingCore`）
2. 实现 `ConnectorHost` 管理连接器生命周期（`PuddingAgent`）
3. 实现 `WebhookConnector` 作为第一个新接口连接器（`PuddingAgent`）
4. 旧 `GatewayAdapterHost` 与 `IPuddingGatewayAdapter` 保留兼容，新旧并存

## 范围

### 做（In Scope）

#### 1. IPuddingConnector 接口与模型（PuddingCore/Platform/）

```csharp
// 新增文件：IPuddingConnector.cs
public interface IPuddingConnector
{
    ConnectorDescriptor Descriptor { get; }
    Task StartAsync(ConnectorContext context, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SendAsync(ConnectorMessage message, CancellationToken ct = default);
    Task<ConnectorOperationResult> OperateAsync(string operation,
        Dictionary<string, string>? parameters = null, CancellationToken ct = default);
    Task<ConnectorDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default);
}
```

**新增模型**：
- `ConnectorDescriptor`：连接器描述（ConnectorId、ConnectorType、Protocol、Capabilities）
- `ConnectorContext`：连接器上下文（OnEventReceived 回调、Log）
- `ConnectorMessage`：出站消息（Target、Content、Metadata）
- `ConnectorOperationResult`：操作结果（Success、Data、Error）
- `ConnectorDiagnostics`：诊断信息（Status、MessagesReceived/Sent、Errors）
- `ConnectorCapability` 枚举：Receive、Send、Manage、Stream

#### 2. ConnectorHost 连接器宿主（PuddingAgent/Gateway/ 或 PuddingAgent/Connectors/）

- 管理 `IPuddingConnector` 实例的生命周期（注册、启动、停止）
- 同时兼容旧的 `IPuddingGatewayAdapter`（通过适配器包装或双队列）
- 提供 `ListConnectors()`、`GetDiagnostics()` 查询接口
- 启动时读取 SQLite `ConnectorConfig` 表，按配置启用/禁用连接器

#### 3. WebhookConnector 实现（PuddingAgent/Connectors/）

- 监听端点：`POST /webhook/{channel_id}`
- 入站：HTTP Body → `PuddingIngressEnvelope.MessageText`，Headers → `Metadata`
- 认证：通过 `X-Hub-Signature-256` 头做 HMAC-SHA256 签名验证（可选开启）
- `SendAsync`：向外部 URL 发起 HTTP POST 回调
- `OperateAsync`：`rotate_secret`（轮换签名密钥）
- `GetDiagnosticsAsync`：返回接收计数、最后接收时间、错误计数
- 配置：Webhook 端口复用 Kestrel（无需额外端口）

#### 4. 数据库迁移

- 新增 `ConnectorConfig` 表：`ConnectorId`、`ConnectorType`、`ConfigJson`、`Enabled`、`CreatedAt`、`UpdatedAt`
- 为 `WebhookConnector` 写入默认配置行

#### 5. DI 注册与启动

```csharp
// Program.cs
services.AddSingleton<ConnectorHost>();
services.AddSingleton<IPuddingConnector, WebhookConnector>();
// 旧接口兼容
services.AddSingleton<IPuddingGatewayAdapter, WebChatGatewayAdapter>();
```

启动时 `ConnectorHost.StartAllAsync()` 遍历所有已注册 `IPuddingConnector`，按配置决定启动。

### 不做（Out of Scope）

- MQTT/Email 连接器实现（后续 task）
- EventRouter 事件路由（后续 task）
- BranchSessionManager 分支会话（后续 task）
- 子代理模型实现（后续 task）
- 旧 Adapter → Connector 迁移（WebChat/Cli/Email 暂不变）
- 连接器 UI 管理界面
- 性能压测

## 验收标准

1. `IPuddingConnector` 接口及 6 个模型在 `PuddingCore` 中编译通过
2. `ConnectorHost` 能注册并管理多个连接器生命周期
3. `WebhookConnector` 启动后可通过 `POST /webhook/test` 接收消息
4. Webhook 入站消息正确转换为 `PuddingIngressEnvelope`，触发 `OnEventReceived`
5. `ConnectorHost` 同时管理新旧接口实例（`IPuddingConnector` + `IPuddingGatewayAdapter`）
6. `dotnet build PuddingAgentNetwork.slnx` 零错误
7. 连接器配置持久化到 SQLite `ConnectorConfig` 表，重启后配置保留

## 技术要点

- **接口位置**：`PuddingCore/Platform/IPuddingConnector.cs`（与现有 `IGateway.cs` 同级）
- **宿主位置**：`PuddingAgent/Connectors/ConnectorHost.cs`
- **Webhook 连接器**：`PuddingAgent/Connectors/WebhookConnector.cs`
- **向后兼容**：`ConnectorHost` 内部分别维护 `_connectors` 和 `_legacyAdapters` 两个字典
- **Webhook 端点**：MapPost 在 Controller 模块的路由注册中完成，非连接器内部架设 HTTP Server

## 风险

- **接口设计变更**：`IPuddingConnector` 在实现过程中可能需要调整（如 `OperateAsync` 的签名过于泛化）。缓解：先以最小接口落地，OperateAsync 可暂为空实现。
- **新旧并存复杂度**：`ConnectorHost` 需同时管理两种接口。缓解：`IPuddingGatewayAdapter` 包装为 `LegacyAdapterWrapper : IPuddingConnector`（SendAsync/OperateAsync 抛 NotSupported）。
- **Webhook 签名验证**：HMAC 算法选择需确认。缓解：默认关闭，通过配置开启。

## 关联文档

- [task36 调研](task36-event-trigger-and-subagent.md) — §0 连接器概念、§2 连接器接口设计、§6 V1 实施建议
- [架构总览](../架构.md)
- [07架构/04PuddingController与Gateway](../07架构/04PuddingController与Gateway.md)
