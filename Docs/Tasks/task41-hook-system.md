# task41 — Hook 系统设计

> **创建日期：** 2026-05-03
> **优先级：** P1.5（V1 基础框架，V1.5 完善）
> **状态：** ✏️ 设计中
> **依赖：** task10 (Agent 能力体系)、task11 (权限沙盒)
> **参考：** [Claude Code EP05 Hook System](../../Docs/claude-reviews-claude/architecture/05-hook-system.md) — 20 种事件 Hook、PreToolUse/PostToolUse 拦截

---

## 任务目标

设计并实现 Pudding Agent 的 Hook 系统——让用户和插件可以在 Agent 生命周期的关键节点插入自定义逻辑。V1 实现核心的 PreToolUse/PostToolUse Hook，V1.5 扩展到完整 20 种事件。

## 参考设计：20 种事件 Hook

借鉴 Claude Code 的 Hook 系统（5,023 行实现代码）：

### 核心 Hook: PreToolUse

在每次工具执行前触发。可以：

| 动作 | 效果 |
|------|------|
| **Allow** | 跳过权限提示，自动批准 |
| **Deny** | 阻止工具执行，返回错误给 LLM |
| **Ask** | 强制触发权限提示（即使用户设置了 always-allow） |
| **Modify Input** | 修改工具参数后再执行 |
| **Stop Session** | 终止整个 Agent 循环 |

### 完整事件列表（20 种）

| 类别 | 事件 | 说明 |
|------|------|------|
| **工具** | `PreToolUse` | 工具执行前 |
| | `PostToolUse` | 工具执行后 |
| | `PostToolUseFailure` | 工具执行失败后 |
| **会话** | `SessionStart` | 会话开始 |
| | `SessionEnd` | 会话结束 |
| | `UserPromptSubmit` | 用户提交消息 |
| | `PreCompact` | 压缩前（可在压缩前将关键信息写入记忆） |
| **通知** | `Notification` | 通用通知（观察，不阻塞） |
| | `Stop` | Agent 停止 |
| | `SubagentStart` / `SubagentStop` | 子代理启停 |
| **权限** | `PermissionRequest` | 权限请求 |
| **LLM** | `PreApiCall` / `PostApiCall` | API 调用前后 |
| **其他** | `FileChange` | 文件变更 |
| | `Checkpoint` | 检查点 |

### 4 种 Hook 执行类型

| 类型 | 实现方式 | Pudding 对应 |
|------|---------|-------------|
| **Command** | Shell 脚本 | `Process.Start()` 执行外部程序 |
| **HTTP** | Webhook 端点 | `HttpClient` POST 到外部 URL |
| **Agent** | 子代理作为 Hook | 子代理执行 |
| **Function** | 进程内回调 | C# `Func<T>` / `Action<T>` 委托 |

### Hook 匹配器

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "Bash(git *)", "command": "python validate_git.py" },
      { "matcher": "FileWrite", "command": "node check_secrets.js" },
      { "matcher": "*", "command": "python audit_all.py" }
    ]
  }
}
```

匹配语法：`ToolName`（完全匹配）、`ToolName(pattern)`（参数模式匹配）、`*`（通配）。

### 退出码约定

| 退出码 | 含义 |
|--------|------|
| 0 | 成功——正常继续 |
| 1 | 非阻断错误——记录日志，继续 |
| **2** | **阻断错误——停止工具执行** |
| 其他 | 视为非阻断错误 |

## Pudding 具体实现方案

### V1 范围（最小可行）

只实现最关键的 3 个 Hook：

| Hook | 理由 |
|------|------|
| `PreToolUse` | 安全控制核心——拦截/修改/阻止工具调用 |
| `PostToolUse` | 审计、日志、结果后处理 |
| `PostToolUseFailure` | 错误处理和重试逻辑 |

V1 只支持 **Function 类型**（C# 进程内回调），通过 DI 注册。

### 接口设计

```csharp
/// <summary>
/// Hook 上下文——携带触发 Hook 时的全部信息
/// </summary>
public class HookContext
{
    public string HookEvent { get; init; }       // "PreToolUse"
    public string ToolName { get; init; }        // "FileWrite"
    public string? ToolInput { get; init; }      // JSON 参数
    public string AgentId { get; init; }
    public string SessionId { get; init; }
}

