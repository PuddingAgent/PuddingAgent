# task36 — 事件触发、外部协议接入与子代理模型（调研）

最后更新：2026-05-02

---

## 第一部分：任务卡

> **ID**: task-20260502-010 ｜ **项目**: Pudding ｜ **优先级**: P1 ｜ **阶段**: in_progress

### 背景

旧 task34（统一事件总线与订阅治理）基于多服务分布式架构设计，依赖 RabbitMQ 作为中心化消息中间件。架构简化后，Pudding 转为单进程 P2P 模型（见 [架构.md](../架构.md)），旧 task34 已废弃。

本任务卡为**调研卡**，目标是在动手实现前，厘清事件触发功能在新架构下的整体设计。

### 调研范围

#### 1. 事件触发三层模型

| 层次 | 职责 | 关键问题 |
|------|------|---------|
| **触发器/订阅器（Trigger）** | 接收外部事件源，转换成内部事件 | 插件式架构？每种协议一个 Adapter？ |
| **网关（Gateway）** | 事件接入、协议转换、来源识别、信任分级 | 进程内模块还是独立进程？ |
| **Agent** | 消费事件、路由到其他 Agent、执行处理 | 主会话和事件处理如何切换？ |

#### 2. 外部协议接入场景

| 场景 | 协议 | 典型用途 | 复杂度 |
|------|------|---------|--------|
| Webhook | HTTP POST | CI/CD 通知、第三方回调 | 低 |
| HTTP 请求 | REST | 外部系统主动调用 | 低 |
| 其他 Agent 事件 | P2P HTTP/gRPC | Agent 间协作、任务分发 | 中 |
| MQTT | MQTT | 智能设备、IoT、嵌入式 | 中 |
| 智能家居 | Home Assistant | 家庭自动化触发 | 中 |
| 邮箱订阅 | IMAP/POP3 | 邮件到达触发 | 中 |
| 插件扩展 | 自定义协议 | 未来任意协议扩展 | 高 |

#### 3. 事件处理模型：主会话 vs 分支会话

- **模式 A（内联）**：事件插入当前对话流 → 简单但阻塞主会话
- **模式 B（分支）**：fork 派生会话 → 不阻塞 → 完成后 merge

#### 4. 子代理（Sub-Agent）模型

主 Agent 下的轻量级处理单元，避免事件处理阻塞主会话。

#### 5. P2P 事件路由

Agent A 收到事件后，如何判断自己处理还是路由给 Agent B。

### 验收标准

1. 产出调研文档包含架构图
2. 协议适配器插件接口设计草案
3. 分支会话模型设计（创建/合并/清理）
4. 子代理模型设计（生命周期/资源隔离/通信）
5. P2P 事件路由策略
6. 推荐 V1 实现范围及风险清单

### 不做

- 具体代码实现
- 性能压测与优化
- 安全审计（后续 task 覆盖）

---

## 第二部分：调研产出

> 以下为调研结论，基于代码库探索（阶段 1）与架构评估（阶段 2）。

---

## 0. 核心概念：连接器（Connector）

### 0.1 为什么需要"连接器"而非"适配器"

之前使用的术语 **Adapter（适配器）** 暗示单向协议转换（外部协议 → 内部信封）。但实际场景中，大量外部通道是**双向**的：

| 通道 | 入站（接收） | 出站（发送） | 管理操作 |
|------|------------|------------|---------|
| **邮箱** | 收到新邮件 → 触发事件 | 发送邮件回复 | 标记已读、移动文件夹、删除 |
| **MQTT** | 订阅 Topic 收到消息 | 发布消息到 Topic | 列出 Topic、管理 QoS |
| **Webhook** | 接收 HTTP POST | 回调外部 URL | 管理签名密钥 |
| **智能家居** | 设备状态变更 | 发送控制指令 | 查询设备列表 |

**连接器（Connector）** 是对外部协议通道的完整抽象——不仅仅是协议转换，而是**对该协议的完整操作能力**。类比 MCP（Model Context Protocol）为 LLM 提供工具，**连接器为 Agent 提供外部通道的操作接口**。

### 0.2 连接器 vs MCP 的关系

