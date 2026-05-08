# 14 消息管线规范化 + 终端代理 + 前端优化 综合架构 ADR

> 状态：**proposed**
> 作者：@architect (战略 ADR)
> 日期：2026-05-08
> 触发条件：A(新架构模式)+B(跨3+模块不可逆数据变更)+C(安全边界决策) — 满足 3/5 条件
> 关联：[02PuddingCore](02PuddingCore.md)、[03PuddingRuntime](03PuddingRuntime.md)、[04PuddingController与Gateway](04PuddingController与Gateway.md)、[05PuddingPlatform](05PuddingPlatform.md)、[10事件系统与事件总线](10事件系统与事件总线.md)、[12多轮会话与工具调用执行](12多轮会话与工具调用执行.md)

---

## 1. 现状诊断

### 1.1 SSE 事件类型现状

当前 `ServerSentEventFrame` 只有 4 种字符串事件名，前端消费 6 种（含 `metadata`/`cancelled`/`error`）：

| 事件名 | 当前用途 | 问题 |
|--------|---------|------|
| `delta` | 增量文本 | 无 thinking/reasoning 分发 |
| `step` | 工具调用通知（仅文本） | 无 tool_call/tool_result 结构 |
| `usage` | Token 统计 | 仅总计，无分层信息 |
| `done` | 流结束 | — |
| `metadata` | 会话元数据（仅前端 relay） | 未标准化 |
| `cancelled` | 取消通知（仅前端 relay） | 未标准化 |

### 1.2 两条执行路径发散

```diff
ExecuteAsync (同步 Agent Loop):
  tools: llmTools (从 SkillRuntime 构建的完整工具列表)
  MaxRounds: _guardrails.MaxRounds (默认多轮)
  行为: 完整的 tool_call → tool_result 闭环

ExecuteStreamAsync (流式 Chat):
- tools: null  ← 显式禁止函数调用
- MaxRounds: 1  ← 单轮
  行为: 仅自然语言回复，ToolCallIndex 以 step 帧通知但丢弃
```

**根因**：流式路径注释写明 "intentionally does not expose function-call deltas to the UI"，但这导致流式路径无法调用任何工具。

### 1.3 推理/思考参数缺失

- `LlmOptions` 只有 `Endpoint / ApiKey / Model / Temperature / MaxTokens`，无 `thinking` / `reasoning_effort`
- `OpenAiLlmGateway.BuildRequestBody` 不发送 `reasoning_effort` 或 `thinking` 参数
- `AgentTemplateDefinition.RuntimeProfile` 无推理深度字段
- 前端 `UpsertGlobalAgentTemplateRequest` 无推理相关字段

### 1.4 StreamDelta 已支持但未暴露的能力

`StreamDelta` 已经定义了 `ReasoningDelta`、`ToolCallIndex/Id/NameDelta/ArgsDelta`，但这些字段在 `ExecuteStreamAsync` 中：
- `ReasoningDelta` 完全忽略
- `ToolCall*` 仅记录为 step 通知然后丢弃

---

## 2. ADR-014-A：统一 SSE 事件类型常量与协议标准化

### 决定

在 `PuddingCode.Platform` 命名空间新增 `SseEventType` 静态常量类和类型化 SSE 事件 DTO，替代散落各处的字符串字面量。

### SSE 事件类型规范（v2）

```
┌──────────────────────────────────────────────────────────────┐
│                    SSE Event Types v2                        │
├──────────────┬───────────────────────────────────────────────┤
│ event 名称    │ 语义                                          │
├──────────────┼───────────────────────────────────────────────┤
│ metadata     │ 流元数据（sessionId/messageId/routeDecisionId）│
│ thinking     │ 思维链增量（reasoning tokens）                 │
│ delta        │ 回复文本增量                                   │
│ tool_call    │ 工具调用开始（name + args 累积）               │
│ tool_result  │ 工具调用结果（exitCode + output + error）      │
│ terminal     │ 终端代理执行过程（stdout/stderr 增量）         │
│ usage        │ Token 用量快照                                 │
│ context      │ 上下文各层 Token 占比（STATIC/TOOLS/...）      │
│ step         │ 状态转换通知（可读文本，保留向后兼容）          │
│ done         │ 流正常结束                                     │
│ error        │ 流错误终止                                     │
│ cancelled    │ 用户取消                                       │
└──────────────┴───────────────────────────────────────────────┘
```

