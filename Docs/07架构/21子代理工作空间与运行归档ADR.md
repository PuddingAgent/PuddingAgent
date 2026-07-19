# 21 子代理工作空间与运行归档 ADR

> 状态：**done**（Phase A-D 完成，Phase E Admin UI 待后续 UI 阶段）
> 日期：2026-05-19
> Git: `c79d983`
> 范围：子代理系统、workspace 隔离、Agent 文件配置、运行归档、可观测性、重放
> 前置：[19架构基础设施增强下一步ADR](19架构基础设施增强下一步ADR.md)、[20会话状态机与事件规范ADR](20会话状态机与事件规范ADR.md)
> 审阅：[QA-2026-05-19-Architecture-Foundation-Review](../QA/QA-2026-05-19-Architecture-Foundation-Review.md)
>
> **2026-07-19 演进**：运行归档仍是子代理详细审计事实源；Chat 实时状态不再消费一套
> 独立易失 SSE 词汇和轮询表，而由 run archive 事件可靠投影到 canonical Conversation
> Event Store。稳定身份、轮次/LLM/工具事件、终态仲裁和前端投影见
> [ADR-060](61ADR-060子代理运行可观测性与会话事件投影ADR.md)。

---

## 1. 背景

`ADR-019` 已经推进了配置目录、事件系统、执行引擎和会话层。下一步应该进入子代理系统，因为它是多 Agent 平台能力的关键验证点：

- 子代理目前主要由 `SubAgentManager`、`SubAgentTool`、`SessionStateManager`、`AgentExecutionService` 协作完成。
- 子代理状态已经可以进入会话事件和 RuntimeActivity。
- 但子代理还没有独立 workspace、文件化配置、run 归档、可重放输入输出，也没有稳定的运行目录边界。
- 如果继续只在数据库里记录子代理状态，用户仍然看不清子代理“是谁、用了什么配置、在哪个目录工作、生成了什么结果”。

本 ADR 决定子代理从“会话中的一次工具调用”升级为“可审计的轻量 Agent 实例运行”。

---

## 2. 决策

### ADR-021-A：子代理必须拥有独立运行身份

**决定**：每次子代理执行生成一个 `subAgentRunId`，并且区分三个 ID：

| ID | 含义 | 示例 |
|----|------|------|
| `agentInstanceId` | Agent 实例身份，长期存在 | `default.researcher-001` |
| `subSessionId` | 会话流身份，用于 SSE 与父会话聚合 | `ses_x-sub-a1b2c3d4` |
| `subAgentRunId` | 单次运行身份，用于文件归档和重放 | `run_20260519_abcdef12` |

**后果**：

- 不能再只用 `subSessionId` 代表所有子代理概念。
- 后续所有子代理事件必须携带 `subAgentRunId`。
- 父会话只负责展示聚合状态，不负责保存全部子代理运行细节。
- 池化 `create` 只分配稳定 `subSessionId`，不得通过 `SpawnAsync` 创建一个隐藏 run。
- 同一池化会话每次 `execute` 都生成新的 `subAgentRunId`，禁止复用 runId。

### ADR-021-B：子代理 workspace 与 run archive 分离

**决定**：子代理有两个目录层次：

1. `workspace/`：子代理可以读写的工作目录。
2. `runs/{runId}/`：一次运行的不可变归档目录。

标准目录：

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
    {subAgentRunId}/
      run.json
      input.json
      output.md
      events.jsonl
      trace.json
      tools.jsonl
      files.json
      errors.jsonl
