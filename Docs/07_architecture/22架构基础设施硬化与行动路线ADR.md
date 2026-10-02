# 22 架构基础设施硬化与行动路线 ADR

> 状态：**done**（ARCH-HARDEN-001~007 完成）
> 日期：2026-05-19
> Git: `3006480`
> 范围：子代理运行归档、事件系统、会话诊断、配置契约、测试基座、Admin 可观测性
> 前置：[19架构基础设施增强下一步ADR](19架构基础设施增强下一步ADR.md)、[20会话状态机与事件规范ADR](20会话状态机与事件规范ADR.md)、[21子代理工作空间与运行归档ADR](21子代理工作空间与运行归档ADR.md)

---

## 1. 背景

上一轮已经完成三个关键阶段：

| 阶段 | 结果 |
|------|------|
| QA 阻塞修复 | 修复 Agent 目录 ID、SchemaRegistry 重复 key、zombie event lease |
| ADR-019 继续 | 事件 schema、会话状态机、双写一致性、trace 聚合、会话重放 |
| ADR-021 子代理 | 子代理路径模型、FileSubAgentRunStore、DB 索引、Manager 接入、权限边界、诊断 API |

当前系统已经进入“基础设施骨架可运行”的阶段。下一步重点不是继续扩张新功能，而是把已经落地的骨架硬化，确保它们满足以下要求：

- 文件归档语义稳定。
- 事件、会话、子代理三条链路职责清晰。
- 契约可测试、可演进、可被 UI 消费。
- 后续 Admin 可观测性与 E2E 测试建立在可靠接口上。

---

## 2. 决策

### ADR-022-A：先硬化归档与契约，再扩展 UI

**决定**：下一阶段不优先做大规模 UI，而是先修复和固化子代理 run archive、事件 envelope、诊断 API、权限边界和测试基座。

理由：

- Admin UI 依赖稳定 API 和归档格式。
- E2E 依赖 deterministic Fake LLM、run archive、session replay。
- 如果归档和事件契约不稳定，UI 会反复返工。

### ADR-022-B：JSON 文件和 JSONL 文件必须使用不同序列化契约

**决定**：所有 `.json` 可使用缩进格式；所有 `.jsonl` 必须保证“一行一个完整 JSON object”。

契约：

```csharp
public static class PuddingJsonContracts
{
    public static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static readonly JsonSerializerOptions JsonLines = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
```

约束：

- `run.json`、`input.json`、`trace.json` 使用 `PrettyJson`。
- `events.jsonl`、`tools.jsonl`、`errors.jsonl`、`session.jsonl` 使用 `JsonLines`。
- JSONL reader 必须逐行读取，每行反序列化失败时返回诊断错误，不应让整个 API 500。

### ADR-022-C：子代理 run 只能有一个完成写入者

**决定**：子代理 run archive 的 terminal 状态只能由一个组件写入。

所有权：

| 行为 | Owner |
|------|-------|
| 创建 run | `SubAgentManager` for async；`AgentExecutionService` for sync |
| 写 started/context/tool event | `AgentExecutionService` |
| 写 completed/failed/cancelled terminal | `AgentExecutionService` |
| 写父会话 SSE 摘要 | `SubAgentManager` |
| 写 DB 查询索引 | `ISubAgentRunStore` |

终态幂等契约：

```csharp
public enum SubAgentRunTerminalWriteResult
{
    Applied,
    AlreadyTerminal,
    NotFound
}
```

```csharp
public interface ISubAgentRunStore
{
    Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct = default);
    Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct = default);
    Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct = default);
    Task<SubAgentRunTerminalWriteResult> CompleteRunAsync(string runId, SubAgentRunCompletion completion, CancellationToken ct = default);
    Task<SubAgentRunArchive?> GetRunArchiveAsync(string runId, CancellationToken ct = default);
}
```

规则：

- `completed`、`failed`、`cancelled` 是 terminal 状态。
- terminal 状态一旦写入，不允许被第二次 completion 覆盖。
- 第二次 completion 返回 `AlreadyTerminal` 并记录 warning。
- 非 terminal 字段可以追加事件，但不能改写 `run.json` 的 terminal 摘要。

### ADR-022-D：事件 schema 必须覆盖“内部事件”和“SSE 帧”两种命名空间

**决定**：`EventSchemaRegistry` 不再只作为内部队列 schema，还要明确区分 schema scope。

契约：

```csharp
public enum EventSchemaScope
{
    Internal,
    SessionFrame
}

public sealed record EventSchemaDefinition(
    string EventType,
    int CurrentVersion,
    EventSchemaScope Scope,
    string Category,
    string Description,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyList<string>? OptionalFields = null);
```

规则：

- 内部事件：`subagent.run.*`、`agent.*`、`cron.*`、`connector.*`、`llm_gateway/*`。
- 会话 SSE 帧：`delta`、`thinking`、`tool_call`、`subagent.spawned`、`done` 等。
- 同一 scope 内不允许重复 key。
- 不同 scope 可以保留相同显示事件名，但 registry key 必须包含 scope。