### 实现位置

| 层 | 文件 | 职责 |
|----|------|------|
| **PuddingCore** | `Platform/SseEventTypes.cs` (新建) | 常量定义 + 类型化 DTO |
| **PuddingRuntime** | `AgentExecutionService.cs` | 按新协议发送事件 |
| **PuddingController** | `LlmProxyController.cs` | 透传，不做变换 |
| **PuddingPlatform** | `ChatApiController.cs` | 透传 |
| **PuddingPlatformAdmin** | `api.ts` + `chat/index.tsx` | 按新事件类型分发 UI |

### 向后兼容

前端 `AdminChatStreamEvent` 联合类型新增 `thinking`/`tool_call`/`tool_result`/`terminal`/`context` 变体，旧有 `delta`/`step`/`usage`/`done` 行为不变。

---

## 3. ADR-014-B：流式工具调用——ExecuteStreamAsync 与 ExecuteAsync 路径统一

### 问题

`ExecuteStreamAsync` 显式 `tools: null`，无法触发工具调用。用户要通过 SSE 看到工具执行全过程。

### 方案对比

| 方案 | 优点 | 缺点 | 评分 |
|------|------|------|------|
| A. 流式路径内置 mini Agent Loop | 最完整 | 将 Loop 逻辑复制到流式路径，维护两份 | ❌ |
| B. 流式路径转发到 ExecuteAsync + Channel<SSE帧> | 逻辑统一 | ExecuteAsync 非流式 LLM 调用，需改造 | ⚠️ |
| **C. ExecuteStreamAsync 保留但传入 tools，工具调用后回到流式** | **复用现有 LLM 流式** | 需在流式内部加入工具结果 → LLM 再调用循环 | ✅ |

### 决定：方案 C

```csharp
// 流式路径改造后的伪代码结构
async IAsyncEnumerable<ServerSentEventFrame> ExecuteStreamAsync(...) {
    // ... 初始化同现 ...
    
    for (int round = 0; round < maxRounds; round++) {
        await foreach (var delta in _llmClient.ChatStreamAsync(
            ..., tools: llmTools, ...))  // ← 不再传 null
        {
            // 分发 thinking / delta / tool_call / usage 帧
            if (delta.ReasoningDelta != null)
                yield return Sse("thinking", ...);
            if (delta.ContentDelta != null)
                yield return Sse("delta", ...);
            if (delta.ToolCallNameDelta != null)
                // 累积 tool_call 信息，finish_reason=tool_calls 时发送
                ...
            if (delta.Usage != null)
                yield return Sse("usage", ...);
        }
        
        if (accumulatedToolCalls.Count == 0) break;  // 无工具调用 → 终止
        
        // 执行工具调用（复用 SandboxExecutor + SkillRuntime）
        foreach (var tc in accumulatedToolCalls) {
            yield return Sse("tool_call", new { name, args });
            var result = await _skillRuntime.InvokeAsync(...);
            yield return Sse("tool_result", new { exitCode, output, error });
            history.Add(toolResultMessage);
        }
        // 继续下一轮 LLM 调用
    }
    yield return Sse("done", ...);
}
```

**关键参数**：
- `maxRounds` 默认 5（流式路径护栏，避免无限循环）
- 护栏复用现有 `AgentExecutionGuardrails`（MaxToolCallsTotal / MaxElapsed / MaxSameToolRepeat）
- 中间层 `LlmProxyController` 需同步支持 `tools` 参数的 SSE 透传

### 影响面

