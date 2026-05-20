# 24 核心架构组件边界与执行引擎拆分 ADR

> 状态：**accepted**
> 日期：2026-05-20
> 范围：执行引擎、上下文合成、LLM 调用、工具调用、子代理调用、会话层、事件系统、可观测性、配置解析
> 前置：[19架构基础设施增强下一步ADR](19架构基础设施增强下一步ADR.md)、[22架构基础设施硬化与行动路线ADR](22架构基础设施硬化与行动路线ADR.md)、[23运行时可观测性闭环与E2E验证基线ADR](23运行时可观测性闭环与E2E验证基线ADR.md)

---

## 1. 背景

Pudding 当前架构方向是合理的：单进程、`data` 目录透明配置、SQLite 查询索引、JSONL 回放、事件持久化、子代理 run archive、Admin 诊断。这套设计适合 V1 的部署目标，也适合后续多 Agent、多 LLM provider、显意识 LLM、潜意识 LLM、记忆图书馆、连接器扩展。

主要问题不在方向，而在核心职责边界正在变重：

- `AgentExecutionService` 约 2391 行，承担执行编排、上下文合成调用、LLM 调用、工具循环、SSE 写入、RuntimeActivity、子代理 run archive、错误终态处理等多种职责。
- `ContextPipeline` 约 1044 行，已经具备上下文分层能力，但缺少清晰的输入输出契约用于执行引擎审计和 timeline 聚合。
- `RuntimeActivity`、`InternalEvent`、`SessionEvent`、`SubAgentRun` 已经存在，但诊断语义尚未统一到一个执行生命周期。
- 子代理、记忆、连接器将继续扩展，如果执行引擎边界不拆，后续会形成“所有东西都塞进 AgentExecutionService”的结构性风险。

本 ADR 的目标是明确 Pudding 的核心组件边界，并给出渐进拆分方案，避免大重写。

---

## 2. 核心组件判断

Pudding 的核心不是单个类，而是一条执行链：

```text
配置与 Agent Profile
  -> 会话层
  -> 执行编排
  -> 上下文合成
  -> LLM 调用
  -> 工具调用
  -> 子代理 / 记忆 / 连接器
  -> 事件系统
  -> 可观测性与诊断
```

其中四个承重组件是：

| 组件 | 架构角色 | 当前风险 | 改进方向 |
|------|----------|----------|----------|
| 执行引擎 | Agent 行为主循环和生命周期编排 | 单类职责过重 | 拆成编排、生命周期、LLM 调用、工具调用、子代理调用 |
| 事件系统 | 系统神经系统，连接 Runtime/Session/SubAgent/Connector/Memory | 事件、activity、session frame 分散 | 统一 trace/timeline 投影 |
| 会话层 | 用户可见交互状态权威来源 | 容易和 Runtime 调度混淆 | 只负责用户可见状态、SSE、replay、session diagnostics |
| 配置/Profile | 多 provider、多 model、多 agent 的地基 | 过渡期存在文件与 DB 双源风险 | 明确文件为配置权威，DB 为索引/运行态 |

---

## 3. 决策

### ADR-024-A：执行引擎采用“薄编排器 + 专职服务”结构

**决定**：`AgentExecutionService` 逐步降级为编排器，不再直接承载所有执行细节。

目标结构：

```text
AgentExecutionService
  ├─ IExecutionLifecycleRecorder
  ├─ IContextAssemblyService
  ├─ ILlmInvocationService
  ├─ IToolInvocationService
  ├─ ISubAgentInvocationService
  └─ ISessionOutputWriter
```

原则：

- 不一次性重写主循环。
- 先提取无行为变化的 facade。
- 每提取一个边界，必须加契约测试或集成测试。
- `AgentExecutionService` 保留流程编排，但不得新增复杂私有方法。

### ADR-024-B：执行生命周期成为 Runtime 的统一语言

**决定**：执行过程必须显式产生生命周期记录，所有关键组件使用同一套状态和事件语义。

生命周期状态：

