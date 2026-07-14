# 16 会话状态层与客户端解耦 (Session State Layer & Client Decoupling)

> **演进说明（2026-07-13）**：本 ADR 关于 SSM、append-only Event Log、多观察者和 Channel 生命周期的决策继续有效。[ADR-056 聊天消息受理与可靠事件流架构](57ADR-056聊天消息受理与可靠事件流架构ADR.md) 进一步规定：聊天执行不得由请求内裸 `Task.Run` 承载；事件必须先持久化再发布；Session SSE 必须通过 sequence cursor 将 replay 与 live 无缝衔接；实时 Channel 只是易失加速器，不构成可靠历史。

> 状态：**proposed**
> 作者：@architect (战略 ADR)
> 日期：2026-05-15
> 触发条件：A(新架构模式/抽象层) + B(跨3+模块不可逆数据变更) — 满足 2/5 条件
> 关联：[02PuddingCore](02PuddingCore.md)、[03PuddingRuntime](03PuddingRuntime.md)、[04PuddingController与Gateway](04PuddingController与Gateway.md)、[05PuddingPlatform](05PuddingPlatform.md)、[06PuddingAgent与客户端](06PuddingAgent与客户端.md)、[10事件系统与事件总线](10事件系统与事件总线.md)、[12多轮会话与工具调用执行](12多轮会话与工具调用执行.md)、[15潜意识LLM子代理系统ADR](15潜意识LLM子代理系统ADR.md)

---

## 1. 背景与现状诊断

### 1.1 核心问题：执行引擎与客户端强耦合

当前架构中，Agent 执行引擎的 SSE 输出直接绑定到前端 HTTP 连接：

```
AgentExecutionService (Runtime)
  → RuntimeDispatcher (Controller, HTTP relay)
    → ChatApiController (Platform)
      → 临时 Channel<ServerSentEventFrame> (Task.Run 写入)
        → HTTP Response (SSE) → 浏览器
```

**关键缺陷**：

| # | 问题 | 根因 |
|---|------|------|
| 1 | 前端断开后帧丢失 | 临时 Channel 绑定 HTTP Response 生命周期；`Task.Run` 继续运行但帧无处可去 |
| 2 | `done` 后 SessionEventHub Channel 销毁 | `CompleteAndRemove` 在 `done` 到达时立即调用；异步子代理完成时 Channel 已不存在 |
| 3 | 思维链/工具调用不可回放 | 所有帧仅在流式传输时可见；流结束后 `thinking`/`tool_call`/`tool_result` 全部丢失 |
| 4 | 不支持滚动加载历史 | 消息以最终 `replyText` 形式整体持久化，过程细节（中间工具调用）不可恢复 |
| 5 | 切换会话 = 看到空白 | 只有当前发起 SSE 的会话有实时流；切换到其他会话时完全没有"追流"能力 |
| 6 | 多客户端无法感知 | 前端是唯一的"流观察者"；移动端/桌面端/浏览器插件无法共享同一会话的实时状态 |
| 7 | 子代理异步完成不可见 | 用户关闭浏览器后，异步子代理完成 → AgentEventHandler 注入上下文 → 前端永远不知道 |

### 1.2 当前链路的精确诊断

**临时 Channel 与 SessionEventHub 的关系**：

```
ChatApiController.SendMessageStream():
  tempChannel = Channel.CreateBounded(256, DropWrite)  // 临时 Channel
  hubChannel = null                                      // 延迟创建

  Task.Run(async () => {
      foreach frame in apiClient.SendMessageStreamAsync():
          ParseFrame(frame)
          if hubChannel == null && streamSessionId != null:
              hubChannel = eventHub.GetOrCreate(streamSessionId)  // ← 注册到 EventHub
          tempChannel.Writer.TryWrite(frame)                       // ← 写入临时 Channel
          hubChannel?.Writer.TryWrite(frame)                       // ← 写入 EventHub
      tempChannel.Writer.Complete()                                // ← 标记完成
      eventHub.CompleteAndRemove(streamSessionId)                  // ← 销毁 EventHub Channel ← 问题！
  })

  await foreach frame in tempChannel.Reader.ReadAllAsync(ct):     // ← HTTP Response 绑定
      WriteRawSseAsync(Response, frame, ct)                        // ← 前端断开→ct 取消→循环结束
```