| 模块 | 文件 | 变更 |
|------|------|------|
| PuddingRuntime | `AgentExecutionService.cs` | ExecuteStreamAsync 重构：传入 tools、循环执行 |
| PuddingCore | `Platform/LlmProxyContracts.cs` | ControllerLlmChatRequest 新增 StreamTools 字段 |
| PuddingController | `LlmProxyController.cs` | ChatStream 端点支持 tools 透传 |
| PuddingCore | `Core/OpenAiLlmGateway.cs` | ChatStreamAsync 传入 tools 参数（已有能力） |
| PuddingPlatformAdmin | `api.ts` | 新增 tool_call/tool_result 事件类型 |

**风险等级**：中（涉及 4 模块，但每层改动量小，复用现有设施）

---

## 4. ADR-014-C：OpenAI 协议层 thinking 参数

### 背景

DeepSeek Reasoner（V3.1/R1）和 OpenAI o1 系列支持 `reasoning_effort` 参数控制推理深度。这是推理模型的核心差异能力。

### 决定

#### 4.1 协议层（PuddingCore）

```csharp
// LlmOptions 新增字段
public sealed record LlmOptions(
    string Endpoint, string ApiKey, string Model,
    double? Temperature = null, int? MaxTokens = null,
    // ── 新增 ──
    string? ReasoningEffort = null,   // "low" | "medium" | "high"
    bool? EnableThinking = null       // 是否启用思维链（某些 provider 用此字段）
);
```

`OpenAiLlmGateway.BuildRequestBody` 按 provider 协议差异映射：
- DeepSeek: `reasoning_effort` → `"reasoning_effort": "medium"`
- OpenAI o-series: `reasoning_effort` → `"reasoning_effort": "medium"`
- Anthropic（通过 OpenAI 兼容端点）: `thinking` → `"thinking": {"type": "enabled", "budget_tokens": 4096}`

#### 4.2 配置层（PuddingPlatform）

- `AgentTemplateDefinition.RuntimeProfile` 新增 `ReasoningEffort` 字段
- `GlobalAgentTemplateDto` / `WorkspaceAgentTemplateDto` 新增 `reasoningEffort?: string`
- 数据库 `GlobalAgentTemplateEntity` / `WorkspaceAgentTemplateEntity` 新增 `ReasoningEffort` 列
- 后台管理页面：Agent Template 编辑页新增「推理深度」下拉选项（低/中/高）

#### 4.3 用户动态覆盖（PuddingPlatformAdmin 前端）

- `AdminChatRequest` 新增可选字段 `reasoningEffort?: string`
- 聊天输入区新增「推理深度」快捷切换按钮（低/中/高），默认「跟随模板设置」
- 用户选择后覆盖模板默认值，仅影响当次请求

### 影响面

| 模块 | 变更 | 风险 |
|------|------|------|
| PuddingCore | LlmOptions + OpenAiLlmGateway + AgentTemplateDefinition | 低（纯扩展） |
| PuddingPlatform | DTO + Entity + Migration + API | 中（DB migration） |
| PuddingPlatformAdmin | api.ts + template 编辑页 + 聊天输入区 | 低 |

---

## 5. ADR-014-D：前端 Token 饼图与上下文层可视化

### 决定

#### 5.1 数据源

`ContextPipeline.AssembleAsync` 内部已逐层计算 Token 用量，新增输出结构：

```csharp
public sealed record ContextLayerSnapshot
{
    public string LayerName { get; init; }      // "STATIC" / "TOOLS" / "SKILLS" / ...等
    public int EstimatedTokens { get; init; }
    public double Percentage { get; init; }
}

// ContextPipeline.AssembleAsync 返回改为:
public async Task<ContextAssemblyResult> AssembleAsync(...)

public sealed record ContextAssemblyResult
{
    public string SystemPrompt { get; init; }
    public int TotalBudget { get; init; }
    public int UsedTokens { get; init; }
    public IReadOnlyList<ContextLayerSnapshot> Layers { get; init; }
}
```

#### 5.2 SSE 事件

新增 `context` 事件类型，在流开始时随 `metadata` 发送一次：