```text
queued
assembling_context
context_assembled
calling_llm
streaming
tool_calling
waiting_subagent
writing_memory
completed
failed
cancelled
```

生命周期记录契约：

```csharp
public sealed record ExecutionLifecycleRecord
{
    public required string ExecutionId { get; init; }
    public required string TraceId { get; init; }
    public string? CorrelationId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string Component { get; init; }
    public required string Operation { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long? DurationMs { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
```

Recorder 契约：

```csharp
public interface IExecutionLifecycleRecorder
{
    Task<string> StartAsync(ExecutionLifecycleRecord record, CancellationToken ct = default);
    Task CompleteAsync(string activityId, string status, string? summary = null, string? error = null, CancellationToken ct = default);
    Task RecordInstantAsync(ExecutionLifecycleRecord record, CancellationToken ct = default);
}
```

实现策略：

- 第一阶段由 `RuntimeActivitySink` 作为持久化后端。
- 后续可同时投影到事件系统或 timeline。
- 不新增第二套主存储。

### ADR-024-C：上下文合成必须输出可审计摘要

**决定**：上下文合成服务不只返回 message list，还必须返回层级摘要、token 预算、记忆命中、压缩行为。

契约：

```csharp
public sealed record ContextAssemblyRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string UserMessage { get; init; }
    public required string LlmProfileId { get; init; }
    public int MaxContextTokens { get; init; }
}

public sealed record ContextAssemblyResult
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public required int EstimatedTokens { get; init; }
    public required IReadOnlyList<ContextLayerSummary> Layers { get; init; }
    public string? CompactionMode { get; init; }
    public string? MemoryRecallMode { get; init; }
}

public sealed record ContextLayerSummary
{
    public required string Layer { get; init; }
    public required int EstimatedTokens { get; init; }
    public required int ItemCount { get; init; }
    public string? Source { get; init; }
    public string? Summary { get; init; }
}
```

规则：

- `ContextPipeline` 可以保留内部实现，但对执行引擎暴露 `IContextAssemblyService`。
- 记忆召回、潜意识 LLM、压缩策略必须进入 `ContextAssemblyResult.Layers`。
- timeline 中必须能看到 context assembly 的耗时和摘要。

### ADR-024-D：LLM 调用边界独立于执行编排

**决定**：执行引擎不直接处理 provider 协议细节，只调用 `ILlmInvocationService`。

契约：

```csharp
public sealed record LlmInvocationRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string ProfileId { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<LlmToolDefinition> Tools { get; init; } = Array.Empty<LlmToolDefinition>();
    public RuntimeTraceContext? Trace { get; init; }
}

public sealed record LlmInvocationResult
{
    public required bool Success { get; init; }
    public string? ReplyText { get; init; }
    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = Array.Empty<LlmToolCall>();
    public TokenUsageDto? Usage { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? Error { get; init; }
}

public interface ILlmInvocationService
{
    Task<LlmInvocationResult> InvokeAsync(LlmInvocationRequest request, CancellationToken ct = default);
    IAsyncEnumerable<StreamDelta> InvokeStreamAsync(LlmInvocationRequest request, CancellationToken ct = default);
}
```

规则：

- `DirectLlmClient` 或 `ControllerRoutedLlmClient` 是协议客户端，不承担执行生命周期编排。
- provider/model/profile 解析结果必须进入 lifecycle metadata。
- 显意识 LLM、潜意识 LLM 使用同一 profile 解析模型，但通过 role 区分。

### ADR-024-E：工具调用必须独立审计和权限拦截

**决定**：工具调用从执行循环中提取到 `IToolInvocationService`，统一处理权限、审计、耗时、错误。

契约：

```csharp
public sealed record ToolInvocationRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public RuntimeTraceContext? Trace { get; init; }
}

public sealed record ToolInvocationResult
{
    public required bool Success { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
    public string ArgsHash { get; init; } = "";
    public int OutputLength { get; init; }
}

public interface IToolInvocationService
{
    Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct = default);
}
```

规则：

