# Task 01: 交互界面设计方案

> **⚠️ 定位变更：** 项目已从"Agentic Self-Programming CLI"重新定位为面向大众的**数字化布丁管家**。CLI 交互界面现作为 **Pro Mode（专业形态）** 保留，主交互入口转为桌面精灵 + 管理窗口的双态架构。最新设计见 [task18定位.md](../Tasks/task18定位.md)。
>
> **目标：** 开发一个类似 OpenCode、Claude Code、Crush 的 **Agentic Self-Programming CLI**，作为 PuddingCode 的 Pro Mode。基于 `Spectre.Console`（C# / .NET 10），打造功能强大且具有独特"布丁感"的终端交互体验。

---

## 目录

1. [界面视觉语言](#1-界面视觉语言-the-look)
2. [布局设计](#2-布局设计-layout)
3. [核心交互特性](#3-核心交互特性-key-features)
4. [斜杠指令系统](#4-斜杠指令系统-slash-commands)
5. [架构支撑层](#5-架构支撑层-architecture)
6. [伪代码参考](#6-伪代码参考)
7. [实现路线图](#7-实现路线图)

---

## 1. 界面视觉语言 (The Look)

使用 `Spectre.Console` 在 C# 中复刻类似 **Crush** 的高级感。

### 1.1 配色方案 (Palette)

| 用途 | 色值 | 说明 |
| --- | --- | --- |
| **主色调** | 抹茶绿 `#A8E6CF` / 暖布丁黄 `#FFD3B6` | 品牌视觉标识 |
| **警告色** | 珊瑚红 `#FF8B94` | 错误、危险操作提示 |
| **代码高亮** | 紫色（关键路径） | 遵循终端原生主题，关键路径额外高亮 |

### 1.2 排版 (Typography)

- 等宽字体推荐：Monaco / JetBrains Mono
- 启动标题使用 ASCII Art 的 **Pudding** FigletText

### 1.3 交互动效

- 模拟"软糯"感：任务进度条不仅平滑移动，还带有微小弹动（Wobble）效果
- Agent 思考时使用 `Spinner.Known.Dots` 动画
- 状态切换时使用渐变色过渡

---

## 2. 布局设计 (Layout)

参考 Claude Code 的极简与 OpenCode 的面板化，采用 **"单列流式 + 弹出式侧边栏"** 布局。

```text
┌─────────────────────────────────────────────────────────┐
│ [🛡️ SAFE]  Model: claude-4.6  Branch: main  $0.03      │  ← 状态仪表盘
├─────────────────────────────────────────────────────────┤
│                                                         │
│  🍮 Pudding is thinking:                                │  ← 思维流
│     "I should check AuthService.cs first..."            │
│                                                         │
│  ┌─ 📂 Read File ─────────────────────────────────┐    │  ← 行动卡片
│  │  AuthService.cs (lines 1-42)                    │    │
│  └─────────────────────────────────────────────────┘    │
│                                                         │
│  ┌─ 📝 Diff ──────────────────────────────────────┐    │  ← Diff 预览
│  │  - var token = GenerateToken(user);             │    │
│  │  + var token = await GenerateTokenAsync(user);  │    │
│  │  [Y] 批准  [N] 拒绝  [E] 微调  [?] 解释       │    │
│  └─────────────────────────────────────────────────┘    │
│                                                         │
├─────────────────────────────────────────────────────────┤
│ Pudding >                                               │  ← Prompt 输入栏
└─────────────────────────────────────────────────────────┘
```

### 2.1 核心会话流 (The Main Stream)

| 组件 | 描述 |
| --- | --- |
| **Prompt 栏** | 底部常驻，格式 `Pudding >`，支持斜杠命令输入 |
| **思维流 (Thought Stream)** | 低饱和度灰色字体，实时展示 Agent 思考逻辑，增加透明度 |
| **行动卡片 (Action Cards)** | 每个工具调用（读文件、运行命令）渲染为独立卡片，替代杂乱日志 |

### 2.2 状态仪表盘 (Dashboard)

终端顶部一行空间，显示关键运行时信息：

- **模式标识：** `[🛡️ SAFE]` 安全模式 / `[🔥 YOLO]` 免确认模式
- **资源消耗：** 当前任务已耗 Token 成本（美元显示）
- **环境信息：** 当前 Git 分支、.NET 版本、当前使用的 AI 模型

---

## 3. 核心交互特性 (Key Features)

### 3.1 任务进度可视化

借鉴 OpenCode，将 Agent 执行过程渲染为步骤图：

```text
[✓] 1. 分析代码依赖
[→] 2. 生成补丁 (Pudding is typing...)
[ ] 3. 运行回归测试
```

- `[✓]` 已完成（绿色）
- `[→]` 进行中（黄色 + 动画）
- `[ ]` 待执行（灰色）

### 3.2 差异预览与审批 (Diff View)

当 Agent 准备修改代码时，在终端直接渲染美观的 Diff 视图：

- 红色代表删除行，绿色代表新增行
- 底部提供交互选项：

| 快捷键 | 操作 |
| --- | --- |
| `Y` | 批准修改 |
| `N` | 拒绝修改 |
| `E` | 手动微调（进入编辑模式） |
| `?` | 让 Agent 解释修改理由 |

### 3.3 指令自动补全 (Autocomplete)

当用户输入 `/` 时，CLI 实时弹出可选指令菜单：

- 使用 `Spectre.Console` 的 `SelectionPrompt` 或 `Console.ReadKey` 监听
- 支持模糊匹配，如输入 `/mo` 自动联想 `/model`、`/mood`

### 3.4 模型状态动态显示

切换模型后，状态仪表盘同步更新，并给出费用预估反馈：

```text
[Pudding] > /model o3-mini
(OK!) 🍮 已切换到高效模式，当前费用预计降低 60%。
```

---

## 4. 斜杠指令系统 (Slash Commands)

### 4.1 完整指令表

| 指令 | 功能 | 视觉反馈 |
| --- | --- | --- |
| `/model [name]` | **切换大模型** | 弹出选择列表（Claude 4.6, DeepSeek, GPT-5），选中后状态栏更新 |
| `/map` | **项目架构雷达** | 基于 LSP 生成架构树，高亮 Agent 已感知的关键节点 |
| `/review [--fix]` | **代码审查** | 子 Agent 寻找潜在 Bug 或坏味道，侧边栏显示"气泡建议"；`--fix` 链式自动修复 |
| `/undo` | **时光回滚** | 利用 Git 快照一键撤销 Agent 最近一轮的所有代码改动 |
| `/yolo` | **解禁模式** | 切换到免确认模式，适合信任 Agent 进行批量重构 |
| `/compact` | **压缩记忆** | 对当前会话进行"蒸馏"，只保留关键计划和结果，节省 Token |
| `/config` | **偏好设置** | 交互式配置：API 密钥、沙盒权限、Agent 人格风格 |
| `/mood [style]` | **心情切换** | `professional`：严肃模式，纯代码输出；`pudding`：软萌模式，带 Emoji 和趣味反馈 |

### 4.2 指令增强特性

#### 指令组合 (Command Chaining)

支持参数化链式操作：

```text
/review --fix     # 先审查，发现问题后自动进入修复模式
/model claude-4.6 # 直接指定模型，跳过选择菜单
```

#### 指令处理器设计

所有指令通过统一的 `CommandDispatcher` 分发处理，遵循"输入 → 解析 → 执行 → 反馈"四步流程。

---

## 5. 架构支撑层 (Architecture)

交互界面的实现依赖以下核心抽象，确保 UI 层与业务逻辑解耦。

### 5.1 能力抽象 (`IAbility`)

将 Agent 的所有行为抽象为"能力"接口，支持在真实环境与沙盒之间无缝切换：

| 接口 | 职责 | 示例 |
| --- | --- | --- |
| `ISenseAbility` | 感知（眼睛） | 获取 LSP 信息、搜索文件、读取代码 |
| `IActAbility` | 执行（手） | 写入代码、执行 Shell、提交 Git |

### 5.2 编排器 (`IOrchestrator`)

Agent 的"前额叶"，负责处理 OpenAI 协议的连续会话，管理生命周期状态机：

```text
Idle → Thinking → Acting → Verifying → Idle
```

### 5.3 关键架构模式

| 模式 | 说明 |
| --- | --- |
| **工具注册 (Registry)** | `PuddingToolRegistry` 通过反射动态加载工具，新工具只需贴特性即可被 Agent 识别 |
| **上下文隔离 (Session Factory)** | 每个任务由独立 Session 实例处理，避免多任务间的上下文污染 |
| **插件化 (MCP)** | 本地工具也通过 MCP 协议通信，支持接入第三方 MCP Server（查文档、操作浏览器等） |

### 5.4 架构感知能力

- 在项目根目录维护 `.pudding/arch.json` 架构清单
- 记录技术栈、分层约定（如 Controller 只能调用 Service）
- Agent 动手前必须先读取架构清单，确保生成的代码不违背既定架构

---

## 6. 伪代码参考

### 6.1 UI 渲染主类

```csharp
using Spectre.Console;

public class PuddingUI
{
    public void RenderHeader()
    {
        AnsiConsole.Write(new FigletText("PuddingCode").Color(Color.Yellow));
        AnsiConsole.MarkupLine("[grey]v0.1.0 - Agentic Self-Programming CLI[/]");
    }

    public void RenderTaskPanel(List<TaskStep> steps)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("步骤");
        table.AddColumn("状态");

        foreach (var step in steps)
        {
            var status = step.State switch
            {
                TaskState.Done       => "[green]✓ Done[/]",
                TaskState.InProgress => "[yellow]→ Cooking...[/]",
                _                    => "[grey]  Pending[/]"
            };
            table.AddRow(step.Name, status);
        }

        AnsiConsole.Write(table);
    }

    public async Task StartAgentLoop()
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在唤醒布丁...", async ctx =>
            {
                // Agent 初始化逻辑
            });
    }
}
```

### 6.2 指令处理器

```csharp
public class CommandDispatcher
{
    public async Task HandleAsync(string input)
    {
        if (!input.StartsWith("/"))
            return;

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLower();
        var args = parts.Length > 1 ? parts[1..] : [];

        switch (cmd)
        {
            case "/model":
                var model = args.Length > 0 ? args[0] : ShowModelPicker();
                await SetModelAsync(model);
                AnsiConsole.MarkupLine($"[bold green]🍮 已切换到 {model}[/]");
                break;

            case "/undo":
                await GitManager.RollbackLastCommitAsync();
                AnsiConsole.MarkupLine("[bold green]✅ 撤销成功！代码已恢复到改动前。[/]");
                break;

            case "/yolo":
                ToggleYoloMode();
                break;

            case "/compact":
                await CompactSessionAsync();
                break;

            case "/map":
                await RenderProjectMapAsync();
                break;

            case "/review":
                var autoFix = args.Contains("--fix");
                await RunCodeReviewAsync(autoFix);
                break;

            case "/config":
                await ShowConfigWizardAsync();
                break;

            case "/mood":
                var style = args.Length > 0 ? args[0] : "pudding";
                SetMoodStyle(style);
                break;

            default:
                AnsiConsole.MarkupLine($"[red]未知指令: {cmd}[/]");
                break;
        }
    }

    private string ShowModelPicker()
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("选择模型：")
                .AddChoices("claude-4.6", "deepseek-r1", "gpt-5", "o3-mini"));
    }
}
```

### 6.3 能力抽象

```csharp
// 感知能力：Agent 的"眼睛"
public interface ISenseAbility
{
    Task<string> ReadFileAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolInfo>> GetSymbolsAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SearchFilesAsync(string pattern, CancellationToken ct = default);
}

// 执行能力：Agent 的"手"
public interface IActAbility
{
    Task WriteFileAsync(string path, string content, CancellationToken ct = default);
    Task<ExecutionResult> RunShellAsync(string command, CancellationToken ct = default);
    Task GitCommitAsync(string message, CancellationToken ct = default);
}

// Agent 核心：只依赖抽象，不依赖具体实现
public class PuddingAgent(ISenseAbility sense, IActAbility act, IOrchestrator orchestrator)
{
    public async Task SolveIssueAsync(string description, CancellationToken ct = default)
    {
        // Idle → Thinking → Acting → Verifying → Idle
    }
}
```

---

## 7. 实现路线图

| 阶段 | 版本 | 交互界面相关目标 |
| --- | --- | --- |
| **奠基** | V0.1 | Spectre.Console 基础框架搭建，Prompt 输入、FigletText 标题、基础 Table 渲染 |
| **交互成型** | V0.3 | 思维流展示、行动卡片渲染、状态仪表盘、基础斜杠指令（`/model`、`/undo`、`/yolo`） |
| **抽象层构建** | V0.5 | 引入 `IAbility` 能力抽象、`IOrchestrator` 编排器、工具注册中心、Session 隔离 |
| **完整体验** | V0.8 | Diff 预览与审批流程、指令自动补全、指令链式操作、`/mood` 切换、Token 消耗统计 |
| **精打细磨** | V1.0 | 弹动动效、配色主题切换、`.pudding/arch.json` 架构感知、MCP 插件化集成 |

---

## 附录：技术依赖

| 依赖 | 用途 |
| --- | --- |
| [`Spectre.Console`](https://github.com/spectreconsole/spectre.console) | TUI 渲染：进度条、表格、富文本面板、Status 动画 |
| [`Terminal.Gui`](https://github.com/gui-cs/Terminal.Gui) | 备选方案：复杂交互式 TUI（侧边栏、文件树等场景） |
| [`Semantic Kernel`](https://github.com/microsoft/semantic-kernel) | Agent 编排：Tool Calling、任务规划、模型路由 |