**异步子代理完成时的推送尝试**（`AgentEventHandler.TryPushToSessionHub`）：

```csharp
// AgentEventHandler.cs — 反射调用 SessionEventHub
hubType.GetMethod("GetOrCreate")?.Invoke(hub, [parentSessionId]);
// ↑ 此时 Channel 通常已被 CompleteAndRemove 销毁！
// GetOrCreate 会创建新 Channel，但已没有 SSE 订阅者读取
```

**结论**：整个链路以"前端 HTTP 连接"为中心设计——连接在、数据在；连接断、数据丢。必须从架构层面解耦。

### 1.3 问题抽象

> **当前把"执行过程"当作"流"对待。但"流"是瞬时的、不可回溯的。真正需要的是一个"会话事件日志"（Session Event Log），它是持久的、可查询的、支持多观察者的。**

| 模型 | 当前（Event Stream） | 目标（Session Event Log） |
|------|---------------------|--------------------------|
| 存储 | 不存储，流完即忘 | SQLite 持久化，append-only |
| 观察者 | 1 个（当前 SSE 连接） | N 个（Web / Mobile / Desktop / CLI） |
| 回溯 | 不支持 | 支持（从任意序列号分页加载） |
| 前端关闭 | 事件丢失 | 事件持久化，重连后补齐 |
| 子代理完成 | 尝试推送到已销毁/无订阅者的 Channel | 写入 Event Log → 通知所有活跃观察者 |
| 历史加载 | 仅加载最终 replyText | 加载完整事件日志 → 重建 Timeline |

---

## 2. 战略方向决策

### ADR-016-A：新增 Session State Manager 中间层

**方案对比**

| 方案 | 优点 | 缺点 | 评分 |
|------|------|------|------|
| **A. 新增 `ISessionStateManager` 中间层** | 不破坏现有执行引擎；清晰分层；所有客户端统一接入 | 多一层间接；需新建 SQLite 表 | ✅ |
| B. 改造 `SessionEventHub` 吞并所有能力 | 接口不变 | 违反单一职责（Channel 管理 + 持久化 + 历史查询混在一起）；单点膨胀 | ❌ |
| C. 在 `AgentExecutionService` 内建日志 | 改动最小 | 执行引擎不应关心存储/推送/客户端管理 | ❌ |
| D. 在 `ChatApiController` 内建日志 | 前端最近 | Controller 层不应持有业务状态；违反分层原则 | ❌ |

**决定**：方案 A。在"执行引擎"和"所有客户端"之间插入 `ISessionStateManager`，它同时承担三个角色：

1. **持久化事件日志**（SQLite append-only，所有执行细节不可变记录）
2. **实时推送通道**（Channel per session，生命周期独立于 HTTP 连接）
3. **子代理状态追踪**（跨父/子会话的状态协调）

```
                        用户消息
                           │
                           ▼
┌──────────────────────────────────────────────────────┐
│  AgentExecutionService (执行引擎，不变)                │
│  · LLM 调用 → 工具调用 → 子代理                       │
│  · 每帧 → SessionStateManager.AppendAsync() ← 新入口  │
└───────────────────────┬──────────────────────────────┘
                        │
                        ▼
┌──────────────────────────────────────────────────────┐
│  ISessionStateManager (会话状态层) ← 本次新增         │
│                                                       │
│  ┌──────────────────┐  ┌──────────────────┐          │
│  │ SessionEventLog  │  │ SubAgentTracker   │          │
│  │ (SQLite 持久化)  │  │ (status 追踪)     │          │
│  └────────┬─────────┘  └──────┬───────────┘          │
│           │                   │                       │
│  ┌────────▼───────────────────▼───────┐              │
│  │ Channel per session (内存推送)      │              │
│  │ · 生命周期: create → closed        │              │
│  │ · 与 HTTP 连接生命周期完全解耦      │              │
│  └────────┬───────────────────────────┘              │
└───────────┼──────────────────────────────────────────┘
            │
   ┌────────┼────────┬──────────────┐
   ▼        ▼        ▼              ▼
┌──────┐ ┌──────┐ ┌──────┐    ┌──────┐
│Web前端│ │移动端│ │桌面端│    │P2P节点│
│(SSE) │ │(SSE) │ │(SSE) │    │(内部)│
└──────┘ └──────┘ └──────┘    └──────┘
```

### ADR-016-B：Channel 生命周期与会话生命周期解耦