| 维度 | MCP Server | Connector |
|------|-----------|-----------|
| **用途** | 为 LLM 提供工具（Tool） | 为 Agent 提供外部通道（Channel） |
| **方向** | 请求-响应（Agent 调 Tool） | 双向（Agent ↔ 外部系统） |
| **生命周期** | 无状态 Tool 调用 | 长连接，持续监听 + 主动操作 |
| **示例** | `get_weather`、`search_files` | 邮箱连接器（收/发/管）、MQTT 连接器（订/发/查） |
| **抽象层级** | LLM 工具层 | 基础设施通道层 |

**两者互补而非替代**：连接器负责打通外部通道，MCP Tool 可由连接器暴露给 LLM 使用。例如"邮箱连接器"提供 IMAP 通道能力，同时可暴露 `send_email`、`search_emails` 等 MCP Tool。

### 0.3 连接器接口设计

```csharp
/// <summary>连接器主接口 — 管理一个外部协议通道的完整生命周期。</summary>
public interface IPuddingConnector
{
    /// <summary>连接器描述符。</summary>
    ConnectorDescriptor Descriptor { get; }

    /// <summary>启动连接器，建立通道连接并开始监听。</summary>
    Task StartAsync(ConnectorContext context, CancellationToken ct = default);

    /// <summary>停止连接器，关闭通道连接。</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>向外部通道发送消息。</summary>
    Task SendAsync(ConnectorMessage message, CancellationToken ct = default);

    /// <summary>对外部通道执行操作（如移动邮件、查询 Topic 列表等）。</summary>
    Task<ConnectorOperationResult> OperateAsync(
        string operation, Dictionary<string, string>? parameters = null,
        CancellationToken ct = default);

    /// <summary>获取连接器当前状态与诊断信息。</summary>
    Task<ConnectorDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default);
}

/// <summary>连接器描述符。</summary>
public sealed record ConnectorDescriptor
{
    public required string ConnectorId { get; init; }             // "email-imap-001"
    public required string ConnectorType { get; init; }           // "email", "mqtt", "webhook", "home-assistant"
    public required string Protocol { get; init; }                // "IMAP", "MQTT", "HTTP"
    public string? Version { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = []; // "receive", "send", "manage"
}
```

### 0.4 连接器上下文

```csharp
public sealed class ConnectorContext
{
    /// <summary>入站事件回调 — 连接器收到外部消息时调用。</summary>
    public required Func<PuddingIngressEnvelope, CancellationToken, Task> OnEventReceived { get; init; }

    /// <summary>连接器日志。</summary>
    public required Action<string> Log { get; init; }
    public CancellationToken CancellationToken { get; init; }
}
```

### 0.5 与现有 IPuddingGatewayAdapter 的关系

`IPuddingGatewayAdapter` 是过渡期接口，V1 中**逐步替换为 `IPuddingConnector`**：

| 连接器 | 继承关系 |
|--------|---------|
| `WebChatConnector` | 新写，替代 `WebChatGatewayAdapter` |
| `CliConnector` | 新写，替代 `CliGatewayAdapter` |
| `EmailConnector` | 新写，替代 `EmailGatewayAdapter` — **新增 OperateAsync（管理邮箱）** |
| `WebhookConnector` | **V1 新增** |
| `MqttConnector` | **V1 新增** — **支持 Subscribe + Publish + OperateAsync** |

**向后兼容**：`IPuddingGatewayAdapter` 暂时保留不删除，新旧接口在 DI 中并存，GatewayAdapterHost 同时支持两种接口，逐步迁移。

### 0.6 连接器能力声明

```csharp
// 每个连接器声明自己的能力，供 Agent 能力声明和路由使用
public enum ConnectorCapability
{
    Receive,    // 可接收外部事件
    Send,       // 可向外发送消息
    Manage,     // 可管理外部通道（如邮箱文件夹操作）
    Stream      // 支持流式数据（如 MQTT 持续订阅）
}
```

### 0.7 连接器与事件触发的关系

连接器是事件触发的**实现载体**，不是替代事件触发模型：

```
事件触发三层模型                       连接器实现
─────────────────                     ─────────
Trigger（触发器/订阅器）    ←──实现──   连接器的监听能力（OnEventReceived 回调）
Gateway（网关）             ←──实现──   连接器的 SendAsync（出站）
                                        连接器的 OperateAsync（通道管理）
Agent                      ←──不变──   EventRouter + BranchSessionManager
```

---

## 1. 事件触发整体架构

### 1.1 架构决策（ADR-001）