```json
event: context
data: {
  "totalBudget": 8192,
  "usedTokens": 5200,
  "layers": [
    {"name": "STATIC", "tokens": 1800, "pct": 22.0},
    {"name": "TOOLS", "tokens": 260, "pct": 3.2},
    {"name": "SKILLS", "tokens": 260, "pct": 3.2},
    {"name": "USER", "tokens": 150, "pct": 1.8},
    {"name": "PINNED", "tokens": 520, "pct": 6.3},
    {"name": "RECENT", "tokens": 2080, "pct": 25.4},
    {"name": "RECALLED", "tokens": 1300, "pct": 15.9},
    {"name": "CURRENT", "tokens": 780, "pct": 9.5}
  ]
}
```

#### 5.3 前端组件

- 替换现有 `TokenBar`（antd Progress）为 ECharts 环形图
- 环中心显示 `used / total`
- 悬浮/点击展开各层详情（名称 + 精确 Token 数）
- 响应式：移动端缩小为纯进度条

### 影响面

| 模块 | 变更 | 风险 |
|------|------|------|
| PuddingRuntime | ContextPipeline 输出结构化结果 | 低（内部重构） |
| PuddingRuntime | AgentExecutionService 发送 context 帧 | 低 |
| PuddingPlatformAdmin | TokenBar → TokenPieChart | 低（UI 替换） |

---

## 6. ADR-014-E：终端代理 (Terminal Agent)

### 6.1 设计原则映射

| 用户原则 | 架构决策 |
|----------|---------|
| 消息管线是全局问题 | 终端输出通过 SSE `terminal` 事件流经全链路推送 |
| 进程不能因 Agent/前端掉线而终止 | 进程由独立 `TerminalProcessManager` 管理，与会话生命周期解耦 |
| 不要复杂化 | 复用 SandboxExecutor 权限模型 + IAgentLoopHook 生命周期 + IMemoryLibraryConvenience 持久化 |

### 6.2 架构分层

```
┌──────────────────────────────────────────────────────────────┐
│                     Terminal Agent 架构                       │
├──────────────────────────────────────────────────────────────┤
│                                                               │
│  ┌─────────────────────┐     ┌──────────────────────────┐   │
│  │ PuddingPlatformAdmin │     │   TerminalProcessManager  │   │
│  │  ┌───────────────┐   │     │  ┌────────────────────┐  │   │
│  │  │TerminalPanel  │   │     │  │ ProcessRegistry    │  │   │
│  │  │(进程列表+终止) │   │     │  │ (ConcurrentDict)   │  │   │
│  │  └───────────────┘   │     │  └────────────────────┘  │   │
│  └─────────────────────┘     │  ┌────────────────────┐  │   │
│           │                  │  │ ProcessHost        │  │   │
│           │ SSE              │  │ (System.Diagnostic. │  │   │
│           ▼                  │  │  Process 包装)      │  │   │
│  ┌─────────────────────┐     │  └────────────────────┘  │   │
│  │   AgentExecutionSvc  │────▶│  ┌────────────────────┐  │   │
│  │   (tool_call →       │     │  │ OutputBuffer       │  │   │
│  │    terminal_execute) │     │  │ (Channel<string>)   │  │   │
│  └─────────────────────┘     │  └────────────────────┘  │   │
│           │                  │  ┌────────────────────┐  │   │
│           │ SandboxExecutor  │  │ ExecutionLogger    │  │   │
│           ▼                  │  │ (文件 + 记忆图书馆)  │  │   │
│  ┌─────────────────────┐     │  └────────────────────┘  │   │
│  │ CommandWhitelist    │     └──────────────────────────┘   │
│  │ + WorkingDirIsolate │                                    │
│  └─────────────────────┘                                    │
└──────────────────────────────────────────────────────────────┘
```

### 6.3 TerminalProcessManager 生命周期