- `IAgentWorkspaceGuard` 必须在工具真正执行前调用。
- 工具拒绝不能静默吞掉，必须进入 session frame、RuntimeActivity、timeline。
- 子代理 run archive 的 `tools.jsonl` 从该服务写入。

### ADR-024-F：会话层只管理用户可见状态

**决定**：会话层不承担 Runtime 内部调度，不直接理解 provider 协议或工具执行细节。

会话层职责：

- SSE frame 追加。
- SQLite + JSONL 双写。
- replay。
- session diagnostics。
- 用户可见错误帧。
- 子代理完成摘要帧。

非职责：

- 不决定是否重试 LLM。
- 不执行工具。
- 不直接启动子代理。
- 不解析 provider/model。

输出契约：

```csharp
public interface ISessionOutputWriter
{
    Task WriteFrameAsync(
        string sessionId,
        string workspaceId,
        ServerSentEventFrame frame,
        RuntimeTraceContext? trace = null,
        CancellationToken ct = default);
}
```

初期实现可由 `SessionStateManager` 适配。

### ADR-024-G：子代理是独立执行单元，不是普通工具输出

**决定**：子代理继续作为独立 Agent 实例处理，保留 run archive、workspace 隔离、trace 关联。

调用契约：

```csharp
public sealed record SubAgentInvocationRequest
{
    public required string ParentSessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ParentAgentInstanceId { get; init; }
    public required string TemplateId { get; init; }
    public required string Task { get; init; }
    public bool IsAsync { get; init; }
    public RuntimeTraceContext? Trace { get; init; }
}

public sealed record SubAgentInvocationResult
{
    public required string SubSessionId { get; init; }
    public string? RunId { get; init; }
    public required string Status { get; init; }
    public string? Reply { get; init; }
    public string? Error { get; init; }
}

public interface ISubAgentInvocationService
{
    Task<SubAgentInvocationResult> InvokeAsync(SubAgentInvocationRequest request, CancellationToken ct = default);
}
```

规则：

- `SubAgentManager` 负责子代理生命周期和父会话摘要。
- `AgentExecutionService` 不直接操作子代理 run 文件细节。
- 子代理 run terminal 状态仍由执行路径的唯一 owner 写入。

---

## 4. 修改方案

### Phase 1：建立契约，不改行为

目标：先增加接口和 DTO，让后续拆分有稳定边界。

交付：

- `Source/PuddingCore/Runtime/ExecutionLifecycleContracts.cs`
- `Source/PuddingCore/Runtime/ContextAssemblyContracts.cs`
- `Source/PuddingCore/Runtime/LlmInvocationContracts.cs`
- `Source/PuddingCore/Runtime/ToolInvocationContracts.cs`
- `Source/PuddingCore/Runtime/SubAgentInvocationContracts.cs`
- `Source/PuddingCore/Runtime/SessionOutputContracts.cs`

验收：

- 新契约可编译。
- 不改变现有执行路径。
- 增加 contract serialization 或 model tests。

### Phase 2：生命周期记录器适配 RuntimeActivity

目标：把执行生命周期统一投影到现有 RuntimeActivity，不新增主存储。

交付：

- `RuntimeActivityExecutionLifecycleRecorder`
- DI 注册 `IExecutionLifecycleRecorder`
- `AgentExecutionService` 用 recorder 替代部分 `RecordActivityAsync`

验收：

- 原 RuntimeActivity 记录仍存在。
- 新 recorder 可通过 tests 验证 started/completed/failed。
- timeline ADR-023 可直接消费。

### Phase 3：上下文合成 facade

目标：包装 `ContextPipeline`，输出 `ContextAssemblyResult`。

交付：

- `ContextAssemblyService`
- `ContextAssemblyResult` 层级摘要
- `AgentExecutionService` 通过 `IContextAssemblyService` 调用上下文合成

验收：

- 现有上下文测试继续通过。
- 新增测试验证 context layer summary。
- 执行日志包含 context token estimate 和 layer count。

### Phase 4：LLM 调用 facade

目标：把 LLM 调用从执行编排中隔离出来。

交付：