**选择内嵌连接器模型**：连接器作为 Agent 进程内模块，通过 DI 注册，经 `ICoordinationEventBus` 投递入站事件。连接器同时支持出站发送（SendAsync）和通道管理（OperateAsync），是双向通道而非单向协议转换。

这与架构文档中"不再需要 Gateway Adapter Plugin 架构"一致——废弃的是旧的多进程动态加载插件体系。新的连接器体系以编译时 DI 注册取代运行时插件扫描。

### 1.2 数据流架构图

```
外部事件源（Webhook POST / MQTT 消息 / 其他 Agent）
        │
        ▼
┌─ IPuddingConnector（连接器）──────────────────┐
│  StartAsync → 监听外部协议                     │
│  OnEventReceived(PuddingIngressEnvelope) ──┐  │
│  SendAsync / OperateAsync ←── 出站操作     │  │
└────────────────────────────────────────────│──┘
                                             │
        ┌────────────────────────────────────┘
        ▼
┌─ EventRouter（Controller 模块，新增）──────────┐
│  1. 解析 Envelope → 判定事件类型               │
│  2. 查路由表：自己处理 or P2P 转发？            │
│  3a. 自己 → SessionManager.ForkBranch()       │
│  3b. P2P  → SwarmMessageHub.BroadcastAsync()  │
└──────────────────────────────────────────────┘
        │
        ▼ (自己处理)
┌─ BranchSessionManager（新增）─────────────────┐
│  1. Fork 分支会话（从主会话或空闲创建）          │
│  2. 装配子代理上下文（记忆/工具/权限）           │
│  3. 注入事件到 LLM 对话循环                    │
│  4. 完成后 Merge 回主会话                      │
└──────────────────────────────────────────────┘
```

### 1.3 单进程内 Agent 内部结构（含新增模块）

```
┌─────────────────── Pudding Agent（单进程）────────────────┐
│                                                            │
│  浏览器 → localhost:8080                                   │
│  ┌──────────────────────────────────────────────────┐     │
│  │  内嵌 Web UI（管理界面 + 对话界面 + 分支树视图）    │     │
│  ├──────────────────────────────────────────────────┤     │
│  │  Controller 模块                                   │     │
│  │  ├─ SessionRouter（现有）                          │     │
│  │  ├─ EventRouter（新增）★                          │     │
│  │  └─ 鉴权/审计                                      │     │
│  ├──────────────────────────────────────────────────┤     │
│  │  Runtime 模块                                      │     │
│  │  ├─ AgentSessionManager（扩展）                    │     │
│  │  ├─ BranchSessionManager（新增）★                 │     │
│  │  ├─ AgentExecutionService（现有）                  │     │
│  │  └─ LLM 对话循环                                   │     │
│  ├──────────────────────────────────────────────────┤     ││  │  SQLite（配置/会话/分支/记忆/审计/死信）            │     │
│  ├──────────────────────────────────────────────────┤     ││  │  连接器（全部进程内 DI 注册）                     │     │
│  │  ├─ WebChatConnector（WebChatGatewayAdapter 迁移） │     │
│  │  ├─ CliConnector（CliGatewayAdapter 迁移）        │     │
│  │  ├─ EmailConnector（EmailGatewayAdapter 升级）★   │     │
│  │  ├─ WebhookConnector（新增）★                    │     │
│  │  └─ MqttConnector（新增）★                       │     │
│  ├──────────────────────────────────────────────────┤     │
│  │  P2P 网络层（节点发现/直连通信/事件广播）           │     │
│  ├──────────────────────────────────────────────────┤     │
│  │  SQLite（配置/会话/分支/记忆/审计/死信）            │     │
│  └──────────────────────────────────────────────────┘     │
│                                                            │
│  ← P2P →  其他 Agent 节点                                  │
└────────────────────────────────────────────────────────────┘
```

---

## 2. 连接器接口设计

### 2.1 架构决策（ADR-002）

**从 Adapter 升级为 Connector**，新增 `IPuddingConnector` 接口作为协议通道的标准抽象。Connector 不仅是协议转换器，而是**双向通道管理器**——可接收事件、发送消息、管理外部系统。

在单进程模型下，连接器就是 DI 容器中的一个注册项，不需要 MEF/Assembly scanning。

### 2.2 新接口 `IPuddingConnector`（详见 §0.3）