```
创建
  │
  ▼
┌─────────┐    stdout/stderr    ┌──────────────┐
│ Process │ ──────────────────▶ │ OutputBuffer │ ──▶ SSE terminal 事件
│ (OS级)  │                     │ (Channel)    │
└─────────┘                     └──────────────┘
  │                                    │
  │ 进程独立于 Agent/前端               │
  │ 断开后继续运行                      │
  │                                    ▼
  │                             ┌──────────────┐
  │                             │ExecutionLog  │
  │                             │.log 文件      │
  │                             │+ 记忆图书馆    │
  │                             └──────────────┘
  │
  │ 前端发送终止指令
  ▼
┌─────────┐
│ Kill()  │ ← 通过 HTTP API /api/terminal/{pid}/kill
└─────────┘
```

### 6.4 关键接口定义

```csharp
// ITerminalProcessManager（PuddingRuntime 服务）
public interface ITerminalProcessManager
{
    /// <summary>启动进程，返回进程 ID</summary>
    Task<TerminalProcessInfo> StartAsync(
        string sessionId, string command, string workingDir,
        CancellationToken ct);

    /// <summary>订阅进程实时输出</summary>
    IAsyncEnumerable<string> SubscribeAsync(string processId, CancellationToken ct);

    /// <summary>终止进程</summary>
    Task<bool> KillAsync(string processId);

    /// <summary>列出所有运行中进程</summary>
    IReadOnlyList<TerminalProcessInfo> ListProcesses(string? sessionId = null);

    /// <summary>清理已结束的僵尸进程记录</summary>
    Task<int> ReapAsync();
}

public sealed record TerminalProcessInfo
{
    public string ProcessId { get; init; }
    public string SessionId { get; init; }
    public string Command { get; init; }
    public string WorkingDir { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public int? ExitCode { get; init; }
    public TerminalProcessStatus Status { get; init; }
}

public enum TerminalProcessStatus { Running, Exited, Killed, Failed }
```

### 6.5 SSE 事件流

Agent 调用 `terminal_execute` 工具 → Runtime 执行 → 逐帧推送：

```
event: tool_call
data: {"name": "terminal_execute", "args": {"command": "dotnet build", "cwd": "/project"}}

event: terminal
data: {"pid": "abc123", "stream": "stdout", "data": "Build started...\n"}

event: terminal
data: {"pid": "abc123", "stream": "stderr", "data": "warning CS0168: ...\n"}

event: terminal
data: {"pid": "abc123", "stream": "stdout", "data": "Build succeeded.\n"}

event: terminal
data: {"pid": "abc123", "type": "exit", "exitCode": 0}

event: tool_result
data: {"name": "terminal_execute", "exitCode": 0, "pid": "abc123"}
```

### 6.6 安全边界——命令白名单与危险命令拦截

**复用 SandboxExecutor 三层权限模型**：

```
┌─────────────────────────────────────────────┐
│          终端代理安全边界                      │
├─────────────────────────────────────────────┤
│                                              │
│  第一层：CapabilityPolicy.AllowShellExecution │
│    ↓ 模板级：是否允许任何 Shell 执行          │
│                                              │
│  第二层：CommandWhitelist                     │
│    ↓ 命令级：允许的命令正则/前缀列表           │
│    示例：["dotnet *", "git *", "ls*", "cat*",│
│            "echo *", "python *", "node *"]    │
│                                              │
│  第三层：DangerousCommandInterceptor           │
│    ↓ 模式级：拦截危险模式（即使通过白名单）     │
│    拦截：rm -rf /、format、dd if=、           │
│          curl ... | sh、> /dev/sda 等          │
│                                              │
│  第四层：WorkingDirectoryIsolation             │
│    ↓ 工作目录限制在 AllowPathPrefix 范围内     │
│    默认：workspace 目录 + temp 目录            │
│                                              │
└─────────────────────────────────────────────┘
```