**当前**：Channel 在 HTTP Response 结束时立即销毁（`done` 帧到达 → `CompleteAndRemove`）

**新设计**：

```
Channel 生命周期状态机：

  create ──→ streaming ──→ stream-completed ──→ closed
               │                                      ↑
               │                              (所有子代理完成)
               │                                      │
               └──→ cancelled ────────────────────────┘
```

- `create`：首次 `GetOrCreateChannel()` 调用
- `streaming`：主代理流式执行中（持续写入帧）
- `stream-completed`：主代理 `done` 帧已写入；异步子代理可能还在运行
- `closed`：所有异步子代理完成，无更多事件将产生；可清理 Channel（带 TTL，如闲置 30 分钟）

**关键**：Channel 的销毁不再由"主代理 done"触发，而是由"会话完全关闭"触发。

### ADR-016-C：事件日志 append-only 不可变

**决定**：`session_event_log` 表采用 append-only 设计，永远不修改/删除已写入的事件。

| 属性 | 值 |
|------|-----|
| 写入模式 | INSERT ONLY，无 UPDATE/DELETE |
| 主键 | `(session_id, sequence_num)` 联合唯一 |
| 序列号 | 单会话内严格单调递增（由 SQLite `MAX+1` 或应用层原子递增保证） |
| 游标策略 | 前端记住最后看到的 `sequence_num`，重连时 `GET /events?from=seq&limit=N` |
| 保留策略 | 与 Session 生命周期一致（Session 删除时级联清理） |

**优势**：
- 避免时钟不同步问题（序列号替代时间戳做游标）
- 天然支持"断点续传"（前端从 seq+1 继续）
- 支持多点观察（不同客户端独立维护各自的"已读序列号"）
- 天然审计日志（不可变记录）

### ADR-016-D：前端双 SSE 连接模式

**决定**：

```
┌─────────────────────────────────────────────┐
│ 连接 1: 会话 SSE (session-specific)          │
│   · 端点: GET /api/sessions/{id}/events/stream│
│   · 用途: 当前选中会话的实时事件              │
│   · 生命周期: 切换会话时断开/重连             │
├─────────────────────────────────────────────┤
│ 连接 2: 工作区 SSE (workspace-wide)           │
│   · 端点: GET /api/workspaces/{id}/notifications/stream│
│   · 用途: 所有会话的摘要通知                  │
│     - "会话 P 的异步子代理已完成"             │
│     - "Cron 作业触发了新会话"                 │
│     - "连接器收到新消息"                      │
│   · 生命周期: 页面打开 → 关闭                  │
├─────────────────────────────────────────────┤
│ REST: 历史加载 (on-demand)                    │
│   · 端点: GET /api/sessions/{id}/events?from={seq}&limit={N}│
│   · 用途: 首次加载 / 滚动加载更早的历史       │
│   · 触发: 进入会话 / 用户向上滚动             │
└─────────────────────────────────────────────┘
```

### ADR-016-E：子代理状态追踪独立管理

**决定**：子代理状态不嵌入事件日志（事件日志只有 SubAgentSpawned / SubAgentCompleted 帧），另建 `session_sub_agents` 表追踪运行时状态。

**原因**：
- 事件日志是不可变的"事实记录"，不适合追踪"当前运行中"的易变状态
- 子代理状态查询（"当前有几个在运行"）需要 O(1)，不应扫描事件日志
- 子代理表的字段（status/running→completed）比事件日志更适合状态机管理

---

## 3. 领域模型

### 3.1 新增接口：`ISessionStateManager`