```

**后果**：

- 子代理可继续复用 workspace 状态，但每次运行都有独立审计归档。
- run archive 默认只追加，不覆盖。
- 归档目录可以被 Admin UI、E2E、QA 直接读取。

### ADR-021-C：子代理配置文件优先，数据库为索引

**决定**：子代理配置以文件为源，数据库只保存查询索引和 UI 摘要。

配置解析顺序：

1. `data/workspaces/{workspaceId}/agents/{agentInstanceId}/config/*.json`
2. `data/agents/{agentInstanceId}/config/*.json`
3. `data/agent-templates/{templateId}/manifest.json`
4. `data/config/llm.providers.json.roles`

**后果**：

- 用户能直接检查每个子代理的 LLM、工具权限、记忆策略。
- 管理后台编辑配置时，本质上是在写文件，然后同步索引。
- 不能只在 `SessionSubAgentEntity` 中保存模型 ID 作为事实来源。

### ADR-021-D：子代理事件必须进入统一 envelope

**决定**：子代理生命周期事件使用内部事件系统，SSE 会话帧只用于用户界面显示。

内部事件：

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

会话 SSE 帧：

```text
subagent.spawned
subagent.delta
subagent.thinking
subagent.tool_call
subagent.tool_result
subagent.completed
```

**后果**：

- 内部事件和会话帧不再争用同一个 schema 定义。
- 事件诊断 UI 看内部事件。
- Chat UI 看会话帧。

### ADR-021-E：子代理运行归档必须可重放

**决定**：每个 run archive 必须包含重放所需的最小输入。

最低归档内容：

```json
{
  "runId": "run_...",
  "parentSessionId": "ses_...",
  "subSessionId": "ses_...-sub-...",
  "workspaceId": "default",
  "agentInstanceId": "default.researcher-001",
  "templateId": "researcher",
  "task": "...",
  "llmProfiles": {
    "conscious": "default-conscious",
    "subconscious": "default-subconscious"
  },
  "startedAt": "2026-05-19T00:00:00Z",
  "completedAt": null,
  "status": "running"
}
```

重放分两级：

- **诊断重放**：读取 `events.jsonl`、`tools.jsonl`、`output.md`，还原发生了什么。
- **执行重跑**：用 `input.json` 和当前配置重新执行，生成新的 runId，不覆盖旧 run。

### ADR-021-F：子代理权限必须显式化

**决定**：子代理工具和文件权限写入 `permissions.json`，不从父代理隐式继承全部权限。

示例：

```json
{
  "filesystem": {
    "read": ["workspace/**", "shared/context/**"],
    "write": ["workspace/**"],
    "deny": ["../**", "data/config/**", "data/databases/**"]
  },
  "tools": {
    "allow": ["search_memory", "file_read", "file_write"],
    "deny": ["shell"]
  },
  "network": {
    "allow": false
  }
}
```

**后果**：

- 子代理不能默认写系统配置。
- 文件工具需要接入 scoped workspace。
- Admin UI 必须能展示权限摘要。

---

## 3. 设计方案

### 3.1 核心类型

新增核心模型建议放在 `PuddingCore/SubAgents`：

```csharp
public sealed record SubAgentRunManifest
{
    public required string RunId { get; init; }
    public required string ParentSessionId { get; init; }
    public required string SubSessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string TemplateId { get; init; }
    public required string Task { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public Dictionary<string, string> LlmProfiles { get; init; } = new();
    public Dictionary<string, string> Trace { get; init; } = new();
}
```

```csharp
public interface ISubAgentRunStore
{
    Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct);
    Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct);
    Task AppendToolAuditAsync(string runId, object payload, CancellationToken ct);
    Task CompleteRunAsync(string runId, SubAgentRunCompletion completion, CancellationToken ct);
    Task<SubAgentRunArchive?> GetRunAsync(string runId, CancellationToken ct);
}
```

### 3.2 数据写入顺序

池化子代理预留：

```text
SubAgentTool(pool_action=create)
  -> SubAgentPool.CreateAsync
  -> SubAgentSessionId.Create
  -> 仅写入进程内 Idle 池条目
  -> 不创建 run / 不派发 Runtime / 不写 session_sub_agents
```

每次子代理执行：

```text
SubAgentTool / SmartWorkflowTool
  -> SubAgentManager.SpawnAsync 或 ExecuteSyncAsync
  -> SubAgentRunStore.CreateRunAsync（每次生成新 runId）
  -> SessionStateManager.TrackSubAgentStartAsync
       -> session_sub_agents 原子 UPSERT 当前状态
       -> 首次 INSERT；复用时仅允许同 parentSessionId 并重置终态字段
  -> InternalEventBus.Publish(subagent.run.created)
  -> AgentExecutionService.ExecuteAsync(child request)
```

若当前状态投影失败，Manager 必须把刚创建的 run 终结为 `failed/cancelled` 后抛出，
不得留下永久 `running` 的孤儿归档；也不得吞掉数据库异常继续执行。

子代理完成：

```text
AgentExecutionService result
  -> SubAgentRunStore.CompleteRunAsync
  -> SessionStateManager.TrackSubAgentCompleteAsync
  -> InternalEventBus.Publish(subagent.run.completed/failed)
  -> parent session SSE subagent.completed
```

### 3.3 文件写入规则

| 文件 | 写入时机 | 可变性 |
|------|----------|--------|
| `run.json` | create + complete 更新 | 可更新状态字段 |
| `input.json` | create | 不覆盖 |
| `events.jsonl` | 执行中追加 | append-only |
| `tools.jsonl` | 工具调用时追加 | append-only |
| `output.md` | complete | 不覆盖，失败可为空 |
| `trace.json` | complete | 不覆盖 |
| `files.json` | complete | 不覆盖 |
| `errors.jsonl` | 失败时追加 | append-only |

### 3.4 数据库索引

建议新增或扩展表：

```text
sub_agent_runs
  run_id
  parent_session_id
  sub_session_id
  workspace_id
  agent_instance_id
  template_id
  status
  started_at
  completed_at
  archive_path
  trace_id
  correlation_id
```

数据库只用于查询和列表，不保存完整归档内容。

### 3.5 API

新增 API：

```text
GET /api/sub-agents/runs?parentSessionId=&workspaceId=&status=&limit=
GET /api/sub-agents/runs/{runId}
GET /api/sub-agents/runs/{runId}/events
GET /api/sub-agents/runs/{runId}/tools
GET /api/sub-agents/runs/{runId}/files
POST /api/sub-agents/runs/{runId}/replay
```

### 3.6 Admin UI

新增“子代理运行”诊断视图：

- 运行列表：状态、模板、模型/profile、耗时、父会话。
- 调用树：父会话 -> 子代理 run -> LLM/tool/memory。
- 归档文件：input、output、events、tools、trace。
- 重放按钮：诊断重放和重新执行分开。

---

## 4. 实施顺序

### Phase A：模型与路径

- 增加 `PuddingDataPaths.SubAgentRunRoot(...)`。
- 增加 `SubAgentRunManifest`、`SubAgentRunStore`。
- 增加路径与 manifest 序列化测试。

### Phase B：SubAgentManager 接入 run store

- `SpawnAsync` 创建 run archive。
- `ExecuteSyncAsync` 也必须创建 run archive。
- 完成/失败时写入 output、trace、errors。
- 所有 run 记录写入数据库索引。

### Phase C：事件与会话分层

- 内部事件改用 `subagent.run.*`。
- 会话 SSE 保留 `subagent.*`。
- 修复 `EventSchemaRegistry` 重复 key。

### Phase D：权限与 scoped workspace

- 增加 `permissions.json`。
- 文件工具和 shell 工具接入子代理 workspace scope。
- 默认模板禁止子代理写 `data/config`、`data/databases`。

### Phase E：诊断 API 与 UI

- 增加 run 查询 API。
- 增加 Admin 子代理运行页面。
- 从 run archive 展示事件、工具、输出和错误。

---

## 5. 验收标准

- 每次异步/同步子代理执行都生成一个 run archive。
- run archive 包含 `run.json`、`input.json`、`events.jsonl`，完成时包含 `output.md` 或 `errors.jsonl`。
- 父会话 replay 能看到子代理摘要，子代理 run API 能看到完整细节。
- 子代理内部事件没有与 session SSE 事件 schema 冲突。
- 子代理默认无法写入 `data/config` 和 `data/databases`。
- 诊断 UI 可以按 parent session 找到所有子代理运行。

---

## 6. 风险与约束

- 不在本阶段重写 AgentExecutionService 主循环；只增加 run store 和归档边界。
- 不把所有子代理输出塞回父会话 JSONL；父会话只保存摘要和链接。
- 不把 run archive 作为事务强一致主存储；数据库索引用于查询，归档用于审计。
- 如果归档写入失败，子代理执行可以失败并进入 `subagent.run.failed`，不能静默吞掉。