/// <summary>
/// Hook 执行结果
/// </summary>
public class HookResult
{
    public HookDecision Decision { get; init; } = HookDecision.Allow;
    public string? ModifiedInput { get; init; }  // Modify Input 时提供
    public string? Message { get; init; }        // 给 LLM 的反馈消息
}

public enum HookDecision
{
    Allow,       // 正常允许
    Deny,        // 阻止（返回错误给 LLM）
    Ask,         // 强制触发用户确认
    ModifyInput, // 修改参数后继续
    StopSession, // 终止会话
}

/// <summary>
/// V1 Hook 接口
/// </summary>
public interface IPuddingHook
{
    string Name { get; }
    string Event { get; }        // "PreToolUse" | "PostToolUse" | "PostToolUseFailure"
    string? Matcher { get; }     // "FileWrite" | "Bash(git *)" | "*" | null = match all
    
    Task<HookResult> ExecuteAsync(HookContext context, CancellationToken ct);
}
```

### DI 注册与调用链

```csharp
// 注册多个 Hook
services.AddSingleton<IPuddingHook, GitValidationHook>();
services.AddSingleton<IPuddingHook, SecretDetectionHook>();
services.AddSingleton<IPuddingHook, AuditLoggingHook>();

// HookManager 负责匹配和调度
public class HookManager
{
    private readonly IEnumerable<IPuddingHook> _hooks;
    
    public async Task<HookResult> ExecutePreToolUseHooks(
        string toolName, string toolInput, HookContext context)
    {
        // 按注册顺序执行匹配的 Hook
        // 第一个 Deny/StopSession 结果立即返回（短路）
        // ModifyInput 结果传递给后续 Hook
        var currentInput = toolInput;
        foreach (var hook in _hooks.Where(h => Matches(h, toolName)))
        {
            var result = await hook.ExecuteAsync(context, ct);
            switch (result.Decision)
            {
                case HookDecision.Deny:
                case HookDecision.StopSession:
                    return result; // 短路
                case HookDecision.ModifyInput:
                    currentInput = result.ModifiedInput ?? currentInput;
                    break;
            }
        }
        return new HookResult { Decision = HookDecision.Allow };
    }
    
    private bool Matches(IPuddingHook hook, string toolName)
        => hook.Matcher == null || hook.Matcher == "*" || hook.Matcher == toolName;
}
```

## 实现步骤

1. **Hook 接口定义** — `IPuddingHook`、`HookContext`、`HookResult`（PuddingCore）
2. **HookManager** — Hook 注册、匹配、调度、短路执行
3. **PreToolUse 集成** — 在权限检查前插入 PreToolUse Hook 调用链
4. **PostToolUse 集成** — 在工具执行后触发 PostToolUse Hook
5. **3 个内置 Hook** — Git 校验 Hook、密钥检测 Hook、审计日志 Hook 作为示例

## 验收标准

1. PreToolUse Hook 可在工具执行前拦截并阻止（Deny）
2. PostToolUse Hook 可记录每次工具调用结果（审计日志）
3. ModifyInput Hook 可修改工具参数（如自动补充文件路径前缀）
4. Hook 按注册顺序依次执行，Deny/StopSession 结果正确短路
5. Hook 异常不传播到 Agent 主循环（记录日志后继续）

## 不做（V1）

- Command/HTTP/Agent 类型 Hook（V1 仅 Function 类型）
- PostToolUseFailure 以外的 Failure Hook（V1.5）
- Hook 的 MCP 传输（V2）
- 完整的 20 种事件（V1 仅 PreToolUse + PostToolUse + PostToolUseFailure）
- Hook 的 YAML 配置加载（V1 仅 C# DI 注册，V1.5 支持配置文件）