```csharp
// CommandWhitelist（PuddingCore 新增）
public sealed class CommandWhitelist
{
    /// <summary>允许的命令前缀/正则列表（空 = 无限制，但不推荐）</summary>
    public IReadOnlyList<string> AllowedPatterns { get; init; } = [];

    /// <summary>允许的工作目录前缀</summary>
    public IReadOnlyList<string> AllowedWorkingDirectories { get; init; } = [];

    /// <summary>是否允许管道符 |</summary>
    public bool AllowPipe { get; init; } = false;

    /// <summary>是否允许重定向 > / <</summary>
    public bool AllowRedirect { get; init; } = false;

    /// <summary>最大执行时间（秒）</summary>
    public int MaxExecutionSeconds { get; init; } = 300;

    /// <summary>最大输出大小（KB）</summary>
    public int MaxOutputKb { get; init; } = 1024;
}
```

### 6.7 持久化——文件 + 记忆图书馆

```
data/terminal/
  {sessionId}/
    {processId}.log       ← 完整 stdout+stderr 文本
    {processId}.meta.json ← 进程元数据（命令、时间、退出码）

记忆图书馆（IMemoryLibraryConvenience）:
  Book: "terminal_executions"
  Chapter: {sessionId}
  Pointer: {processId} → 摘要 + 文件路径
```

### 6.8 影响面

| 模块 | 文件 | 变更 | 风险 |
|------|------|------|------|
| **PuddingCore** | (新建) `Terminal/CommandWhitelist.cs` | 命令白名单模型 | 低 |
| **PuddingCore** | `Platform/AgentTemplateDefinition.cs` | CapabilityPolicy 新增 CommandWhitelist 引用 | 低 |
| **PuddingRuntime** | (新建) `Services/TerminalProcessManager.cs` | ITerminalProcessManager 实现 | 中 |
| **PuddingRuntime** | `Services/SandboxExecutor.cs` | 新增 ValidateCommand() | 低 |
| **PuddingRuntime** | `Services/SkillRuntime.cs` 或新建 Skill | terminal_execute 技能注册 | 低 |
| **PuddingRuntime** | `Services/AgentExecutionService.cs` | terminal 事件发送 | 低 |
| **PuddingPlatform** | `Controllers/Api/TerminalController.cs` (新建) | 进程列表/终止 HTTP API | 中 |
| **PuddingPlatformAdmin** | (新建) `pages/chat/components/TerminalPanel.tsx` | 终端进程面板 | 中 |
| **PuddingPlatformAdmin** | `api.ts` | terminal API 客户端 | 低 |

---

## 7. ADR-014-F：不影响用户端 SSE 的中间层 relay 策略

### 决定

Controller 层 SSE 中继在五类帧上只需透传：

| 帧类型 | Controller 行为 |
|--------|----------------|
| `thinking` | 透明透传 |
| `delta` | 透明透传 |
| `tool_call` | 透明透传 |
| `tool_result` | 透明透传 |
| `terminal` | 透明透传 |
| `usage` | 透传 + Controller 自身埋点 |
| `context` | 透传 |
| `done` | 透传 |
| `error` | 透传 |

Controller 不做帧级别解析或变换，保持零开销中继。Gateway 层同理。

---

## 8. 综合影响面总览

```
                         PuddingCore
                    ┌─────┼─────┐
                    │ 新增  │ 变更  │
                    ├─────┼─────┤
                    │SseEvent│Stream│
                    │Types   │Delta │
                    │Command │LlmOpt│
                    │White   │ions  │
                    │list    │      │
                    └───────┴──────┘
                          │
          ┌───────────────┼───────────────┐
          ▼               ▼               ▼
    PuddingRuntime  PuddingPlatform  PuddingPlatformAdmin
    ┌─────┬─────┐  ┌─────┬─────┐   ┌─────┬─────┐
    │新增  │变更  │  │新增  │变更  │   │新增  │变更  │
    ├─────┼─────┤  ├─────┼─────┤   ├─────┼─────┤
    │Termin│Agent │  │Termin│ChatApi│   │Token │api.ts│
    │alProc│Exec  │  │alCtrl│Control│   │Pie   │Input │
    │essMgr│Svc   │  │ler   │ler    │   │Chart │Area  │
    │      │Contex│  │      │DTOs   │   │Termin│Templa│
    │      │tPipe │  │      │Migrations│alPnl│teEdit│
    │      │line  │  │      │       │   │      │      │
    └──────┴──────┘  └──────┴───────┘   └──────┴──────┘
```