### ADR-022-E：诊断 API 返回稳定 DTO，不直接暴露 EF Entity

**决定**：所有诊断 API 输出专用 DTO，不直接返回 EF Entity 或匿名结构中的 Entity。

理由：

- EF Entity 会泄漏内部字段和迁移细节。
- 前端需要稳定字段。
- 后续脱敏、分页、错误展示需要统一格式。

契约示例：

```csharp
public sealed record SubAgentRunSummaryDto
{
    public required string RunId { get; init; }
    public required string ParentSessionId { get; init; }
    public required string SubSessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string TemplateId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long TotalDurationMs { get; init; }
    public int TotalRounds { get; init; }
    public int TotalToolCalls { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### ADR-022-F：权限边界必须从“配置存在”升级为“工具实际执行”

**决定**：`permissions.json` 不能只作为文档或模板字段，必须被文件工具、Shell 工具、子代理工具执行路径消费。

权限契约：

```csharp
public interface IAgentWorkspaceGuard
{
    WorkspaceGuardDecision CanRead(string agentInstanceId, string workspaceId, string path);
    WorkspaceGuardDecision CanWrite(string agentInstanceId, string workspaceId, string path);
    WorkspaceGuardDecision CanExecuteTool(string agentInstanceId, string workspaceId, string toolId);
}

public sealed record WorkspaceGuardDecision
{
    public bool Allowed { get; init; }
    public string? Reason { get; init; }
    public string? MatchedRule { get; init; }
}
```

规则：

- 默认 deny `data/config/**`、`data/databases/**`、`../**`。
- 子代理默认只能写自己的 `workspace/`。
- 父代理不能隐式授予子代理高权限工具。

### ADR-022-G：测试分层作为架构契约的一部分

**决定**：下一阶段测试按层次固定，不再只依赖零散 focused tests。

测试层级：

| 层级 | 目标 | 示例 |
|------|------|------|
| Unit | 模型、路径、schema、权限匹配 | `SubAgentRunModelsTests` |
| Integration | 文件归档 + DB 索引 + replay | `FileSubAgentRunStoreTests` |
| Web API | 诊断 API DTO、分页、404、脱敏 | `PuddingWebApiTests` |
| E2E | Docker + Fake LLM + 浏览器流程 | Playwright/Python |

---

## 3. 行动路线

### Phase 0：提交前硬化

目标：修复已知 P0 风险，确保当前里程碑可以作为后续基础。

必须完成：

1. JSONL 单行序列化。
2. 子代理 run terminal 单写入者。
3. `FileSubAgentRunStore` 往返测试。
4. 清理构建输出文件。

### Phase 1：契约收口

目标：把散落契约固化为可复用接口。

必须完成：

1. `PuddingJsonContracts`。
2. `EventSchemaScope`。
3. `SubAgentRunSummaryDto` / `SubAgentRunDetailDto`。
4. `IAgentWorkspaceGuard`。

### Phase 2：诊断 API 稳定化

目标：让 Admin UI 能依赖稳定 API。

必须完成：

1. 子代理 run list/detail/events/tools/output API 返回 DTO。
2. JSONL 读取支持错误行报告。
3. 事件诊断 API 支持 scope/category/status/trace 过滤。
4. 会话 trace-report 对 token usage 字段做兼容解析。

### Phase 3：权限执行接入

目标：权限配置从“可见”变成“生效”。

必须完成：

1. 文件工具读写接入 workspace guard。
2. Shell 工具执行接入 workspace guard。
3. 子代理工具选择接入 tool allow/deny。
4. 权限拒绝进入 RuntimeActivity 和 session event。

### Phase 4：Admin 可观测性 UI

目标：可视化展示执行链路。

必须完成：

1. 子代理 run 列表。
2. run detail：input/output/events/tools/errors。
3. 会话 trace timeline。
4. 事件队列诊断页面：pending/retrying/processing/dead_letter。

### Phase 5：E2E 基线

目标：用自动化替代人工冒烟。

必须完成：

1. 修复 WebApiTests 文件锁和输出目录问题。
2. Fake LLM 覆盖非流式、流式、工具调用、错误注入。
3. Docker 启动后自动跑健康检查。
4. 浏览器自动化覆盖：登录、建会话、发消息、子代理运行、查看诊断。

---

## 4. 验收标准

下一阶段完成后必须满足：

- `events.jsonl` / `tools.jsonl` 每行均可独立反序列化。
- 子代理 run terminal 状态不会被二次覆盖。
- 子代理 run API 不返回 EF Entity。
- 权限配置实际阻止越界文件写入。
- 事件 schema registry 支持 scope 且无重复 key。
- Admin UI 可以从 API 展示一次会话的主代理、子代理、LLM、工具、事件顺序。
- 一条命令能完成 Docker + Fake LLM + 核心聊天链路冒烟。

---

## 5. 不做事项

- 不引入外部 MQ 或独立服务。
- 不把 run archive 改为数据库主存储。
- 不在本阶段重写 AgentExecutionService 主循环。
- 不让 UI 直接读取本地文件系统。
- 不把权限失败静默吞掉。

