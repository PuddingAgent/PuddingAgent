# Pudding 工具系统增强代码级设计方案

日期: 2026-06-24

参考:

- CodeWhale `docs/TOOL_SURFACE.md`: https://github.com/Hmbown/CodeWhale/blob/main/docs/TOOL_SURFACE.md
- Pudding 当前工具契约: `Source/PuddingCore/Tools/PuddingToolContracts.cs`
- Pudding 当前工具注册与执行: `Source/PuddingRuntime/Tools/Platform/PuddingToolRegistry.cs`, `ToolInvocationService.cs`, `PuddingToolServiceCollectionExtensions.cs`
- Pudding 当前内置工具: `Source/PuddingRuntime/Tools/BuiltIns/*`

## 1. 设计目标

本方案不是照搬 CodeWhale 的工具表面，而是在 Pudding 现有 `IPuddingTool`、`ToolInvocationService`、`AgentFirewall`、`CapabilityPolicy`、SSE timeline、Diagnostics、Memory、SubAgent、CodeIntelligence 等基础上补齐关键短板。

目标:

1. 保持 Pudding 现有自动发现、DI 注册、权限策略、审计链路不被绕过。
2. 解决当前工具系统最影响上下文和长任务能力的缺口:
   - Shell 前台/后台/等待/交互/取消生命周期不统一。
   - 大工具输出直接进入上下文，缺少 spill 和按需取回。
   - `file_patch` 缺少标准 unified diff 输入模式。
   - `manage_tasks` 还是内存 todo，不是 durable work surface。
   - 工具可见性只在调用时拒绝，未在模型可见目录中过滤。
   - 跨会话接力依赖自动摘要/记忆，缺少主动 handoff artifact。
3. 避免“两个等价工具同时可见”造成模型摇摆和 prefix cache 命中率下降。
4. 所有新增能力都有可观测证据: timeline、telemetry metric、artifact、diagnostic API。

非目标:

- 不替换 Pudding 当前多 Agent、记忆、代码智能和审批系统。
- 不把 shell 变成唯一工具；专用工具仍优先服务结构化输出。
- 不一次性实现 GitHub、Automation、RLM 等低优先级能力。
- 不在第一阶段引入不可回滚的自动行为。

## 2. 当前 Pudding 代码事实

### 2.1 Tool 契约和注册

当前新工具统一实现 `IPuddingTool`，推荐基类是:

```csharp
public abstract class PuddingToolBase<TArgs> : IPuddingTool
    where TArgs : class
{
    public ToolDescriptor Descriptor { get; }
    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default);
    protected abstract Task<ToolExecutionResult> ExecuteCoreAsync(TArgs args, ToolExecutionContext context, CancellationToken ct);
}
```

工具元数据来自 `[Tool]`:

```csharp
[Tool(
    id: "file_read",
    name: "Read file",
    description: "...",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low)]
```

注册链路:

- `RuntimeServiceExtensions.AddPuddingToolsFromAssembly(...)` 自动发现带 `[Tool]` 的 `IPuddingTool`。
- `AddPuddingToolRegistry(...)` 注册 `IPuddingToolRegistry`、`PuddingToolSchemaService`、`IPuddingToolExecutionService`。
- `PuddingToolSchemaService.BuildLlmTools(policy)` 从 `registry.ListAvailable(policy)` 生成 LLM tool schema。

设计约束:

- 新能力应优先以原生 `IPuddingTool` 增加。
- 旧 `IAgentSkill` 只作为兼容 adapter，不应再扩散。
- 新的可见性、spill、审计都应接入 registry/schema/execution 这三层，而不是绕开工具系统。

### 2.2 权限与执行

当前执行路径:

```text
AgentExecutionService
  -> ToolInvocationService.InvokeAsync
  -> IPuddingToolExecutionService.ExecuteAsync
  -> AgentFirewall.EvaluateAsync
  -> IPuddingTool.ExecuteAsync
  -> ToolExecutionResult
```

关键现状:

- `ToolInvocationService` 负责运行时 fuse、workspace guard、耗时和 `ToolInvocationResult`。
- `PuddingToolExecutionService` 负责 registry 查找、YOLO context、`AgentFirewall`、telemetry metric。
- `ToolExecutionResult` 只有 `Success/Output/Error/ExitCode`，没有 artifact、handle、spill 元数据。
- `ToolInvocationResult` 有 `OutputLength`，但输出仍可能完整进入上下文。