```csharp
// 位置: Source/PuddingCore/Abstractions/ISessionStateManager.cs
namespace PuddingCode.Abstractions;

using System.Threading.Channels;
using PuddingCode.Platform;

/// <summary>
/// 会话状态管理器 — 执行引擎与所有客户端之间的唯一中间层。
/// 
/// 三大职责：
///   1. 持久化事件日志 — append-only SQLite 存储所有执行帧
///   2. 实时推送通道 — Channel per session，生命周期独立于 HTTP 连接
///   3. 子代理状态追踪 — 跨父/子会话的状态协调
///
/// 设计原则：
///   · 执行引擎不感知客户端（前端/移动端/桌面端）
///   · 所有客户端通过此接口获取实时事件和历史事件
///   · Channel 生命周期 = 会话完全关闭，而非 HTTP 连接断开
/// </summary>
public interface ISessionStateManager
{
    // ════════════════════════════════════════════════════════
    // 事件追加（append-only）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 向会话事件日志追加一帧。返回全局递增序列号。
    /// 同时写入 SQLite（持久化）和内存 Channel（实时推送）。
    /// </summary>
    Task<long> AppendAsync(
        string sessionId, string workspaceId,
        ServerSentEventFrame frame,
        CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // 历史加载（分页/游标）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 从指定序列号加载 N 条事件。fromSeq=null 表示从最新开始（向前加载更早的）。
    /// </summary>
    Task<SessionEventPage> GetEventsAsync(
        string sessionId,
        long? fromSequence = null,
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// 获取会话中指定序列号之后的事件总数（用于增量加载判断）。
    /// </summary>
    Task<long> GetEventCountAfterAsync(string sessionId, long afterSequence, CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // 实时订阅
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 获取会话的实时事件 Channel Reader。不存在则创建。
    /// Channel 生命周期由内部状态机管理，不随调用者释放而销毁。
    /// </summary>
    ChannelReader<ServerSentEventFrame> Subscribe(string sessionId);

    /// <summary>
    /// 订阅工作区级别的通知 Channel（所有会话的摘要事件）。
    /// </summary>
    ChannelReader<SessionNotification> SubscribeWorkspace(string workspaceId);

    // ════════════════════════════════════════════════════════
    // 会话状态
    // ════════════════════════════════════════════════════════

    /// <summary>获取会话当前状态。</summary>
    Task<SessionState> GetSessionStateAsync(string sessionId, CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // 子代理追踪
    // ════════════════════════════════════════════════════════

    /// <summary>追踪子代理创建。</summary>
    Task TrackSubAgentStartAsync(
        string parentSessionId, SubAgentSpawnInfo info,
        CancellationToken ct = default);

    /// <summary>追踪子代理完成。</summary>
    Task TrackSubAgentCompleteAsync(
        string subSessionId, SubAgentResult result,
        CancellationToken ct = default);

    /// <summary>获取会话的所有子代理状态（含运行中和已完成的）。</summary>
    Task<IReadOnlyList<SubAgentStatus>> GetSubAgentsAsync(
        string sessionId, CancellationToken ct = default);

    /// <summary>获取会话中正在运行的子代理数量。</summary>
    Task<int> GetRunningSubAgentCountAsync(string parentSessionId, CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // 生命周期标记
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 标记主代理流式执行结束（done 帧已发送）。
    /// 不销毁 Channel — 异步子代理可能还在运行。
    /// </summary>
    Task MarkStreamCompleteAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 标记会话完全关闭（无更多事件将产生，所有子代理完成）。
    /// 启动 Channel 清理倒计时（TTL 后可销毁）。
    /// </summary>
    Task MarkSessionClosedAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>会话事件分页结果。</summary>
public sealed record SessionEventPage
{
    /// <summary>事件列表（按序列号升序）。</summary>
    public required IReadOnlyList<SessionEventEntry> Events { get; init; }

    /// <summary>是否还有更早的事件可加载。</summary>
    public bool HasMore { get; init; }

    /// <summary>本页最小序列号（用于下次加载 from=minSeq）。</summary>
    public long MinSequence { get; init; }

    /// <summary>本页最大序列号。</summary>
    public long MaxSequence { get; init; }

    /// <summary>会话总事件数。</summary>
    public long TotalCount { get; init; }
}

/// <summary>事件日志中的单条事件。</summary>
public sealed record SessionEventEntry
{
    public required long SequenceNum { get; init; }
    public required string EventType { get; init; }
    public required string Data { get; init; }       // JSON payload
    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>会话运行时状态。</summary>
public enum SessionState
{
    Streaming,          // 主代理正在流式执行
    StreamCompleted,    // 主代理流式已完成，可能有异步子代理在运行
    Closed              // 会话完全关闭，无更多事件
}

/// <summary>工作区级通知。</summary>
public sealed record SessionNotification
{
    public required string Type { get; init; }          // "sub_agent_completed" | "session_created" | ...
    public required string SessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? AgentId { get; init; }
    public string? SessionTitle { get; init; }
    public object? Data { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```

### 3.2 子代理追踪 DTO