```csharp
public interface IPuddingConnector
{
    ConnectorDescriptor Descriptor { get; }
    Task StartAsync(ConnectorContext context, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SendAsync(ConnectorMessage message, CancellationToken ct = default);
    Task<ConnectorOperationResult> OperateAsync(
        string operation, Dictionary<string, string>? parameters = null,
        CancellationToken ct = default);
    Task<ConnectorDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default);
}
```

核心新增能力（对比旧 `IPuddingGatewayAdapter`）：

| 方法 | 用途 | 示例 |
|------|------|------|
| `SendAsync` | 向外部通道发送消息 | 发送邮件回复、MQTT Publish |
| `OperateAsync` | 操作外部通道 | 标记邮件已读、查询 MQTT Topic 列表 |
| `GetDiagnosticsAsync` | 获取连接器诊断信息 | 连接状态、消息统计 |

### 2.3 迁移策略

`IPuddingGatewayAdapter` 保留，新旧接口在 DI 中并存。GatewayAdapterHost 升级为 `ConnectorHost`，同时支持两种接口：

```csharp
// Program.cs 中注册连接器
// 旧接口兼容（逐步迁移）
services.AddSingleton<IPuddingGatewayAdapter, WebChatGatewayAdapter>();
// 新接口
services.AddSingleton<IPuddingConnector, EmailConnector>();
services.AddSingleton<IPuddingConnector, WebhookConnector>();
services.AddSingleton<IPuddingConnector, MqttConnector>();
```

### 2.4 连接器配置持久化

每个连接器的配置存入 SQLite 的 `ConnectorConfig` 表（JSON column），结构：

```
ConnectorId | ConnectorType | ConfigJson                        | Enabled | UpdatedAt
------------|---------------|-----------------------------------|---------|-----------
email-001   | email         | {"imap_host":"...","smtp_host":"..."}| true    | ...
mqtt-001    | mqtt          | {"broker":"mosquitto:1883","...") }| true    | ...
```

### 2.5 邮箱连接器设计要点（Connector 典型示例）

邮箱连接器是 Connector 概念的最佳示范——不仅接收事件，还支持发送和操作：

```csharp
public sealed class EmailConnector : IPuddingConnector
{
    // StartAsync: 连接 IMAP 服务器，使用 IDLE 模式监听新邮件
    // OnEventReceived: 新邮件到达 → PuddingIngressEnvelope（MessageText=邮件正文, Metadata=发件人/主题）

    // SendAsync: 通过 SMTP 发送邮件回复
    //   → ConnectorMessage { To, Subject, Body, InReplyTo }

    // OperateAsync: 管理邮箱操作
    //   → "mark_read"    : { "message_id": "..." }
    //   → "move_folder"  : { "message_id": "...", "target_folder": "Archive" }
    //   → "list_folders" : {}
    //   → "delete"       : { "message_id": "..." }
}
```

### 2.6 新增 Webhook 连接器设计要点

- **监听端点**：`POST http://localhost:8080/webhook/{channel_id}`
- **认证**：支持签名验证（HMAC-SHA256），密钥存储在连接器配置中
- **入站转换**：HTTP Body → `MessageText`，Header → `Metadata`
- **来源识别**：通过 `ChannelId` 区分不同 webhook 来源
- **SendAsync**：向外部 URL 发起 HTTP POST 回调

### 2.7 新增 MQTT 连接器设计要点

- **依赖**：MQTTnet 库（MIT 许可）
- **连接**：连接到外部 MQTT Broker（如 Mosquitto）
- **Topic 映射**：`pudding/agent/{agentId}/event/{type}` ↔ PuddingIngressEnvelope
- **SendAsync**：Publish 消息到指定 Topic
- **OperateAsync**：`list_topics`、`get_topic_info`、`manage_subscription`
- **QoS**：At-Least-Once（QoS 1），防抖动窗口 500ms

---

## 3. 分支会话模型设计

### 3.1 完整生命周期

```
                    ┌─→ Completed ──→ Merged ──→ Archived
                    │
Created ──→ Running ┤─→ Failed ──→ Retryable? ──→ Retry (N=3) or Dead
                    │
                    └─→ Timeout ──→ ForceMerge (摘要注入) or Discard
```

### 3.2 数据模型

