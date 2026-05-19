# SubAgent Workspace Run Archive Design

> 日期：2026-05-19
> ADR：[21子代理工作空间与运行归档ADR](../../07架构/21子代理工作空间与运行归档ADR.md)
> 状态：draft

## 1. 目标

下一阶段目标是让子代理成为可审计、可隔离、可重放的运行单元，而不是只作为父会话中的一段文本输出存在。

核心产物：

- 子代理 workspace 目录规范。
- 子代理 run archive 文件规范。
- 子代理配置文件化与权限边界。
- 子代理内部事件与会话 SSE 事件分层。
- 子代理运行查询、诊断、重放 API。

## 2. 当前基线

已经具备：

- `ISubAgentManager` / `SubAgentManager` 统一子代理创建、同步执行、取消、查询。
- `SessionStateManager` 能记录子代理 start/complete。
- `InternalEventBus` 能发布 `agent.sub_completed`。
- `RuntimeTraceContext` 支持 `SubAgentId` 和 child execution。
- `ADR-019` 已完成配置、事件、执行、会话的大部分骨架。

缺口：

- 没有 `subAgentRunId`。
- 没有 run archive。
- 子代理没有独立 workspace scope。
- 子代理配置仍未成为文件源。
- 内部事件 `subagent.*` 与会话 SSE `subagent.*` 命名冲突。
- Admin UI 无法查看子代理运行细节。

## 3. 目录设计

```text
data/workspaces/{workspaceId}/agents/{agentInstanceId}/
  manifest.json
  config/
    llm.json
    memory.json
    tools.json
    permissions.json
  workspace/
  runs/
    {runId}/
      run.json
      input.json
      output.md
      events.jsonl
      trace.json
      tools.jsonl
      files.json
      errors.jsonl
```

目录职责：

| 路径 | 职责 |
|------|------|
| `manifest.json` | 子代理实例长期身份 |
| `config/*.json` | 子代理结构化配置 |
| `workspace/` | 子代理可读写工作区 |
| `runs/{runId}` | 单次运行归档 |

## 4. run archive 文件格式

### `run.json`

```json
{
  "runId": "run_20260519_abcdef12",
  "parentSessionId": "ses_parent",
  "subSessionId": "ses_parent-sub-12345678",
  "workspaceId": "default",
  "agentInstanceId": "default.researcher-001",
  "templateId": "researcher",
  "task": "总结最近的会话问题",
  "status": "completed",
  "startedAt": "2026-05-19T00:00:00Z",
  "completedAt": "2026-05-19T00:00:12Z",
  "llmProfiles": {
    "conscious": "default-conscious",
    "subconscious": "default-subconscious"
  },
  "trace": {
    "traceId": "trace_...",
    "correlationId": "corr_...",
    "parentExecutionId": "exec_parent",
    "executionId": "exec_sub"
  }
}
```

### `input.json`

```json
{
  "task": "总结最近的会话问题",
  "parentSessionId": "ses_parent",
  "workspaceId": "default",
  "capabilityPolicy": {},
  "maxRounds": 20,
  "requestedModel": null
}
```

### `events.jsonl`

每行一个内部事件摘要：

```json
{"eventId":"evt_1","eventType":"subagent.run.started","timestamp":"2026-05-19T00:00:00Z","payload":{}}
```

### `tools.jsonl`

每行一个工具调用审计：

```json
{"toolCallId":"call_1","toolName":"memory.search","argsHash":"sha256:...","success":true,"durationMs":34,"outputLength":512}
```

### `output.md`

子代理最终输出。只在成功或有部分输出时写入。

### `errors.jsonl`

失败事件、异常摘要和 stack trace hash。

## 5. 核心接口

建议新增到 `Source/PuddingCore/SubAgents`：

```csharp
public interface ISubAgentRunStore
{
    Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct);
    Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct);
    Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct);
    Task CompleteRunAsync(string runId, SubAgentRunCompletion completion, CancellationToken ct);
    Task<SubAgentRunArchive?> GetRunAsync(string runId, CancellationToken ct);
}
```