```csharp
/// <summary>子代理创建信息。</summary>
public sealed record SubAgentSpawnInfo
{
    public required string SubSessionId { get; init; }
    public required string ParentSessionId { get; init; }
    public string? ParentAgentId { get; init; }
    public string? TemplateId { get; init; }
    public string? ModelId { get; init; }
    public required string TaskSummary { get; init; }
    public required DateTimeOffset SpawnedAt { get; init; }
}

/// <summary>子代理完成结果。</summary>
public sealed record SubAgentResult
{
    public required bool Success { get; init; }
    public string? Reply { get; init; }
    public string? Error { get; init; }
    public TokenUsageDto? Usage { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>子代理当前状态（查询用）。</summary>
public sealed record SubAgentStatus
{
    public required string SubSessionId { get; init; }
    public required string Status { get; init; }        // "running" | "completed" | "failed"
    public string? TemplateId { get; init; }
    public string? ModelId { get; init; }
    public required string TaskSummary { get; init; }
    public required DateTimeOffset SpawnedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ResultSummary { get; init; }
    public bool? Success { get; init; }
}
```

---

## 4. 持久化存储

### 4.1 会话事件日志表 `session_event_log`

```sql
CREATE TABLE IF NOT EXISTS session_event_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id      TEXT    NOT NULL,
    workspace_id    TEXT    NOT NULL,
    sequence_num    INTEGER NOT NULL,
    event_type      TEXT    NOT NULL,       -- SessionEventType 枚举值
    data            TEXT    NOT NULL,       -- JSON payload (ServerSentEventFrame.Data)
    recorded_at     TEXT    NOT NULL,       -- ISO8601 UTC

    UNIQUE(session_id, sequence_num)
);

CREATE INDEX IF NOT EXISTS idx_sel_session_seq
    ON session_event_log(session_id, sequence_num);

CREATE INDEX IF NOT EXISTS idx_sel_workspace_time
    ON session_event_log(workspace_id, recorded_at);
```

### 4.2 子代理状态追踪表 `session_sub_agents`

```sql
CREATE TABLE IF NOT EXISTS session_sub_agents (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_session_id   TEXT    NOT NULL,
    parent_agent_id     TEXT,
    sub_session_id      TEXT    NOT NULL UNIQUE,
    status              TEXT    NOT NULL DEFAULT 'running',  -- running | completed | failed
    template_id         TEXT,
    model_id            TEXT,
    task_summary        TEXT    NOT NULL,
    spawned_at          TEXT    NOT NULL,
    completed_at        TEXT,
    success             INTEGER,            -- NULL=running, 0=failed, 1=success
    reply_summary       TEXT,               -- 截断的 reply（≤200 字符）
    error_summary       TEXT,               -- 截断的 error（≤500 字符）
    full_result_json    TEXT                -- 完整结果 JSON（可选，大文本）
);

CREATE INDEX IF NOT EXISTS idx_ssa_parent
    ON session_sub_agents(parent_session_id, status);

CREATE INDEX IF NOT EXISTS idx_ssa_sub
    ON session_sub_agents(sub_session_id);
```

### 4.3 客户端读取位置表 `client_read_position`（V2）

```sql
-- 多客户端各自维护已读序列号，支持断点续传
CREATE TABLE IF NOT EXISTS client_read_position (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    client_id       TEXT    NOT NULL,       -- 客户端唯一标识（如设备ID + 浏览器指纹）
    session_id      TEXT    NOT NULL,
    last_read_seq   INTEGER NOT NULL DEFAULT 0,
    updated_at      TEXT    NOT NULL,

    UNIQUE(client_id, session_id)
);
```

V1 暂不实现此表——前端用内存状态维护已读序列号，重连时从 `GET /events?from=N` 加载。

---

## 5. 改造流程全景

### 5.1 异步子代理完整时间线（改造后）