**总风险等级**：中
- **高风险点**：ExecuteStreamAsync 重构（需充分测试工具调用闭环 + 取消语义）
- **中风险点**：TerminalProcessManager 进程生命周期 + 数据库 Migration
- **低风险点**：SSE 事件类型常量、前端 Token 饼图、thinking 参数传递

---

## 9. 实施建议

### 阶段 1：消息管线（优先级最高，其他依赖此项）
1. 定义 `SseEventTypes` + 类型化 DTO（PuddingCore）
2. 改造 `ExecuteStreamAsync` 支持 tools + 工具调用闭环
3. 所有层 relay 新事件类型

### 阶段 2：thinking 参数 + Token 饼图（可并行）
4. LlmOptions 扩展 + OpenAiLlmGateway 透传
5. AgentTemplate 配置 + 前端编辑页
6. ContextPipeline 输出结构化结果 + 前端 TokenPieChart

### 阶段 3：终端代理
7. TerminalProcessManager 实现
8. SandboxExecutor 命令级安全校验
9. 前端终端进程面板

---

## 10. ADR-014-A~F 测试范围

| ADR | 测试类型 | 测试范围 |
|-----|---------|---------|
| A | 集成 | SSE 帧往返：Runtime → Controller → Platform → 前端 |
| B | 单元+集成 | ExecuteStreamAsync 工具调用闭环（单工具/多工具/护栏/取消） |
| C | 单元 | OpenAiLlmGateway 参数序列化（各 provider） |
| D | 单元+E2E | ContextPipeline 输出验证 + 前端渲染 |
| E | 集成+E2E | 终端进程启停/隔离/白名单拦截/掉线恢复 |

---

## 附录：关键文件清单

### 需新建
| 文件 | 模块 |
|------|------|
| `Platform/SseEventTypes.cs` | PuddingCore |
| `Terminal/CommandWhitelist.cs` | PuddingCore |
| `Services/TerminalProcessManager.cs` | PuddingRuntime |
| `Controllers/Api/TerminalController.cs` | PuddingPlatform |
| `pages/chat/components/TokenPieChart.tsx` | PuddingPlatformAdmin |
| `pages/chat/components/TerminalPanel.tsx` | PuddingPlatformAdmin |

### 需修改
| 文件 | 变更要点 |
|------|---------|
| `Platform/StreamingContracts.cs` | ServerSentEventFrame 类型化辅助方法 |
| `Models/StreamDelta.cs` | 已有字段，无需修改 |
| `Models/LlmOptions.cs` | 新增 ReasoningEffort / EnableThinking |
| `Core/OpenAiLlmGateway.cs` | BuildRequestBody 新增推理参数 |
| `Platform/AgentTemplateDefinition.cs` | RuntimeProfile 新增 ReasoningEffort |
| `Platform/LlmProxyContracts.cs` | ControllerLlmChatRequest 新增 StreamTools / ReasoningEffort |
| `Services/AgentExecutionService.cs` | ExecuteStreamAsync 重构 + context 帧发送 |
| `Services/ContextPipeline.cs` | AssembleAsync 输出 ContextAssemblyResult |
| `Services/SandboxExecutor.cs` | 新增命令级安全校验方法 |
| `Controllers/LlmProxyController.cs` | SSE 中继 tools + thinking |
| `Controllers/Api/ChatApiController.cs` | 推理参数透传 |
| `Data/Dtos/PlatformDtos.cs` | AdminChatRequest 新增 reasoningEffort |
| `Data/Entities/` | 数据库 Migration |
| `services/platform/api.ts` | 新增 SSE 事件类型 + terminal API |
| `pages/chat/components/TokenBar.tsx` | 替换为 TokenPieChart |
| `pages/chat/components/InputArea.tsx` | 推理深度切换 |
| `pages/chat/index.tsx` | 新增 SSE 事件处理分支 |
| `pages/admin/templates/` | 编辑页新增推理参数 |