### 2.3 Shell 现状

当前有两个相关工具:

1. `shell` (`HostShellTool`)
   - 前台执行。
   - 超时由 `HostShellExecutor.ExecuteAsync` 控制。
   - 没有后台任务 ID、wait、stdin、cancel。
   - 走 `OperationZoneClassifier` 和 `AuditLogger`。

2. `terminal_execute` (`TerminalSkill`)
   - 启动独立进程并立即返回 PID。
   - 输出通过 SSE terminal 事件推送。
   - 缺少模型可调用的 wait/interact/cancel/read-result 原语。

结论:

- `shell` 和 `terminal_execute` 目前语义重叠但不等价。
- P0 应统一生命周期，而不是继续增加第三个互相竞争的 shell 工具。

### 2.4 文件与 patch 现状

当前已有:

- `file_read`: 支持 `max_chars`、`head_lines`、`tail_lines`、`offset_lines`、`limit_lines`。
- `file_write`: create/overwrite/append。
- `file_patch`: 现有能力需要继续审阅，但当前设计目标应是补齐标准 `unified_diff` 输入和 multi-hunk 验证。
- `file_search`、`search_grep`、CodeIntelligence 工具。

结论:

- Pudding 已有专用工具优先的基础。
- 需要通过 `ToolLoopInstructionBuilder` 明确引导: 多块编辑优先 `file_patch mode=unified_diff`，不要用 `file_write` 全量重写。

### 2.5 Task 现状

`TaskManagerTool` 现状:

- 工具名: `manage_tasks`
- 内存 `List<TaskItem>`。
- 支持 `create/update_status/list/delete`。
- `TaskItem` 只有 `Id/Title/Status/CreatedAt`。

结论:

- 它是 todo，不是 durable task。
- CodeWhale 的 task/checklist/gate/artifact 应迁移为 Pudding 的 durable work surface，但需要保留 `manage_tasks` 兼容。

## 3. CodeWhale 可迁移原则

从 `TOOL_SURFACE.md` 迁移以下原则，而不是直接迁移所有工具名:

1. 专用工具必须明显优于 shell，优先提供结构化输出。
2. 不暴露两个等价工具；兼容旧名可以隐藏注册，但不要进入模型可见目录。
3. Shell 工具只在 session/profile 允许时进入模型可见目录。
4. 大输出使用 spill/handle，通过 `retrieve_tool_result` 或 `handle_read` 按需读取。
5. Durable task 是真实工作对象，checklist 是其下属进度，gate/artifact 是证据。
6. Sub-agent 返回 compact receipt + transcript handle，父会话不应吞入完整子代理 transcript。
7. `/relay` 是自动 compact 的主动补充，用 artifact 保留目标、决策、验证状态和下一步。
8. release smoke 应验证模型可见 registry 名称，而不是 grep handler 名称。

## 4. 分阶段规划

| 阶段 | 主题 | 优先级 | 目标 |
| --- | --- | --- | --- |
| P0 | Shell 生命周期 + 大输出 spill/handle | 必做 | 解锁长任务和上下文保护 |
| P1 | Unified diff patch + durable task | 高 | 降低编辑 token，建立可验证工作面 |
| P2 | 工具可见性过滤 + session relay | 中 | 降低模型困惑，增强跨会话连续性 |
| P3 | RLM、GitHub、Automation、MCP 管理 | 按需 | 在 P0-P2 稳定后逐步引入 |

## 5. P0-1 Shell 生命周期统一

### 5.1 目标

把 `shell` 从单次前台执行扩展为一组生命周期动作，同时逐步收敛 `terminal_execute` 的模型可见性。

推荐模型可见工具名:

- 保留 `shell` 作为 Pudding 主 shell 工具。
- 在 `shell` 参数中增加 `action`:
  - `run`: 前台 bounded command，默认。
  - `start`: 后台启动，返回 `shell_task_id`。
  - `wait`: 轮询后台任务输出。
  - `interact`: 向后台任务 stdin 写入。
  - `cancel`: 取消一个或全部后台任务。
  - `list`: 列出当前 session 的后台 shell 任务。

不建议同时暴露 `task_shell_start`、`exec_shell_wait` 等 CodeWhale 名称。若未来需要兼容 replay，可做 hidden alias。

### 5.2 参数模型

修改 `HostShellToolArgs`:

```csharp
public sealed record HostShellToolArgs
{
    [ToolParam("Action: run, start, wait, interact, cancel, or list. Default: run.")]
    public string? Action { get; init; }

    [ToolParam("Command to execute. Required for run/start.")]
    public string? Command { get; init; }

    [ToolParam("Background shell task id. Required for wait/interact/cancel unless cancel_all=true.")]
    public string? ShellTaskId { get; init; }

    [ToolParam("Input line or text to send to stdin for interact.")]
    public string? Stdin { get; init; }

    [ToolParam("Cancel all running shell tasks in this session.")]
    public bool? CancelAll { get; init; }

    [ToolParam("Shell mode: auto, wsl, bash, cmd, or powershell. Default: auto.")]
    public string? Shell { get; init; }

    [ToolParam("Host working directory.")]
    public string? WorkingDirectory { get; init; }

    [ToolParam("Timeout in seconds for run/wait.")]
    public int? TimeoutSeconds { get; init; }

    [ToolParam("Reason. Required for agent-private directory operations.")]
    public string? Reason { get; init; }
}
```

兼容要求:

- 旧调用只传 `command` 时等价于 `action=run`。
- `Action` 使用 `run/start/wait/interact/cancel/list`，避免同时引入 `sub_action` 和 CodeWhale 风格多个工具名。

### 5.3 新服务: ShellTaskManager

新文件:

```text
Source/PuddingRuntime/Tools/BuiltIns/Shell/ShellTaskManager.cs
```

职责:

- 按 session 管理后台 process。
- 异步收集 stdout/stderr ring buffer。
- 支持 incremental cursor。
- 支持 stdin。
- 支持 cancel entire process tree。
- 记录 stale 状态，不在进程重启后假装任务仍活着。

接口:

```csharp
public interface IShellTaskManager
{
    Task<ShellTaskStartResult> StartAsync(ShellTaskStartRequest request, CancellationToken ct);
    Task<ShellTaskPollResult> WaitAsync(ShellTaskWaitRequest request, CancellationToken ct);
    Task<ShellTaskInteractResult> InteractAsync(ShellTaskInteractRequest request, CancellationToken ct);
    Task<ShellTaskCancelResult> CancelAsync(ShellTaskCancelRequest request, CancellationToken ct);
    IReadOnlyList<ShellTaskCard> List(string sessionId);
}
```

核心模型:

```csharp
public sealed record ShellTaskStartRequest(
    string WorkspaceId,
    string SessionId,
    string AgentInstanceId,
    string Command,
    string? Shell,
    string? WorkingDirectory,
    RuntimeTraceContext? Trace);

public sealed record ShellTaskCard
{
    public required string ShellTaskId { get; init; }
    public required string SessionId { get; init; }
    public required string Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public int? ProcessId { get; init; }
    public required string Status { get; init; } // running/completed/failed/cancelled/stale
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int? ExitCode { get; init; }
    public int OutputChars { get; init; }
    public int ErrorChars { get; init; }
}
```

存储:

- 第一阶段使用 singleton 内存 store。
- 每个任务输出使用 bounded in-memory buffer + 可选 spill 文件。
- 不跨进程恢复 live process；重启后如果保留 metadata，标记 `stale`。

DI:

```csharp
services.TryAddSingleton<IShellTaskManager, ShellTaskManager>();
```

### 5.4 HostShellTool 接入

`HostShellTool.ExecuteCoreAsync` 改为 dispatch:

```csharp
var action = (args.Action ?? "run").Trim().ToLowerInvariant();
return action switch
{
    "run" => await RunForegroundAsync(...),
    "start" => await StartBackgroundAsync(...),
    "wait" => await WaitBackgroundAsync(...),
    "interact" => await InteractAsync(...),
    "cancel" => await CancelAsync(...),
    "list" => ListTasks(...),
    _ => ToolExecutionResult.Fail(...)
};
```

权限:

- `run/start` 继续走 `OperationZoneClassifier.ClassifyShellCommand` 和 reason 要求。
- `wait/list` 低风险，但仍必须验证 task 属于当前 `sessionId`。
- `interact/cancel` 需要 `RequiresShell`，但不重新审批命令本身；审计记录 action 和 task id。

超时策略:

- `run` 超时后不保留进程，返回明确提示:

```text
Command timed out. For long-running work, call shell with action=start, then poll with action=wait.
```

- `wait` 超时只停止等待，不杀后台任务。

### 5.5 terminal_execute 收敛