```
时间线：用户 → 主代理 → 异步子代理 → 通知 → 前端感知

┌─ T0: 用户发送消息 ──────────────────────────────────────┐
│  ChatApiController.SendMessageStream()                   │
│    → SessionStateManager 创建会话事件日志                 │
│    → 前端 SSE: 订阅 session=P 的事件 Channel              │
└──────────────────────────────────────────────────────────┘

┌─ T1: 主代理执行，调用 spawn_sub_agent(sync=false) ──────┐
│  SubAgentTool.ExecuteAsync():                             │
│    1. 创建子会话 sub=S                                    │
│    2. SessionStateManager.TrackSubAgentStartAsync(        │
│         parent=P, sub=S, info)                            │
│    3. SessionStateManager.AppendAsync(P,                  │
│         frame: type=SubAgentSpawned, subAgentId=S)       │
│    4. _ = Task.Run(执行子代理)  // fire-and-forget        │
│    5. return "子代理 S 已启动"  → 主代理继续               │
│                                                           │
│  前端 SSE 立即收到 SubAgentSpawned 事件                    │
│    → SubAgentIndicator 更新: runningCount=1               │
└──────────────────────────────────────────────────────────┘

┌─ T2: 主代理完成 ─────────────────────────────────────────┐
│  AgentExecutionService → done 帧:                         │
│    → SessionStateManager.AppendAsync(P, type=Done)        │
│    → SessionStateManager.MarkStreamCompleteAsync(P)       │
│    → Channel 不销毁（子代理 S 还在运行）                   │
│                                                           │
│  前端 SSE 收到 Done 事件                                   │
│    → 主代理回答渲染完成                                    │
│    → "1 个子代理运行中" 持续显示                           │
└──────────────────────────────────────────────────────────┘

┌─ T3: 用户关闭浏览器 / 切到其他会话 ───────────────────────┐
│  前端 SSE 连接断开                                         │
│  后端不受影响：                                             │
│    · 子代理 S 继续在后台执行                               │
│    · SessionEventLog 持续记录子代理 S 的事件               │
│    · Channel 保持活跃（帧持续追加）                        │
└──────────────────────────────────────────────────────────┘

┌─ T4: 子代理 S 执行完成 ──────────────────────────────────┐
│  Task.Run 内部:                                            │
│    1. result = executionService.ExecuteAsync(S)            │
│    2. SessionStateManager.TrackSubAgentCompleteAsync(S)    │
│    3. SessionStateManager.AppendAsync(P,                   │
│         frame: type=SubAgentCompleted,                     │
│         data={subAgentId:S, success, reply, ...})          │
│    4. SessionStateManager 检查所有子代理:                  │
│       → GetRunningSubAgentCountAsync(P) == 0              │
│       → MarkSessionClosedAsync(P)                         │
│    5. IInternalEventBus.PublishAsync(                     │
│         "agent.sub_completed", ...)                       │
│                                                           │
│  AgentEventHandler:                                        │
│    1. 注入 <task-notification> 到 ContextWindowManager(P)  │
│    2. 不再 TryPushToSessionHub — SessionStateManager 已处理│
│                                                           │
│  工作区 SSE 频道推送 SessionNotification:                  │
│    → type: "sub_agent_completed", sessionId: P             │
└──────────────────────────────────────────────────────────┘

┌─ T5: 前端重新打开会话 P ─────────────────────────────────┐
│  场景A: Channel 还活着（子代理刚完成或还在运行）            │
│    1. GET /api/sessions/{P}/events?limit=50                │
│       → 加载历史事件（含 SubAgentSpawned, Done,             │
│                     SubAgentCompleted）                    │
│    2. 前端重建完整 Timeline                                │
│    3. SSE 连接到 Channel → 实时接收新帧                    │
│                                                           │
│  场景B: Channel 已销毁（会话完全关闭）                      │
│    1. GET /api/sessions/{P}/events?limit=50                │
│       → 加载完整历史                                       │
│    2. 前端重建完整 Timeline                                │
│    3. SSE 连接: Channel 不存在 → 建立短 SSE               │
│       （仅等待新用户消息触发时重建 Channel）                │
│                                                           │
│  场景C: 从工作区通知进入                                    │
│    1. 侧边栏收到工作区 SSE: "会话 P 子代理完成"            │
│    2. 侧边栏显示橙色圆点标记                               │
│    3. 用户点击 → 切换到会话 P                              │
│    4. 同场景 A 的加载流程                                  │
└──────────────────────────────────────────────────────────┘
```

### 5.2 执行引擎集成点