- `LlmInvocationService`
- `ILlmInvocationService` DI 注册
- 非流式路径先迁移，流式路径后迁移

验收：

- Fake LLM tests 通过。
- token usage、provider/model/profile 进入 lifecycle metadata。
- 失败时返回结构化 `LlmInvocationResult.Error`。

### Phase 5：工具调用 facade

目标：统一工具权限、审计、耗时、错误。

交付：

- `ToolInvocationService`
- `IAgentWorkspaceGuard` 接入真实工具执行点
- 工具结果统一输出 `ToolInvocationResult`

验收：

- 工具调用成功/失败均进入 RuntimeActivity。
- 权限拒绝进入 session frame。
- 子代理 run archive `tools.jsonl` 使用统一结果。

### Phase 6：子代理调用 facade

目标：隔离子代理生命周期和父执行循环。

交付：

- `SubAgentInvocationService`
- `SubAgentManager` 作为底层生命周期实现继续保留
- `AgentExecutionService` 只依赖 `ISubAgentInvocationService`

验收：

- 异步子代理仍能写父会话完成帧。
- 同步子代理仍能返回结果。
- run archive terminal 单写者测试继续通过。

### Phase 7：收敛 AgentExecutionService

目标：`AgentExecutionService` 成为薄编排器。

约束：

- 不要求一次性压到固定行数。
- 新增复杂逻辑必须落到专职服务。
- 私有方法只保留流程辅助，不承载跨组件职责。

验收：

- 主循环可读性提升。
- 关键行为由 facade tests 覆盖。
- E2E smoke 通过。

---

## 5. 架构边界规则

### 5.1 Runtime 层

Runtime 可以依赖：

- PuddingCore contracts。
- Memory abstractions。
- Platform 提供的抽象实现通过 DI 注入。

Runtime 不应直接依赖：

- EF Entity。
- Admin UI DTO。
- 文件系统路径细节，除非通过抽象。

### 5.2 Platform 层

Platform 负责：

- SQLite/EF。
- API controller。
- 文件归档实现。
- session state 持久化。
- runtime activity 持久化。

Platform 不负责：

- provider 协议细节。
- Agent 主循环策略。

### 5.3 Core 层

Core 负责：

- 契约。
- DTO。
- 事件模型。
- 配置模型。
- 纯函数解析逻辑。

Core 不负责：

- EF。
- 文件 IO。
- HTTP。
- Runtime 具体执行。

---

## 6. 风险与缓解

| 风险 | 影响 | 缓解 |
------|------|------|
| 过早大重写 | 破坏已工作的聊天链路 | facade-first，每阶段保持行为不变 |
| 接口过度设计 | 增加样板代码 | 只抽当前已有职责，不为空想功能建接口 |
| 双写观测数据不一致 | timeline 误导诊断 | recorder 先投影 RuntimeActivity，timeline 只读现有数据 |
| 流式路径迁移风险高 | SSE 回归 | 非流式先迁移，流式路径单独阶段 |
| 子代理终态重复写入 | run archive 不可信 | 保留 ADR-022 terminal 单写者测试 |

---

## 7. 不做事项

- 不一次性重写 `AgentExecutionService`。
- 不引入外部工作流引擎。
- 不把 RuntimeActivity 替换成新存储。
- 不把子代理降级为普通工具。
- 不把会话层变成调度器。
- 不在本 ADR 中实现新的 UI。

---

## 8. 验收标准

ADR-024 完成后必须满足：

- `AgentExecutionService` 不再直接承载上下文合成、LLM 调用、工具调用、子代理调用的全部细节。
- 执行生命周期通过统一 recorder 写入 RuntimeActivity。
- 上下文合成结果可审计：层级、token estimate、记忆召回、压缩策略。
- LLM 调用结果结构化：provider/model/profile/usage/error。
- 工具调用结果结构化：args hash、duration、output length、permission denied。
- 子代理调用通过 `ISubAgentInvocationService` 进入执行引擎。
- 会话层只接收用户可见 frame，不处理 provider/tool/sub-agent 细节。
- 现有核心测试和 E2E smoke 通过。