`terminal_execute` 当前仍可保留供 UI/SSE terminal 面板使用，但模型可见目录中应逐步隐藏，避免与 `shell action=start` 竞争。

建议:

- P0 不删除 `terminal_execute`。
- P2 工具可见性过滤完成后，默认不暴露 `terminal_execute` 给模型。
- 若历史 transcript 精确调用 `terminal_execute`，仍允许执行并返回 `_deprecation.use_instead = shell(action=start)`。

## 6. P0-2 大输出 spill 与 handle_read

### 6.1 目标

避免大工具输出直接进入父会话上下文。所有工具统一经过 spill 层:

```text
ToolExecutionResult.Output
  -> ToolInvocationService
  -> ToolOutputSpillStore.MaybeSpill
  -> small reference in transcript
  -> retrieve_tool_result / handle_read for slices
```

### 6.2 扩展结果模型

修改 `ToolExecutionResult`:

```csharp
public sealed record ToolExecutionResult
{
    public required bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }
    public int ExitCode { get; init; }

    public ToolOutputReference? OutputReference { get; init; }
    public IReadOnlyList<ToolArtifactReference> Artifacts { get; init; } = [];
}

public sealed record ToolOutputReference
{
    public required string HandleId { get; init; }
    public required string Kind { get; init; } // spilled_text / var_handle / json
    public required string ToolId { get; init; }
    public required int TotalChars { get; init; }
    public int? TotalLines { get; init; }
    public required string Summary { get; init; }
}
```

修改 `ToolInvocationResult`:

```csharp
public ToolOutputReference? OutputReference { get; init; }
public IReadOnlyList<ToolArtifactReference> Artifacts { get; init; } = [];
```

### 6.3 ToolOutputSpillStore

新文件:

```text
Source/PuddingRuntime/Tools/Platform/ToolOutputSpillStore.cs
```

接口:

```csharp
public interface IToolOutputSpillStore
{
    ToolOutputSpillDecision MaybeSpill(ToolOutputSpillRequest request);
    Task<ToolOutputSlice> ReadAsync(ToolOutputReadRequest request, CancellationToken ct);
}
```

模型:

```csharp
public sealed record ToolOutputSpillRequest(
    string WorkspaceId,
    string SessionId,
    string AgentInstanceId,
    string ToolId,
    string ToolCallId,
    string Output,
    RuntimeTraceContext? Trace);

public sealed record ToolOutputReadRequest(
    string WorkspaceId,
    string SessionId,
    string HandleId,
    string Mode,       // summary/head/tail/lines/query
    string? Argument,
    int? MaxChars);
```

存储路径:

```text
D:\data\tool-outputs\{yyyyMMdd}\{workspaceId}\{sessionId}\{handleId}.txt
D:\data\tool-outputs\{yyyyMMdd}\{workspaceId}\{sessionId}\{handleId}.meta.json
```

阈值:

```csharp
public sealed class ToolOutputSpillOptions
{
    public int SpillThresholdChars { get; init; } = 12000;
    public int InlinePreviewChars { get; init; } = 1600;
    public int DefaultReadMaxChars { get; init; } = 8000;
}
```

### 6.4 接入点

优先接入 `ToolInvocationService`，因为它知道 `ToolCallId/SessionId/ToolName/OutputLength`，并且是 Agent loop 的统一 facade。

伪代码:

```csharp
var result = await _toolExecutionService.ExecuteAsync(...);
var spill = _spillStore.MaybeSpill(new ToolOutputSpillRequest(...));

if (spill.IsSpilled)
{
    return new ToolInvocationResult
    {
        Success = result.Success,
        Output = spill.ModelVisibleText,
        OutputReference = spill.Reference,
        OutputLength = result.Output.Length,
        ...
    };
}
```

模型可见文本示例:

```text
[tool_output_spilled]
tool=file_read
handle=toolout_20260624_abcd1234
total=48321 chars, 1280 lines
summary=Large output was stored. Use retrieve_tool_result with mode=head/tail/lines/query.
preview:
...
```

### 6.5 新工具: retrieve_tool_result

新文件:

```text
Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/RetrieveToolResultTool.cs
```

工具定义:

```csharp
[Tool(
    id: "retrieve_tool_result",
    name: "Retrieve spilled tool output",
    description: "Read summary or bounded slices from large prior tool outputs by handle.",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly)]
public sealed class RetrieveToolResultTool : PuddingToolBase<RetrieveToolResultArgs>
```