```csharp
// AgentExecutionService.ExecuteStreamAsync 的关键改动点：
async IAsyncEnumerable<ServerSentEventFrame> ExecuteStreamAsync(request) {
    // ... 现有初始化代码 ...
    
    var ssm = _services.GetRequiredService<ISessionStateManager>();
    long lastSeq = 0;

    for (int round = 0; round < maxRounds; round++) {
        await foreach (var delta in llmClient.ChatStreamAsync(...)) {
            // ... 现有帧构造逻辑 ...
            var frame = BuildFrame(delta);             // 构造 ServerSentEventFrame
            lastSeq = await ssm.AppendAsync(           // ← 新增：写入 SessionStateManager
                sessionId, workspaceId, frame, ct);
            yield return frame;                        // ← 仍然 yield 给上游 relay
        }
        // ... 工具执行循环 ...
    }

    // done 帧
    var doneFrame = ServerSentEventFrame.Json("done", new { reply, usage });
    await ssm.AppendAsync(sessionId, workspaceId, doneFrame, ct);
    await ssm.MarkStreamCompleteAsync(sessionId, ct);
    yield return doneFrame;
}
```

**关键**：`AppendAsync` 和 `yield return` 同时发生。`AppendAsync` 保证持久化和 Channel 推送；`yield return` 保持现有 relay 链兼容性。

---

## 6. Timeline 重建与回放

### 6.1 从事件日志重建 UI Timeline

```
事件日志序列 → 前端 Timeline 渲染

seq   type                渲染为
──────────────────────────────────────────
1     Metadata            [会话信息卡片]
2     Thinking            [💭 思维链卡片 (可折叠)]
3     Delta               "回答文本开始..."
4     ToolCall            [🔧 bash 卡片]
5     ToolResult          [    ✅ 成功 卡片]
6     Delta               "基于工具结果..."
7     SubAgentSpawned     [🤖 子代理 S 已启动 指示器]
8     Usage               [Token: 1,234]
9     Done                [✅ 主回答完成标记]
─(用户离开, 子代理 S 在后台运行)─
10    SubAgentCompleted   [🤖 子代理 S 完成 ✅ 卡片]
11    SessionClosed       [会话完全关闭标记]
```

### 6.2 前端加载协议

```
进入会话:
  1. GET /api/sessions/{id}/events?limit=50
     → 返回 seq 1-50（如果总共 50 条）
     → 从下往上渲染最新 50 条
  
  2. 用户向上滚动到顶:
     → GET /api/sessions/{id}/events?from=50&limit=50
     → 返回 seq 1-49（如果总共 50 条）
     → 在顶部追加渲染
  
  3. SSE 实时连接:
     → GET /api/sessions/{id}/events/stream
     → 收到帧 seq 51+ → 追加到 Timeline 底部
```

### 6.3 事件帧类型扩展

```csharp
/// <summary>会话事件类型（扩展到 SSE 事件名）。</summary>
public static class SessionEventTypes
{
    // ── 内容层 ──
    public const string Delta = "delta";
    public const string Thinking = "thinking";

    // ── 工具层 ──
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";

    // ── 子代理层 ──
    public const string SubAgentSpawned = "subagent.spawned";
    public const string SubAgentDelta = "subagent.delta";
    public const string SubAgentThinking = "subagent.thinking";
    public const string SubAgentToolCall = "subagent.tool_call";
    public const string SubAgentToolResult = "subagent.tool_result";
    public const string SubAgentCompleted = "subagent.completed";

    // ── 生命周期 ──
    public const string Done = "done";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
    public const string SessionClosed = "session.closed";

    // ── 元数据 ──
    public const string Metadata = "metadata";
    public const string Usage = "usage";

    // ── 系统通知 ──
    public const string Notification = "notification";
}
```

---

## 7. 与现有子系统的关系

| 子系统 | 关系 | 说明 |
|--------|------|------|
| **AgentExecutionService** | 直接写入方 | 每帧调用 `AppendAsync` |
| **SubAgentTool** | 子代理追踪 | `TrackSubAgentStartAsync` / `TrackSubAgentCompleteAsync` |
| **AgentEventHandler** | 子代理完成 → 通知 | 注入上下文后也写入 SessionStateManager |
| **事件系统 (V3)** | 上游事件源 | Cron/Connector/P2P 触发新会话时写入初始 Metadata 帧 |
| **潜意识系统 (ADR-015)** | 数据消费者 | 可从 SessionEventLog 中提取对话内容进行记忆整合 |
| **Token 计费系统** | 数据消费者 | Usage 帧天然是计费数据源 |
| **审计日志** | 数据消费者 | SessionEventLog 不可变记录 = 天然审计日志 |
| **认证鉴权** | 访问控制 | SSE/REST 端点通过 JWT + Workspace 权限约束 |

