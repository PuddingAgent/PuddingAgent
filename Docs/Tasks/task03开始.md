# Task 03: V0.1 实现方案 — 从脚手架到 Agent 闭环

> **目标：** 在现有三项目脚手架（`PuddingCode` / `PuddingCodeCLI` / `PuddingCodeDesktop`）基础上，完成 V0.1 里程碑——打通 **"用户输入 → LLM 调用 → 工具执行 → 结果回传 → 最终回答"** 的完整 Agent 闭环。

---

> 命令行可以使用CliWrap

## 目录

1. [当前状态与目标](#1-当前状态与目标)
2. [Step 1：能力契约 — Core 抽象层](#2-step-1能力契约--core-抽象层)
3. [Step 2：本地工具 — 感官与肢体](#3-step-2本地工具--感官与肢体)
4. [Step 3：API 网关 — 接入 LLM](#4-step-3api-网关--接入-llm)
5. [Step 4：CLI 交互 — REPL 原型](#5-step-4cli-交互--repl-原型)
6. [项目结构规划](#6-项目结构规划)
7. [验收标准](#7-验收标准)
8. [依赖清单](#8-依赖清单)

---

## 1. 当前状态与目标

### 1.1 现有代码

| 项目 | 路径 | 当前状态 |
| --- | --- | --- |
| `PuddingCode` (Core 类库) | `Source/PuddingCode/` | 空 `Class1.cs`，.NET 10 |
| `PuddingCodeCLI` (控制台) | `Source/PuddingCodeCLI/` | `Hello World`，已引用 Core，AOT 启用 |
| `PuddingCodeDesktop` (桌面) | `Source/PuddingCodeDesktop/` | Avalonia 模板，暂不涉及 |

### 1.2 V0.1 目标

```text
用户输入 ──→ CLI (Spectre.Console REPL)
                │
                ▼
         PuddingCode.Core
         ┌──────────────────────┐
         │  AgentOrchestrator   │ ← 编排 LLM 对话 + Tool Calling
         │    │                 │
         │    ├→ LLM Gateway    │ ← OpenAI 兼容 API（Claude / DeepSeek / GPT）
         │    │                 │
         │    └→ ToolRegistry   │ ← 自动发现并执行本地工具
         │        ├ FileTool    │
         │        └ ShellTool   │
         └──────────────────────┘
                │
                ▼
         结果回传 ──→ CLI 渲染输出
```

---

## 2. Step 1：能力契约 — Core 抽象层

> **原则：** 在接入任何 API 之前，先定义 Agent 能做什么。后续接入 Claude、GPT 或本地模型时，逻辑统一。

### 2.1 核心接口

#### `ITool` — 工具契约

每个工具（读文件、执行命令等）都实现此接口。Agent 通过它来了解"我有哪些手"。

```csharp
namespace PuddingCode.Abstractions;

/// <summary>
/// Agent 可调用的工具。实现此接口后自动被 ToolRegistry 发现。
/// </summary>
public interface ITool
{
    /// <summary>工具名称，对应 LLM function calling 的 name 字段</summary>
    string Name { get; }

    /// <summary>工具描述，告诉 LLM 这个工具做什么</summary>
    string Description { get; }

    /// <summary>JSON Schema 格式的参数定义，用于生成 tools JSON</summary>
    ToolParameterSchema Parameters { get; }

    /// <summary>执行工具，传入 LLM 返回的参数 JSON，返回结果文本</summary>
    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}
```

#### `IToolRegistry` — 工具注册中心

```csharp
namespace PuddingCode.Abstractions;

/// <summary>
/// 管理所有可用工具。支持自动发现（反射）和手动注册。
/// </summary>
public interface IToolRegistry
{
    /// <summary>注册一个工具</summary>
    void Register(ITool tool);

    /// <summary>按名称查找工具</summary>
    ITool? GetTool(string name);

    /// <summary>获取所有已注册工具</summary>
    IReadOnlyList<ITool> GetAllTools();

    /// <summary>生成 OpenAI/Claude tools JSON 数组</summary>
    string ToToolsJson();
}
```

#### `ILlmGateway` — LLM 网关

```csharp
namespace PuddingCode.Abstractions;

/// <summary>
/// LLM API 网关。屏蔽不同供应商的差异（Claude / GPT / DeepSeek），
/// 统一使用 OpenAI Chat Completions 兼容协议。
/// </summary>
public interface ILlmGateway
{
    /// <summary>发送对话消息，获取 LLM 响应（可能包含 tool_calls）</summary>
    Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        CancellationToken ct = default);
}
```

#### `IAgentOrchestrator` — 编排器

```csharp
namespace PuddingCode.Abstractions;

/// <summary>
/// Agent 主循环编排器。处理 "对话 → Tool Call → 执行 → 回传" 的闭环。
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>处理用户的一次输入，返回 Agent 最终回答</summary>
    IAsyncEnumerable<AgentEvent> ProcessAsync(string userInput, CancellationToken ct = default);
}
```

### 2.2 消息与事件模型

```csharp
namespace PuddingCode.Models;

/// <summary>对话消息</summary>
public sealed record ChatMessage(ChatRole Role, string Content, string? ToolCallId = null);

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>LLM 响应</summary>
public sealed record LlmResponse(string? Content, IReadOnlyList<ToolCall>? ToolCalls);

/// <summary>工具调用请求</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Agent 事件流（用于 UI 渲染）</summary>
public abstract record AgentEvent;
public sealed record ThinkingEvent(string Thought) : AgentEvent;
public sealed record ToolCallEvent(string ToolName, string Arguments) : AgentEvent;
public sealed record ToolResultEvent(string ToolName, string Result) : AgentEvent;
public sealed record AnswerEvent(string Content) : AgentEvent;
public sealed record ErrorEvent(string Message) : AgentEvent;
```

### 2.3 工具参数 Schema

```csharp
namespace PuddingCode.Models;

/// <summary>描述工具参数的 JSON Schema</summary>
public sealed record ToolParameterSchema(
    IReadOnlyList<ToolParameter> Properties,
    IReadOnlyList<string> Required);

public sealed record ToolParameter(
    string Name,
    string Type,
    string Description);
```

---

## 3. Step 2：本地工具 — 感官与肢体

> **原则：** Agent 还没说话之前，得先能干活。实现最基础的本地工具，并用单元测试验证，不依赖 AI。

### 3.1 FileTool — 文件读写

```csharp
namespace PuddingCode.Tools;

public sealed class FileTool : ITool
{
    public string Name => "file";
    public string Description => "Read or write file contents. Actions: read, write, list.";

    public ToolParameterSchema Parameters => new(
        [
            new("action", "string", "The action to perform: read, write, or list"),
            new("path", "string", "File or directory path"),
            new("content", "string", "Content to write (only for write action)")
        ],
        ["action", "path"]);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<FileToolArgs>(argumentsJson);

        return args?.Action?.ToLower() switch
        {
            "read"  => await File.ReadAllTextAsync(args.Path, ct),
            "write" => await WriteFileAsync(args.Path, args.Content ?? "", ct),
            "list"  => string.Join("\n", Directory.GetFileSystemEntries(args.Path)),
            _       => $"Unknown action: {args?.Action}"
        };
    }

    private static async Task<string> WriteFileAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content, ct);
        return $"Written {content.Length} chars to {path}";
    }
}

file record FileToolArgs(string? Action, string Path, string? Content);
```

### 3.2 ShellTool — 命令行执行

```csharp
namespace PuddingCode.Tools;

public sealed class ShellTool : ITool
{
    public string Name => "shell";
    public string Description => "Execute a shell command and return stdout/stderr.";

    public ToolParameterSchema Parameters => new(
        [
            new("command", "string", "The shell command to execute"),
            new("workingDirectory", "string", "Working directory (optional)")
        ],
        ["command"]);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<ShellToolArgs>(argumentsJson);
        if (args?.Command is null) return "Error: command is required";

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            Arguments = isWindows ? $"/c {args.Command}" : $"-c \"{args.Command}\"",
            WorkingDirectory = args.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var result = $"Exit code: {process.ExitCode}";
        if (!string.IsNullOrWhiteSpace(stdout)) result += $"\n--- stdout ---\n{stdout}";
        if (!string.IsNullOrWhiteSpace(stderr)) result += $"\n--- stderr ---\n{stderr}";
        return result;
    }
}

file record ShellToolArgs(string? Command, string? WorkingDirectory);
```

### 3.3 单元测试要求

| 测试 | 验证点 |
| --- | --- |
| `FileTool_Read` | 读取已知文件，返回正确内容 |
| `FileTool_Write` | 写入文件后再读取，内容一致 |
| `FileTool_List` | 列出目录，返回包含已知文件名 |
| `ShellTool_Echo` | 执行 `echo hello`，stdout 包含 `hello` |
| `ShellTool_ExitCode` | 执行非法命令，ExitCode ≠ 0 |

---

## 4. Step 3：API 网关 — 接入 LLM

> **核心难点：双向翻译。** 将 C# `ITool` 描述 → OpenAI `tools` JSON；将 LLM 返回的 `tool_calls` → C# 方法调用。

### 4.1 ToolRegistry 实现

```csharp
namespace PuddingCode.Core;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    public ITool? GetTool(string name) =>
        _tools.GetValueOrDefault(name);

    public IReadOnlyList<ITool> GetAllTools() =>
        [.. _tools.Values];

    /// <summary>
    /// 将所有工具转换为 OpenAI function calling 的 tools JSON 格式。
    /// 这是"C# → LLM"方向的翻译。
    /// </summary>
    public string ToToolsJson()
    {
        var tools = _tools.Values.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = new
                {
                    type = "object",
                    properties = t.Parameters.Properties.ToDictionary(
                        p => p.Name,
                        p => new { type = p.Type, description = p.Description }),
                    required = t.Parameters.Required
                }
            }
        });

        return JsonSerializer.Serialize(tools, new JsonSerializerOptions { WriteIndented = true });
    }
}
```

### 4.2 OpenAI 兼容 LLM 网关

```csharp
namespace PuddingCode.Core;

/// <summary>
/// 基于 OpenAI Chat Completions API 兼容协议的 LLM 网关。
/// 支持 Claude（通过 OpenAI 兼容端点）、DeepSeek、GPT 等。
/// </summary>
public sealed class OpenAiLlmGateway(HttpClient httpClient, LlmOptions options) : ILlmGateway
{
    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(messages, tools);
        var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        return ParseResponse(json);
    }

    // ... BuildRequestBody: 组装 messages + tools JSON
    // ... ParseResponse:    解析 content 和 tool_calls
}

public sealed record LlmOptions(string Endpoint, string ApiKey, string Model);
```

### 4.3 AgentOrchestrator — 编排闭环

这是 Agent 的主循环，实现 **"对话 → Tool Call → 执行 → 结果回传 → 最终回答"**：

```csharp
namespace PuddingCode.Core;

public sealed class AgentOrchestrator(
    ILlmGateway llm,
    IToolRegistry tools) : IAgentOrchestrator
{
    private readonly List<ChatMessage> _history =
    [
        new(ChatRole.System, """
            You are PuddingCode, an AI programming assistant.
            Use the provided tools to help the user with coding tasks.
            """)
    ];

    public async IAsyncEnumerable<AgentEvent> ProcessAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _history.Add(new ChatMessage(ChatRole.User, userInput));

        while (true)
        {
            // 1. 调用 LLM
            yield return new ThinkingEvent("Calling LLM...");
            var response = await llm.ChatAsync(_history, tools.GetAllTools(), ct);

            // 2. 如果没有工具调用，返回最终回答
            if (response.ToolCalls is null or { Count: 0 })
            {
                var answer = response.Content ?? "";
                _history.Add(new ChatMessage(ChatRole.Assistant, answer));
                yield return new AnswerEvent(answer);
                yield break;
            }

            // 3. 有工具调用：逐个执行
            _history.Add(new ChatMessage(ChatRole.Assistant, response.Content ?? "")
            {
                // 此处需要记录 tool_calls 到 history（具体序列化方式取决于实现）
            });

            foreach (var call in response.ToolCalls)
            {
                yield return new ToolCallEvent(call.Name, call.ArgumentsJson);

                var tool = tools.GetTool(call.Name);
                string result;

                if (tool is null)
                {
                    result = $"Error: unknown tool '{call.Name}'";
                    yield return new ErrorEvent(result);
                }
                else
                {
                    result = await tool.ExecuteAsync(call.ArgumentsJson, ct);
                    yield return new ToolResultEvent(call.Name, result);
                }

                // 4. 将工具结果回传给 LLM
                _history.Add(new ChatMessage(ChatRole.Tool, result, call.Id));
            }

            // 5. 回到循环顶部，让 LLM 看到工具结果后继续决策
        }
    }
}
```

---

## 5. Step 4：CLI 交互 — REPL 原型

> **目标：** 用 `Spectre.Console` 实现最简单的 REPL 循环，把 Agent 闭环跑通。

### 5.1 Program.cs 主入口

```csharp
using PuddingCode.Core;
using PuddingCode.Tools;
using Spectre.Console;

// 1. 初始化
AnsiConsole.Write(new FigletText("PuddingCode").Color(Color.Yellow));
AnsiConsole.MarkupLine("[grey]v0.1.0 - Agentic Self-Programming CLI[/]\n");

var options = new LlmOptions(
    Endpoint: Environment.GetEnvironmentVariable("PUDDING_API_ENDPOINT")
              ?? "https://api.openai.com/v1/chat/completions",
    ApiKey:   Environment.GetEnvironmentVariable("PUDDING_API_KEY")
              ?? throw new InvalidOperationException("Set PUDDING_API_KEY"),
    Model:    Environment.GetEnvironmentVariable("PUDDING_MODEL")
              ?? "gpt-4o");

var httpClient = new HttpClient();
var gateway = new OpenAiLlmGateway(httpClient, options);

var registry = new ToolRegistry();
registry.Register(new FileTool());
registry.Register(new ShellTool());

var agent = new AgentOrchestrator(gateway, registry);

// 2. REPL 主循环
while (true)
{
    var input = AnsiConsole.Prompt(
        new TextPrompt<string>("[bold yellow]Pudding >[/]")
            .AllowEmpty());

    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;

    await foreach (var evt in agent.ProcessAsync(input))
    {
        switch (evt)
        {
            case ThinkingEvent e:
                AnsiConsole.MarkupLine($"[grey italic]🍮 {e.Thought}[/]");
                break;
            case ToolCallEvent e:
                AnsiConsole.MarkupLine($"[blue]⚙️ Calling tool: {e.ToolName}[/]");
                break;
            case ToolResultEvent e:
                var panel = new Panel(e.Result.EscapeMarkup())
                    .Header($"[green]📂 {e.ToolName} result[/]")
                    .Border(BoxBorder.Rounded);
                AnsiConsole.Write(panel);
                break;
            case AnswerEvent e:
                AnsiConsole.MarkupLine($"\n[bold]{e.Content.EscapeMarkup()}[/]\n");
                break;
            case ErrorEvent e:
                AnsiConsole.MarkupLine($"[red]❌ {e.Message.EscapeMarkup()}[/]");
                break;
        }
    }
}

AnsiConsole.MarkupLine("[grey]Bye! 🍮[/]");
```

### 5.2 完整交互流程

```text
┌──────────────────────────────────────────────────────────────────┐
│  PuddingCode v0.1.0                                             │
├──────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Pudding > 帮我看一下 Program.cs 的内容                           │
│                                                                  │
│  🍮 Calling LLM...                                              │
│  ⚙️ Calling tool: file                                          │
│  ┌─ 📂 file result ─────────────────────────────────────────┐   │
│  │  namespace PuddingCodeCLI                                 │   │
│  │  {                                                        │   │
│  │      internal class Program { ... }                       │   │
│  │  }                                                        │   │
│  └───────────────────────────────────────────────────────────┘   │
│                                                                  │
│  这是一个标准的 .NET 控制台入口文件。目前只有 Hello World 输出...  │
│                                                                  │
│  Pudding > /exit                                                 │
│  Bye! 🍮                                                        │
└──────────────────────────────────────────────────────────────────┘
```

---

## 6. 项目结构规划

完成 V0.1 后，`Source/PuddingCode/` 的目录结构：

```text
Source/PuddingCode/
├── Abstractions/
│   ├── ITool.cs
│   ├── IToolRegistry.cs
│   ├── ILlmGateway.cs
│   └── IAgentOrchestrator.cs
├── Models/
│   ├── ChatMessage.cs
│   ├── LlmResponse.cs
│   ├── ToolCall.cs
│   ├── AgentEvent.cs
│   └── ToolParameterSchema.cs
├── Core/
│   ├── ToolRegistry.cs
│   ├── OpenAiLlmGateway.cs
│   └── AgentOrchestrator.cs
├── Tools/
│   ├── FileTool.cs
│   └── ShellTool.cs
└── PuddingCode.csproj
```

```text
Source/PuddingCodeCLI/
├── Program.cs              ← REPL 主循环
└── PuddingCodeCLI.csproj   ← 引用 PuddingCode.Core
```

---

## 7. 验收标准

V0.1 完成时，以下场景必须可用：

| # | 场景 | 预期结果 |
| --- | --- | --- |
| 1 | 启动 CLI | 显示 FigletText 标题 + 版本号，进入 `Pudding >` 提示 |
| 2 | 输入自然语言问题 | Agent 调用 LLM，返回文本回答 |
| 3 | 输入"读取 xxx 文件" | Agent 调用 `FileTool.read`，在 Panel 中显示文件内容，再给出总结 |
| 4 | 输入"执行 dotnet build" | Agent 调用 `ShellTool`，显示编译输出，再给出分析 |
| 5 | 输入 `/exit` | 正常退出 |
| 6 | 多轮对话 | Agent 保持上下文，能基于上一轮结果继续操作 |
| 7 | 工具执行失败 | 错误信息回传给 LLM，Agent 尝试自动修正或告知用户 |

---

## 8. 依赖清单

### PuddingCode.csproj（Core 类库）

| 包 | 用途 |
| --- | --- |
| `System.Text.Json` | JSON 序列化（框架内置，无需额外引用） |

> **设计决策：** V0.1 阶段 Core 类库零第三方依赖，仅使用 .NET 10 内置 API。降低复杂度，保持 AOT 兼容性。

### PuddingCodeCLI.csproj

| 包 | 用途 |
| --- | --- |
| `Spectre.Console` | TUI 渲染：FigletText、Panel、Markup、TextPrompt |

### 环境变量

| 变量 | 说明 | 示例 |
| --- | --- | --- |
| `PUDDING_API_KEY` | **必填** — LLM API 密钥 | `sk-xxx...` |
| `PUDDING_API_ENDPOINT` | 可选 — API 端点 | `https://api.openai.com/v1/chat/completions` |
| `PUDDING_MODEL` | 可选 — 模型名称 | `gpt-4o` / `claude-sonnet-4-20250514` / `deepseek-chat` |