参数:

```csharp
public sealed record RetrieveToolResultArgs
{
    public required string HandleId { get; init; }
    public string? Mode { get; init; } // summary/head/tail/lines/query
    public string? Argument { get; init; } // "1-120" for lines, regex/query string
    public int? MaxChars { get; init; }
}
```

权限:

- 只能读取同 workspace/session 可见的 handle。
- 如果未来允许跨 session，需要通过 `query_session_logs` 或专门权限。

### 6.6 新工具: handle_read

`retrieve_tool_result` 只处理文本 spill。`handle_read` 是统一 var handle 投影工具，给 RLM、sub-agent transcript、JSON payload 使用。

第一阶段先实现文本和 JSON 两类:

```csharp
[Tool(id: "handle_read", ...)]
public sealed class HandleReadTool : PuddingToolBase<HandleReadArgs>
```

参数:

```csharp
public sealed record HandleReadArgs
{
    public required string HandleId { get; init; }
    public string? Projection { get; init; } // text/json
    public string? Mode { get; init; }       // summary/head/tail/lines/query/jsonpath/count
    public string? Argument { get; init; }
    public int? MaxChars { get; init; }
}
```

设计原则:

- `retrieve_tool_result` 是模型易用的 spilled output 专用入口。
- `handle_read` 是更通用的 handle 投影入口。
- 两者底层共用 `IHandleStore`，但工具描述中明确使用场景，避免模型混淆。

## 7. P1-1 file_patch unified diff

### 7.1 目标

让 Pudding 的 `file_patch` 支持标准 unified diff，覆盖多文件、多 hunk、小范围编辑。减少 `file_write` 全量重写。

### 7.2 参数扩展

在 `FilePatchArgs` 增加:

```csharp
public string? Mode { get; init; } // existing operations or "unified_diff"
public string? DiffText { get; init; }
public bool? DryRun { get; init; }
```

### 7.3 UnifiedDiffParser

新文件:

```text
Source/PuddingRuntime/Tools/BuiltIns/Files/UnifiedDiffParser.cs
```

接口:

```csharp
public static class UnifiedDiffParser
{
    public static IReadOnlyList<UnifiedDiffFilePatch> Parse(string diffText);
}
```

支持:

- `diff --git a/path b/path`
- `--- a/path`, `+++ b/path`
- `@@ -oldStart,oldCount +newStart,newCount @@`
- 多 hunk。
- 文件新增/删除第一版可先拒绝，提示使用 `file_write` 或 `file_delete`。

应用策略:

- 先 dry-run 验证所有 hunk old lines 与当前文件匹配。
- 任一 hunk 失败，整次 patch 不落盘。
- 输出 hunk 失败上下文，避免模型盲目重试。

### 7.4 ToolLoopInstructionBuilder 引导

在 `ToolLoopInstructionBuilder.BuildFromDescriptors` 后追加工具使用指导:

```text
For multi-hunk file edits, prefer file_patch with mode=unified_diff.
Use file_write only for new files or complete intentional rewrites.
```

## 8. P1-2 Durable Task / Checklist / Gate / Artifact

### 8.1 目标

把 `manage_tasks` 从内存 todo 升级成 durable work surface。借鉴 CodeWhale，但保持 Pudding 工具名兼容。

推荐模型可见工具名:

- 新增 `task_create`, `task_list`, `task_read`, `task_cancel`
- 新增 `checklist_write`, `checklist_add`, `checklist_update`, `checklist_list`
- 新增 `task_gate_run`
- 新增 `task_artifact_add`, `task_artifact_read`

兼容:

- `manage_tasks` 保留，但 P2 后默认从模型可见目录隐藏。
- `manage_tasks` 精确调用仍可执行，返回 `_deprecation.use_instead`。

### 8.2 存储

新服务:

```text
Source/PuddingRuntime/Tools/BuiltIns/Tasks/ITaskWorkStore.cs
Source/PuddingRuntime/Tools/BuiltIns/Tasks/FileTaskWorkStore.cs
```

路径:

```text
D:\data\tasks\{workspaceId}\{agentInstanceId}.tasks.json
D:\data\tasks\{workspaceId}\artifacts\{taskId}\...
```

模型:

```csharp
public sealed record DurableTaskRecord
{
    public required string TaskId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public string? SessionId { get; init; }
    public required string Title { get; init; }
    public string Status { get; init; } = "pending";
    public IReadOnlyList<ChecklistItemRecord> Checklist { get; init; } = [];
    public IReadOnlyList<TaskGateRecord> Gates { get; init; } = [];
    public IReadOnlyList<TaskArtifactRecord> Artifacts { get; init; } = [];
    public IReadOnlyList<TaskTimelineEvent> Timeline { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
```

Gate:

```csharp
public sealed record TaskGateRecord
{
    public required string GateId { get; init; }
    public required string Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public string Status { get; init; } = "pending"; // pending/running/passed/failed
    public int? ExitCode { get; init; }
    public long? DurationMs { get; init; }
    public string? Summary { get; init; }
    public string? ArtifactHandleId { get; init; }
}
```

### 8.3 task_gate_run

`task_gate_run` 不应绕过 shell 权限。实现方式:

- 使用 `IShellTaskManager` 或 `HostShellExecutor`。
- 走同一 `AgentFirewall` / shell policy。
- 大日志自动进入 `ToolOutputSpillStore` 或 task artifact。

结果:

```json
{
  "task_id": "task_...",
  "gate_id": "gate_...",
  "status": "passed",
  "command": "pnpm jest ...",
  "exit_code": 0,
  "duration_ms": 12345,
  "artifact": {
    "handle_id": "toolout_...",
    "summary": "3 tests passed"
  }
}
```

## 9. P2-1 工具可见性过滤

### 9.1 问题

当前 `PuddingToolRegistry.ListAvailable(policy)` 只用 `IToolPermissionPolicyService.CanExposeToAgent`。这能做能力策略过滤，但缺少 session/profile/yolo/shell开关/隐藏兼容别名维度。

### 9.2 新接口

新增到 `PuddingCore/Tools`:

```csharp
public interface IToolVisibilityFilter
{
    bool IsVisible(ToolDescriptor descriptor, ToolVisibilityContext context);
}

public sealed record ToolVisibilityContext
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public CapabilityPolicy? CapabilityPolicy { get; init; }
    public bool IsYoloMode { get; init; }
    public bool ShellEnabled { get; init; }
    public bool FileWriteEnabled { get; init; }
    public bool IncludeHiddenAliases { get; init; }
}
```

扩展 registry:

```csharp
IReadOnlyList<ToolDescriptor> ListAvailable(
    CapabilityPolicy? policy,
    ToolVisibilityContext? visibilityContext);
```

扩展 schema:

```csharp
BuildLlmTools(CapabilityPolicy? policy, ToolVisibilityContext? visibilityContext)
```

### 9.3 默认规则

```text
Hidden alias:
  默认不可见，但 GetTool 精确调用可执行。

Shell:
  ShellEnabled=false 且非 YOLO 时隐藏 shell/terminal_execute。

File write:
  FileWriteEnabled=false 时隐藏 file_write/file_patch。

Deprecated:
  terminal_execute/manage_tasks 进入 hidden compatibility 状态。

Dangerous:
  High permission 且 capability 未授权时隐藏，而不是展示后再拒绝。
```

### 9.4 ToolDescriptor 扩展

新增:

```csharp
public bool IsHiddenFromModel { get; init; }
public string? ReplacedByToolId { get; init; }
public string? RemovedInVersion { get; init; }
```

`[Tool]` 可扩展:

```csharp
public bool IsHiddenFromModel { get; set; }
public string? ReplacedByToolId { get; set; }
```

## 10. P2-2 Session Relay

### 10.1 目标

新增主动接力 artifact，作为自动 compact 和 memory recall 的补充。它应保存经过当前 agent 判断的事实，而不是简单复制上下文。

### 10.2 新工具 session_handoff

```csharp
[Tool(
    id: "session_handoff",
    name: "Session handoff",
    description: "Save, read, or list compact handoff artifacts for session continuity.",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Medium)]
public sealed class SessionHandoffTool : PuddingToolBase<SessionHandoffArgs>
```

参数:

```csharp
public sealed record SessionHandoffArgs
{
    public string? Action { get; init; } // save/read/list
    public string? SessionId { get; init; }
    public string? Focus { get; init; }
    public string? Content { get; init; } // optional explicit markdown
}
```

### 10.3 HandoffService

新服务:

```text
Source/PuddingRuntime/Services/SessionHandoffService.cs
```

路径:

```text
D:\data\handoffs\{workspaceId}\{agentInstanceId}\{sessionId}.handoff.md
```

默认模板:

```markdown
# Session Relay

## Goal

## Current Work

## Checklist

## Files Modified

## Key Decisions

## Verification

## Risks

## Next Action
```

### 10.4 触发方式

第一阶段只通过工具显式调用:

```text
session_handoff action=save
```

后续可增加系统命令:

```text
/relay [focus]
/接力 [focus]
```

不要第一版自动在每次 session 结束时调用 LLM 生成 handoff，避免成本和错误事实写入。

## 11. P3 能力边界

### 11.1 RLM

Pudding 已有 `spawn_sub_agent`/多 Agent/记忆系统，RLM 不是 P0。若引入，应使用 CodeWhale 的持久 session 形态:

- `rlm_open`
- `rlm_eval`
- `rlm_configure`
- `rlm_close`
- 通过 `handle_read` 读取大结果。

不要做一个一次性 `rlm` 工具。

### 11.2 Sub-agent 表面收敛

当前 Pudding 工具名是 `spawn_sub_agent`，并有 `query_sub_agents`。CodeWhale 当前模型可见表面收敛到 `agent`，返回 compact receipt + transcript handle。

建议:

- 保留 `spawn_sub_agent` 兼容。
- 新增模型可见 `agent`，底层调用现有 `ISubAgentInvocationService`。
- 返回:

```json
{
  "run_id": "...",
  "status": "...",
  "summary": "...",
  "transcript_handle": "handle_...",
  "artifacts": [],
  "verification": "self_report_only"
}
```

完整 transcript 不进入父上下文，通过 `handle_read` 查看。

### 11.3 GitHub / Automation / MCP

这些按需实现，不进入 P0-P2:

- GitHub: 优先使用结构化工具读 issue/PR/comment，而不是 shell `gh`。
- Automation: 需要 durable task 先稳定，再让 automation run enqueue task。
- MCP manager: Pudding 如需 UI 管理，先设计 config/schema，不直接暴露任意 server mutation。

## 12. 可观测与诊断

新增 telemetry metric:

```text
tool.output.spilled
tool.output.retrieve
shell.task.started
shell.task.completed
shell.task.cancelled
task.gate.run
tool.visibility.filtered
session.handoff.saved
```

字段:

```text
workspace_id
session_id
agent_instance_id
tool_id
duration_ms
output_chars
spilled
handle_id_hash
status
reason
```

session timeline 事件:

```text
tool.output.spilled
shell.task.started
shell.task.output
shell.task.completed
task.gate.completed
session.handoff.saved
```

诊断脚本:

```text
Tools/Diagnostics/query_metrics.py tool-output --days 7 --min-chars 8192
Tools/Diagnostics/query_metrics.py shell-tasks --session-id <id>
```

## 13. 测试计划

### 13.1 Shell

测试项目:

```text
Source/PuddingRuntimeTests/Tools/ShellTaskManagerTests.cs
Source/PuddingRuntimeTests/Tools/HostShellToolLifecycleTests.cs
```

用例:

1. `Shell_Run_Foreground_ReturnsOutput`
2. `Shell_Start_ReturnsTaskIdWithoutWaiting`
3. `Shell_Wait_ReturnsIncrementalOutput`
4. `Shell_Cancel_KillsProcessTree`
5. `Shell_Interact_WritesStdin`
6. `Shell_TaskIsolation_DeniesOtherSessionTask`
7. `Shell_RunTimeout_ReturnsBackgroundHint`

### 13.2 Spill / handle

```text
ToolOutputSpillStoreTests
RetrieveToolResultToolTests
HandleReadToolTests
```

用例:

1. 输出低于阈值不 spill。
2. 输出高于阈值写文件并返回 handle。
3. `summary/head/tail/lines/query` 均能 bounded 读取。
4. 不允许跨 session 读取 handle。
5. tool invocation 对大输出替换为 model-visible reference。

### 13.3 Patch

```text
UnifiedDiffParserTests
FilePatchUnifiedDiffTests
```

用例:

1. 单文件单 hunk。
2. 单文件多 hunk。
3. 多文件 diff。
4. hunk context 不匹配时整次失败。
5. dry-run 不落盘。

### 13.4 Durable task

```text
FileTaskWorkStoreTests
DurableTaskToolsTests
TaskGateRunToolTests
```

用例:

1. task/checklist/gate/artifact 持久化。
2. gate run 成功记录 evidence。
3. gate 大日志进入 artifact/spill。
4. `manage_tasks` 兼容返回 deprecation metadata。