```csharp
public sealed record BranchSessionRecord
{
    public required string BranchSessionId { get; init; }        // "session-abc:branch-webhook-x1"
    public required string ParentSessionId { get; init; }        // 主会话 ID
    public required string TriggerEventId { get; init; }         // 触发事件的 EnvelopeId
    public required BranchSessionStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? TimeoutAt { get; init; }              // 超时阈值，默认 +5min
    public string? ResultSummary { get; init; }                  // Merge 用的摘要
    public int RetryCount { get; init; }
    public required string BranchAgentInstanceId { get; init; }  // 子代理 ID
}

public enum BranchSessionStatus
{
    Created, Running, Completed, Failed, Timeout, Merged, Discarded
}
```

### 3.3 Merge 策略（3 级）

| 级别 | 策略 | 适用场景 | V1 实现 |
|------|------|---------|---------|
| **L1: 摘要注入** | 分支完成后生成一句话摘要，作为系统消息注入主会话 | 低优先级通知（CI 构建完成） | ✅ P0 |
| **L2: 状态变更** | 分支修改共享状态，主会话通过事件感知 | 设备状态更新、配置变更 | P2 |
| **L3: 对话历史拼接** | 分支完整对话历史追加到主会话上下文 | 需理解事件全貌（消耗 Token） | P2 |

默认 L1，可通过 `Metadata["merge_strategy"]` 覆盖。

### 3.4 资源隔离

| 资源 | 隔离策略 |
|------|---------|
| **记忆** | 独立 scope（`branch:{BranchSessionId}`），写回时标记 `source=branch` |
| **工具访问** | 默认继承主会话工具集，可通过 `Metadata["tool_allowlist"]`/`["tool_denylist"]` 限制 |
| **LLM 上下文** | 独立上下文窗口，fork 时复制主会话最近 N 条关键消息（默认 N=5，`Metadata["context_fork_depth"]` 可配） |

### 3.5 超时与清理

- 默认超时：5 分钟（`Metadata["branch_timeout_seconds"]` 可配）
- 超时处理：ForceMerge 摘要 → 标记 Timeout → 不再重试
- 清理：已 Merged/Discarded 的分支 24h 后从内存移除，SQLite 保留 7 天

### 3.6 AgentSessionManager 扩展

```csharp
// 现有方法不变，新增：
public BranchSessionRecord ForkBranch(string parentSessionId, PuddingIngressEnvelope trigger);
public Task<MergeResult> MergeBranchAsync(string branchSessionId, MergeStrategy strategy);
public IReadOnlyList<BranchSessionRecord> ListBranches(string parentSessionId);
public void DiscardBranch(string branchSessionId);
```

内部从 `ConcurrentDictionary<SessionId, AgentInstanceRecord>` 扩展为支持树形结构。

---

## 4. 子代理模型设计

### 4.1 核心定义

**子代理是主 Agent 内的轻量级逻辑分区，而非独立进程。** 共享主 Agent 的 SQLite、LLM 连接池、P2P 网络身份，但拥有独立的上下文窗口、记忆 scope、工具权限。

每个分支会话对应恰好一个子代理（1:1 映射），子代理的 `AgentInstanceId` 等于 `BranchSessionRecord.BranchAgentInstanceId`。

### 4.2 进程内结构

```
主 Agent 进程
├── AgentInstanceRecord (SessionId="main", AgentInstanceId="main-001")
│   ├── LLM 上下文窗口 A（主会话）
│   ├── 记忆 scope: "main"
│   └── 工具集: 全部
│
├── AgentInstanceRecord (SessionId="main:branch-webhook-1", AgentInstanceId="sub-001")
│   ├── LLM 上下文窗口 B（独立）
│   ├── 记忆 scope: "branch:main:branch-webhook-1"
│   ├── 工具集: 受限
│   └── 父实例: "main-001"
│
└── AgentInstanceRecord (SessionId="main:branch-mqtt-1", AgentInstanceId="sub-002")
    ├── LLM 上下文窗口 C（独立）
    ├── 记忆 scope: "branch:main:branch-mqtt-1"
    └── 工具集: 受限
```

### 4.3 关键设计决策

| 维度 | 决策 | 理由 |
|------|------|------|
| **资源隔离** | 共享 SQLite，独立 LLM 上下文窗口 | SQLite 写锁进程级，额外进程不减少竞争 |
| **通信机制** | `ICoordinationEventBus`（进程内 pub/sub） | 已有线程安全实现 |
| **生命周期** | **按需创建，用完即毁** | V1 事件频率低，常驻浪费资源 |
| **是否拥有独立工具集** | 是，默认继承后可限制 | Webhook 事件不应访问文件系统 |
| **是否参与 P2P 网络** | **否** | 避免 M×N 节点发现爆炸 |