```csharp
public sealed record SubAgentRunCreateRequest
{
    public required string ParentSessionId { get; init; }
    public required string SubSessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string TemplateId { get; init; }
    public required string Task { get; init; }
    public required RuntimeTraceContext Trace { get; init; }
}
```

## 6. 数据库索引

新增 `SubAgentRunEntity`：

```text
sub_agent_runs
  id
  run_id unique
  parent_session_id index
  sub_session_id index
  workspace_id index
  agent_instance_id index
  template_id
  status index
  started_at
  completed_at
  archive_path
  trace_id index
  correlation_id index
  error_message
```

数据库仅保存索引与摘要，不保存完整 events/tools/output。

## 7. API 设计

```text
GET /api/sub-agents/runs
  query: parentSessionId, workspaceId, agentInstanceId, status, limit, offset

GET /api/sub-agents/runs/{runId}
  returns: run.json + DB summary

GET /api/sub-agents/runs/{runId}/events
  returns: paged events.jsonl

GET /api/sub-agents/runs/{runId}/tools
  returns: paged tools.jsonl

GET /api/sub-agents/runs/{runId}/output
  returns: output.md

POST /api/sub-agents/runs/{runId}/replay
  mode: diagnostic | rerun
```

## 8. 事件命名修正

内部事件使用：

```text
subagent.run.created
subagent.run.started
subagent.run.context_assembled
subagent.run.llm_started
subagent.run.tool_started
subagent.run.tool_completed
subagent.run.completed
subagent.run.failed
subagent.run.cancelled
subagent.run.archived
```

会话 SSE 继续使用：

```text
subagent.spawned
subagent.delta
subagent.thinking
subagent.tool_call
subagent.tool_result
subagent.completed
```

`EventSchemaRegistry` 不允许重复 key。重复事件类型应在测试中失败。

## 9. 实施计划摘要

### Task 1：修复提交前阻塞问题

- 默认 agent instance 目录 ID 与 manifest ID 统一。
- `EventSchemaRegistry` 重复 key 检测。
- zombie event 回收后重新 lease。

### Task 2：路径与模型

- `PuddingDataPaths` 增加子代理 workspace/run 路径 helper。
- 新增 `SubAgentRunManifest`、`SubAgentRunCreateRequest`、`SubAgentRunCompletion`。
- 增加路径和 manifest 序列化测试。

### Task 3：Run Store

- 新增 `FileSubAgentRunStore`。
- 创建 run 目录并写入 `run.json`、`input.json`。
- 支持 append events/tools/errors。
- 支持 complete 更新和 output 写入。

### Task 4：数据库索引

- 新增 `SubAgentRunEntity`。
- `PlatformDbContext` 增加 DbSet 和非破坏性 DDL。
- Run Store 写文件后同步 DB 索引。

### Task 5：SubAgentManager 接入

- `SpawnAsync` 和 `ExecuteSyncAsync` 创建 run。
- 完成/失败时归档结果。
- 内部事件改为 `subagent.run.*`。
- 父会话 SSE 只保留摘要。

### Task 6：权限边界

- 增加 `permissions.json` 模型。
- 文件工具/shell 工具应用 scoped workspace。
- 默认禁止写 `data/config`、`data/databases`。

### Task 7：诊断 API

- 增加 run list/detail/events/tools/output API。
- 支持 parentSessionId 查询所有子代理 run。

### Task 8：Admin UI

- 增加子代理运行列表。
- 增加 run detail。
- 展示 input/output/events/tools/trace。

## 10. 验收

- `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo` 通过。
- 子代理同步和异步执行都会生成 run archive。
- run archive 可以通过 API 查询。
- 父会话 replay 显示子代理摘要，run detail 显示完整细节。
- 子代理无法写入系统配置目录。
- 事件 registry 无重复 key。