### 13.5 Visibility

```text
ToolVisibilityFilterTests
PuddingToolSchemaServiceVisibilityTests
```

用例:

1. shell disabled 时 schema 不包含 `shell`。
2. yolo mode 时 shell 可见。
3. hidden alias 可精确调用但不进 schema。
4. high risk 未授权时不可见。

## 14. 实施顺序

### Phase P0A: Shell lifecycle

改动:

- `HostShellToolArgs`
- `HostShellTool`
- 新增 `IShellTaskManager/ShellTaskManager`
- DI 注册
- tests

完成标准:

- 长命令可 `shell action=start` 后 `wait/cancel/interact`。
- `terminal_execute` 暂不移除。
- 超时提示指向 `shell action=start`。

### Phase P0B: Spill and retrieve

改动:

- `ToolExecutionResult`
- `ToolInvocationResult`
- `ToolInvocationService`
- 新增 `ToolOutputSpillStore`
- 新增 `retrieve_tool_result`
- 新增基础 `handle_read`
- `ToolLoopInstructionBuilder` 增加说明

完成标准:

- 大输出不再全量进入上下文。
- 模型能用 handle 读取 bounded slice。

### Phase P1A: Unified diff

改动:

- `FilePatchArgs`
- `UnifiedDiffParser`
- `FilePatchTool`
- tests

完成标准:

- multi-hunk diff 成功。
- hunk 不匹配不落盘。

### Phase P1B: Durable work surface

改动:

- 新 durable task store
- task/checklist/gate/artifact tools
- `manage_tasks` 兼容层

完成标准:

- task 可跨进程保留。
- gate evidence 可追踪。

### Phase P2A: Visibility

改动:

- `ToolDescriptor` / `[Tool]` metadata
- `IToolVisibilityFilter`
- `PuddingToolRegistry`
- `PuddingToolSchemaService`

完成标准:

- 模型可见工具表面随 session/profile 收敛。
- hidden alias 不进入 prompt。

### Phase P2B: Relay

改动:

- `SessionHandoffService`
- `session_handoff` tool
- 可选 `/relay` system command

完成标准:

- handoff artifact 可保存、读取、列出。
- compact 后新 session 可通过 handoff 找到上一阶段事实。

## 15. 风险与控制

| 风险 | 控制 |
| --- | --- |
| 工具名过多导致模型混乱 | 新能力优先扩展现有工具；旧名隐藏兼容 |
| Shell 后台进程泄漏 | session scoped task owner、TTL、cancel all、进程树 kill |
| 大输出 handle 泄漏跨 session 数据 | handle metadata 绑定 workspace/session/agent |
| Durable task 与现有 Docs/Tasks 冲突 | 存储在 `D:\data\tasks`，文档任务系统不受影响 |
| 自动 relay 写入错误事实 | 第一版只手动触发 |
| `ToolExecutionResult` 扩展影响广 | 新字段可选，旧调用不受影响 |

## 16. Release smoke

发布前不要只 grep 类名，必须验证模型可见 registry:

```powershell
# 伪命令，后续可补 Tools/Diagnostics 实现
.\.venv\Scripts\python.exe Tools\Diagnostics\query_tools.py list-visible --workspace default --agent default.global_general-assistant.823
```

预期:

- P0 后可见: `shell`, `retrieve_tool_result`, `handle_read`
- P1 后可见: `file_patch` 支持 unified diff, `task_*`, `checklist_*`
- P2 后默认隐藏: `terminal_execute`, `manage_tasks`
- 兼容精确调用仍可执行并返回 deprecation metadata。

## 17. 决策摘要

1. Pudding 不照搬 CodeWhale 多个 shell 工具名，优先扩展现有 `shell action=*`，减少模型选择分叉。
2. `terminal_execute` 保留给 UI/SSE 终端能力，但逐步从模型可见工具目录隐藏。
3. 大输出处理必须进入统一 `ToolInvocationService`，不能只在个别工具里截断。
4. `retrieve_tool_result` 和 `handle_read` 都需要，但第一阶段可共用底层 store。
5. `manage_tasks` 不继续扩展为大而全工具；新 durable work surface 使用 task/checklist/gate/artifact 分离工具。
6. 工具可见性过滤是 P2 必做，否则新增工具会增加模型困惑并降低 prefix cache 稳定性。
7. Relay 第一版必须是显式工具/命令，不做自动 LLM handoff。