### 4.4 与 AgentSessionManager 衔接

子代理的 `SessionId` 采用层次命名：`"{parentSessionId}:branch-{triggerType}-{shortId}"`。`AgentSessionManager` 新增 `ParentSessionId` 字段到 `AgentInstanceRecord`，新增 `ListChildren()` 方法。

### 4.5 CoordinationEventKind 扩展

```csharp
// 现有（不变）: LockAcquired, LockDenied, LockReleased, LockForceReleased, LockExpired, UnlockRequested
// 新增子代理事件类型:
SubAgentStarted,      // 子代理创建并开始执行
SubAgentCompleted,    // 子代理处理完成，等待 Merge
SubAgentFailed,       // 子代理处理失败
SubAgentTimeout,      // 子代理超时
BranchMerged,         // 分支成功合并到主会话
BranchDiscarded       // 分支被丢弃
```

---

## 5. P2P 事件路由策略

### 5.1 路由决策流

```
EventRouter 收到 PuddingIngressEnvelope
        │
        ▼
┌─ 1. 自身能力匹配？─────────────────────┐
│  Metadata["required_capability"]       │
│  vs 本地 Agent 能力声明               │
│  ✓ 有 → 自己处理（ForkBranch）        │
│  ✗ 无 → 进入 P2P 路由                 │
└────────────────────────────────────────┘
        │
        ▼ (需要路由)
┌─ 2. P2P 路由表查询 ────────────────────┐
│  - 已发现的节点能力清单（缓存）         │
│  - 匹配目标节点列表                    │
└────────────────────────────────────────┘
        │
        ▼
┌─ 3. 路由策略选择 ──────────────────────┐
│  a. 单播：Metadata["target_agent_id"]  │
│     明确指定 → PostAsync(to)           │
│  b. 任播：匹配第一个能力匹配节点        │
│     → PostAsync(first_match)           │
│  c. 广播：Metadata["route"]="broadcast"│
│     → BroadcastAsync                   │
│  默认：任播（best-effort delivery）     │
└────────────────────────────────────────┘
```

### 5.2 能力声明模型

每个 Agent 启动时声明能力，通过 P2P 心跳广播给同 Workspace 节点：

```csharp
public sealed record AgentCapabilityDeclaration
{
    public required string AgentId { get; init; }
    public required string WorkspaceId { get; init; }
    public List<string> Tags { get; init; } = [];              // "ci", "iot", "code-review"
    public List<string> HandledEventTypes { get; init; } = [];  // "github.webhook.push"
    public List<string> ToolNames { get; init; } = [];          // "FileTool", "ShellTool"
    public int CurrentLoad { get; init; }                       // 当前活跃子代理数
    public DateTimeOffset UpdatedAt { get; init; }
}
```

### 5.3 离线与死信处理

| 场景 | 策略 |
|------|------|
| 目标节点离线（单播/任播） | 重试 3 次（指数退避：1s→5s→25s），仍失败 → SQLite 死信表 |
| 广播时部分节点离线 | 尽力送达；离线节点下次心跳时通过 `ISwarmTransport.ReceiveAsync` 追回 |
| 死信持久化 | SQLite `DeadLetterEvent` 表，查询和手动重放接口，30 天自动清理 |

### 5.4 事件去重

使用 `EnvelopeId` 做幂等键。`BranchSessionManager` 创建分支前检查是否已存在同 `TriggerEventId` 的分支。

---

## 6. V1 实施建议

### 6.1 优先级排序