---

## 8. 实施路线图

| Phase | 内容 | 依赖 | 影响面 |
|-------|------|------|--------|
| **Phase 1** | `ISessionStateManager` 接口 + SQLite 实现（`session_event_log` + `session_sub_agents` 表） | 无 | `PuddingCore` + `PuddingPlatform` |
| **Phase 2** | `AgentExecutionService` 集成：每帧写入 `AppendAsync` | Phase 1 | `PuddingRuntime` |
| **Phase 3** | `SubAgentTool` 改造：TrackSubAgentStart/Complete 替代直接发布 InternalEvent | Phase 1 | `PuddingRuntime` |
| **Phase 4** | `AgentEventHandler` 改造：子代理完成 → `AppendAsync(SubAgentCompleted)` + `MarkSessionClosed`（不再反射调用 SessionEventHub） | Phase 1, 3 | `PuddingAgent` |
| **Phase 5** | 历史加载 REST API：`GET /api/sessions/{id}/events?from&limit` | Phase 1 | `PuddingPlatform` |
| **Phase 6** | 会话 SSE 端点：`GET /api/sessions/{id}/events/stream` | Phase 1, 2 | `PuddingPlatform` |
| **Phase 7** | 工作区通知 SSE：`GET /api/workspaces/{id}/notifications/stream` | Phase 1, 4 | `PuddingPlatform` |
| **Phase 8** | 前端 Timeline 重构：从事件日志重建完整 Timeline（含 thinking/tool 回放 + 滚动加载） | Phase 5, 6 | 前端 |
| **Phase 9** | 前端 SubAgentIndicator 对接工作区 SSE（实时 runningCount） | Phase 7 | 前端 |
| **Phase 10** | 多客户端抽象层（可选 V2） | Phase 7 | `PuddingCore` |

---

## 9. 文件/变更清单

| 操作 | 文件 | 说明 |
|------|------|------|
| **新增** | `PuddingCore/Abstractions/ISessionStateManager.cs` | ⭐ 核心接口 + DTO |
| **新增** | `PuddingCore/Platform/SessionEventTypes.cs` | 事件类型常量 |
| **新增** | `PuddingPlatform/Services/SessionStateManager.cs` | SQLite + Channel 实现 |
| **新增** | `PuddingPlatform/Controllers/Api/SessionEventsController.cs` | REST + SSE 端点 |
| **新增** | `PuddingPlatform/Controllers/Api/WorkspaceNotificationsController.cs` | 工作区通知 SSE |
| **修改** | `PuddingRuntime/Services/AgentExecutionService.cs` | `ExecuteStreamAsync` / `ExecuteAsync` 调用 `AppendAsync` |
| **修改** | `PuddingRuntime/Services/Skills/SubAgentTool.cs` | `TrackSubAgentStartAsync` / `TrackSubAgentCompleteAsync` |
| **修改** | `PuddingAgent/Services/Events/AgentEventHandler.cs` | `HandleSubCompletedAsync` → 通过 SessionStateManager 推送 |
| **修改** | `PuddingPlatform/Controllers/Api/ChatApiController.cs` | `SendMessageStream` 注册 SessionStateManager（替代临时 Channel） |
| **修改** | `PuddingAgent/Program.cs` | DI 注册 `ISessionStateManager` |
| **修改** | `PuddingPlatformAdmin/src/services/platform/api.ts` | 新增事件类型 + SSE/REST API |
| **修改** | `PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts` | Timeline 重建 + 滚动加载 |
| **修改** | `PuddingPlatformAdmin/src/pages/chat/components/SubAgentIndicator.tsx` | 对接工作区 SSE |

---

## 10. 风险与缓解

| 风险 | 等级 | 缓解 |
|------|------|------|
| Channel 内存泄漏（会话永不关闭） | 中 | Channel TTL 30 分钟 + 闲置扫描 BackgroundService |
| 事件日志膨胀（单会话数万帧） | 低 | SQLite 单表数万行无压力；序列号索引 + 分页查询 |
| 前端 SSE 双连接资源消耗 | 低 | 浏览器原生支持 6+ SSE 连接；2 个连接可忽略 |
| ExecuteStreamAsync 改造破坏现有 relay | 低 | `yield return` 保持不变；`AppendAsync` 是追加调用不改变帧内容 |