| 优先级 | 内容 | 理由 |
|--------|------|------|
| **P0 立即** | `IPuddingConnector` 接口定义（`PuddingCore`） | 所有连接器的基接口，先行定义 |
| **P0 立即** | `ConnectorHost`（替代 `GatewayAdapterHost`） | 连接器生命周期管理 |
| **P0 立即** | Webhook 连接器 | 最低复杂度，最广泛适用 |
| **P0 立即** | EventRouter（事件路由核心） | 所有事件流的中枢 |
| **P0 立即** | BranchSessionManager + 基础分支模型 | 不阻塞主会话是核心需求 |
| **P1 尽快** | 邮箱连接器（从 Adapter 升级，新增 OperateAsync） | 已有 EmailGatewayAdapter 基础 |
| **P1 尽快** | MQTT 连接器 | IoT/智能家居关键入口 |
| **P1 尽快** | 子代理模型（逻辑分区） | 依赖 BranchSessionManager |
| **P1 尽快** | P2P 事件路由（能力声明 + 任播） | 多 Agent 协作基础 |
| **P2 延后** | 旧 Adapter → Connector 迁移（WebChat/Cli） | 向后兼容，逐步迁移 |
| **P2 延后** | 死信队列与重放 | V1 日志记录即可 |
| **P2 延后** | Merge 策略 L2/L3 | L1 摘要注入覆盖 80% |
| **P3 远期** | 插件动态加载 | V1 编译时 DI 注册已足够 |

### 6.2 V1 最小可行链路

```
外部 Webhook POST → WebhookConnector
  → EventRouter（判定自己处理）
  → BranchSessionManager.ForkBranch()
  → 子代理（分支内 AgentInstance）
  → LLM 对话处理事件
  → Merge（L1 摘要注入主会话）
```

这条链路覆盖了最核心的价值：**Agent 能响应外部事件而不打断正在进行的对话**。

### 6.3 新增/修改/不变汇总

| 新增 | 修改 | 不变 |
|------|------|------|
| `IPuddingConnector` 接口（`PuddingCore/Platform/`） | `GatewayAdapterHost` → `ConnectorHost` | `IPuddingGatewayAdapter` 接口（保留兼容） |
| `ConnectorHost`（`PuddingAgent/Gateway/`） | `AgentSessionManager`（+Fork/Merge） | 已有 3 个旧 Adapter（逐步迁移） |
| `EventRouter` 服务（`PuddingAgent/Controller/`） | `AgentInstanceRecord`（+ParentSessionId） | `ISwarmMessageHub`/`ISwarmTransport` |
| `BranchSessionManager`（`PuddingAgent/Runtime/`） | `CoordinationEventKind`（+SubAgent 事件） | `PuddingIngressEnvelope`/`EgressEnvelope` |
| `WebhookConnector`（`PuddingAgent/Connectors/`） | | Swarm 全部基础设施 |
| `EmailConnector`（`PuddingAgent/Connectors/`）★升级 | | |
| `MqttConnector`（`PuddingAgent/Connectors/`） | | |
| `AgentCapabilityDeclaration`（`PuddingCore/Models/`） | | |
| `BranchSessionRecord`（`PuddingCore/Models/`） | | |

---

## 7. 风险评估

### 7.1 架构风险

| 风险 | 等级 | 缓解措施 |
|------|------|---------|
| **SQLite 写锁竞争** | 中 | V1 分支数少（<5）；WAL 模式下并发读无阻塞 |
| **LLM 上下文膨胀** | 中 | 默认 L1 摘要注入；L3 需显式开启 |
| **子代理资源泄漏** | 低 | 后台定时器 30s 扫描超时分支，自动 ForceMerge |
| **P2P 能力声明过期** | 低 | 心跳附带能力声明，缓存 TTL=心跳间隔×3 |

### 7.2 迁移风险

| 风险 | 等级 | 缓解措施 |
|------|------|---------|
| **已有 Adapter 破坏** | 低 | 新增接口为可选实现，不强制 |
| **AgentSessionManager 变更** | 低 | 纯新增方法，不改签名 |
| **CoordinationEventKind 扩展** | 低 | 订阅者按需过滤，新事件对旧代码透明 |

### 7.3 待定决策清单

| 决策项 | 状态 | 说明 |
|--------|------|------|
| 分支会话 LLM 上下文 fork 深度 N | 已定 N=5 | 可后续调整为动态计算 |
| 分支超时默认值 | 已定 5min | 可根据事件类型差异化 |
| MQTT Broker 选择 | 待定 | Mosquitto 作为参考实现 |
| Webhook 签名算法选择 | 待定 | 建议支持 HMAC-SHA256 + 自定义 Header |

---

## 8. 调试与可观测性

- 所有日志带 `SessionId` 和 `BranchSessionId`
- Web UI 新增**分支树视图**（类似 Git Graph）
- `IPuddingConnector.GetDiagnosticsAsync()` 提供连接器健康数据
- 死信表提供查询 API：`GET /api/dead-letters?workspaceId=xxx`
