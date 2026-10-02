# Task 12 — 感官过滤设计方案 (Output Distillation)

> **状态：** ✏️ 设计中
> **依赖：** Task 10 (Agent 能力体系)、Task 11 (权限与安全沙盒)、D02 (ShellTool)
> **目标：** 为 Agent 的工具输出建立分级过滤管道，防止冗余日志淹没 LLM 上下文窗口，同时保证关键信息不丢失

---

## 目录

1. [设计原则](#一设计原则)
2. [三层数据流模型](#二三层数据流模型)
3. [过滤策略](#三过滤策略)
4. [三段式断路器](#四三段式断路器)
5. [自适应过滤规则](#五自适应过滤规则)
6. [异构模型压缩链](#六异构模型压缩链)
7. [LLM 感知技能](#七llm-感知技能)
8. [UI 双轨制显示](#八ui-双轨制显示)
9. [核心实现](#九核心实现)
10. [实现路线](#十实现路线)

---

## 一、设计原则

软件桥梁层在工具输出与 LLM 之间充当 **丘脑（Thalamus）**——过滤背景噪音，只传递关键信号。

| 原则 | 说明 |
|---|---|
| **数据在本地** | 原始日志 100% 写入磁盘，永不丢弃 |
| **精华给 AI** | LLM 只接收蒸馏后的结构化摘要 |
| **细节给用户** | 用户可随时穿透查看完整日志 |
| **失败增强** | 命令失败时自动切换到增强模式，附带错误上下文 |
| **自适应进化** | LLM 可动态定义过滤规则，系统在运行中学习 |

---

## 二、三层数据流模型

每次工具调用产生的输出进入三层分级管道：

```
CliWrap stdout/stderr
       │
       ▼
┌──────────────────────────────┐
│  L1: 原始层 (Raw)            │  100% 留存 → .log 文件
│  载体: 磁盘文件               │  LLM 无感，除非主动 read_full_log
└──────────────┬───────────────┘
               ▼
┌──────────────────────────────┐
│  L2: 蒸馏层 (Distilled)      │  按规则提取 → 内存 Buffer
│  载体: 结构化 JSON/Key-Value  │  LLM 接收蒸馏后数据
└──────────────┬───────────────┘
               ▼
┌──────────────────────────────┐
│  L3: 信号层 (Signal)         │  仅 ExitCode + Success/Fail
│  载体: 状态字                 │  极简感知，最节省 Token
└──────────────────────────────┘
```

### 数据流向三路分发

```
                    ┌──→  Disk     (完整日志文件)
CliWrap 输出 ───────┼──→  UI       (实时滚动的气泡预览)
                    └──→  Distiller (为下一次 LLM 请求准备摘要)
```

---

## 三、过滤策略

针对不同类型的工具输出，采用不同的蒸馏策略：

### 3.1 结构化蒸馏 (Structured Distillation)

**适用：** 具有固定格式的输出（编译错误、单元测试结果）

| 步骤 | 说明 |
|---|---|
| 解析 | 正则 / 解析器提取 Error Code、File Path、Line Number、Message |
| 反馈 | "编译失败。共 3 个错误。主要错误：`CS0103` 在 `AuthService.cs:12`" |
| 原始 | 完整构建日志保留在磁盘供用户查阅 |

**已知格式模板：**

| 工具 | 解析方式 | 提取字段 |
|---|---|---|
| `dotnet build` | MSBuild 格式：`file(line,col): error CODE: msg` | 文件、行号、错误码、消息 |
| `dotnet test` | TRX/控制台格式：`Passed/Failed/Skipped` 计数 | 通过数、失败数、失败用例名 |
| `npm install` | 包安装日志 + peer dependency 警告 | 安装数、警告数、错误原因 |
| `cargo build` | Rust 编译格式：`error[E0425]: ...` | 错误码、文件、行号 |
| `go build` | `file.go:line:col: msg` | 文件、行号、消息 |

### 3.2 滚动窗口截断 (Rolling Window Truncation)

**适用：** 流式输出（`npm install`、长日志、CI 管道）

保留 **首部**（启动信息）+ **尾部**（最终结果），中间省略：

```
[Output Truncated]
--- Head (5 lines) ---
Installing dependencies...
Resolving packages...
...
--- [中间 2000 行已隐藏] ---
...
--- Tail (10 lines) ---
added 42 packages in 12s
0 vulnerabilities
```

### 3.3 语义去重与折叠 (Semantic Folding)

**适用：** 重复性高的输出（进度条、重复警告）

| 原始 | 折叠后 |
|---|---|
| 450 行 `npm WARN peer dependency` | "已跳过 450 个重复的 peerDependency 警告" |
| 200 行 `Downloading ...` 进度 | "下载完成 (200 个文件)" |
| 80 行重复的 `Restoring packages...` | "NuGet 包还原完成 (80 packages)" |

### 3.4 失败增强模式 (Error Enhancement)

命令失败时，过滤策略自动升级：

```
正常 (ExitCode=0)        → L3 信号层足够
失败 (ExitCode≠0)        → 自动切换到增强模式
  → 智能扫描 Exception / Error / Failed / FATAL 关键词
  → 提取错误行及上下文 (前后各 3 行)
  → 附带到 LLM 反馈中
```

增强模式反馈示例：

```
[SYSTEM]: Command 'dotnet build' failed (exit code 1).
Errors (2):
  1. CS0103: The name 'authToken' does not exist — AuthService.cs:12
  2. CS0246: The type 'UserRole' could not be found — Models/User.cs:5
Context around error 1:
  10: var user = GetUser(id);
  11: var token = GenerateToken(user);
> 12: return authToken;  // ← CS0103
  13: }
```

---

## 四、三段式断路器

物理层面的硬性保护，防止上下文窗口被撑爆：

### 4.1 触发逻辑

```
输出行数
  │
  ├── ≤ 20 行    → 直接透传（不过滤）
  │
  ├── 21–100 行  → 硬截断（Head + Tail）
  │
  └── > 100 行   → 硬截断 + 错误检测
                    若含 Error/Fail → 启动结构化蒸馏或本地模型摘要
```

### 4.2 断路器配置

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `PassthroughLimit` | 20 | 行数 ≤ 此值直接透传 |
| `TruncationLimit` | 100 | 行数 > 此值触发截断 |
| `HeaderSize` | 5 | 截断时头部保留行数 |
| `FooterSize` | 10 | 截断时尾部保留行数 |
| `MaxLlmChars` | 4000 | 传递给 LLM 的最大字符数 |
| `ErrorScanKeywords` | `Error,Fail,Exception,FATAL` | 增强模式关键词 |

### 4.3 截断反馈模板

```
[SYSTEM]: Command output truncated (1250 lines total).
Showing first 5 and last 10 lines. Exit code: 0 (Success).
Use 'get_full_log' for complete output, or 'set_filter' to define extraction rules.
```

---

## 五、自适应过滤规则

LLM 可在运行中动态定义过滤规则，系统随任务进化。

### 5.1 LLM 驱动的规则设定

Agent 通过 `set_output_filter` 技能下发规则：

```
LLM: "请帮我监控 dotnet build，只提取包含 CS 开头的错误代码和所在文件名。"
→ set_output_filter(pattern: "^.*?(CS\\d+).*?([\\w.]+\\.cs).*$", mode: "extract")
```

桥梁层动态编译正则过滤器，后续同类命令自动应用。

### 5.2 软件主动探测

软件层检测到输出异常时，主动向 LLM 发起诊断请求：

```
[SYSTEM]: Detected 500 warning lines in 'npm install' output.
Current filter rules do not cover these. Options:
  1. Ignore warnings
  2. Define a rule to extract warning details
  3. Read first 50 lines for inspection
```

LLM 回复后，系统完成一次 **规则进化**。

### 5.3 Leader 的全局过滤器

Leader 可以管理过滤规则的 **经验库**：

- 当某个 Worker 反复调用 `get_full_log`，Leader 可调高该 Worker 的 `TruncationLimit`
- Leader 可预设项目级过滤规则，新 Worker 自动继承
- 过滤经验写入 `MEMORY.md`，跨会话复用

---

## 六、异构模型压缩链

引入 **分级算力** 概念——低成本本地模型作为感官前置处理器：

### 6.1 模型分工

```
┌──────────────────────────────────────────────────────────┐
│  L0: 物理层（正则/硬截断）                                 │
│  成本: 0   · 延迟: <1ms  · 处理: npm install 等极端冗余    │
├──────────────────────────────────────────────────────────┤
│  L1: 压缩层（本地小模型 Qwen 0.5B / Ollama）               │
│  成本: 0   · 延迟: ~100ms · 处理: 100+ 行报错 → 一句摘要    │
├──────────────────────────────────────────────────────────┤
│  L2: 决策层（GPT-4o / Claude / DeepSeek-R）                │
│  成本: 高  · 延迟: ~2s   · 处理: 仅经过压缩后的精炼上下文    │
└──────────────────────────────────────────────────────────┘
```

### 6.2 本地模型工作流

```
CliWrap 捕获 200 行报错
  → LocalDistiller (Qwen 0.5B, ONNX 内存运行)
  → Prompt: "总结这段报错的根本原因，一句话"
  → 输出: "第 42 行变量名拼写错误导致 CS0103"
  → 这段话被喂给 Worker Agent (GPT-4o)
```

**优势：**

| 维度 | 说明 |
|---|---|
| 即时性 | 无网络延迟，日志产生瞬间即开始流式摘要 |
| 隐私性 | 原始长日志完全在本地处理，只有摘要发往云端 |
| 成本 | 本地推理零费用，可节省约 85% 的 Token 开销 |

### 6.3 自适应触发

| 条件 | 处理方式 |
|---|---|
| 行数 ≤ 20 | 直接透传，不启动任何过滤 |
| 20 < 行数 ≤ 100 | 物理截断（Head + Tail） |
| 行数 > 100 且含 Error/Fail | 本地模型摘要 + 错误上下文提取 |
| 行数 > 100 且无错误 | 物理截断 + L3 信号层 |

### 6.4 模型配置

```json
{
  "distiller": {
    "provider": "local-ollama",
    "model": "qwen2.5-0.5b",
    "enabled": true,
    "fallback": "truncation",
    "trigger_threshold_lines": 100
  },
  "policy": {
    "auto_distill": true,
    "max_llm_chars": 4000,
    "passthrough_limit": 20,
    "truncation_limit": 100
  }
}
```

---

## 七、LLM 感知技能

配合 Task 10 的 SkillRegistry，注册以下感官过滤相关技能：

| 函数名 | 参数 | 描述 |
|---|---|---|
| `get_full_log` | `task_id` | 获取某次命令的完整原始输出（按需耗 Token） |
| `get_log_range` | `task_id`, `start`, `count` | 分页读取日志的指定行范围 |
| `set_output_filter` | `pattern`, `mode` | 动态设置正则过滤规则 |
| `clear_output_filter` | — | 清除当前过滤规则，恢复默认 |
| `get_output_stats` | `task_id` | 获取输出统计：总行数、错误数、警告数 |

---

## 八、UI 双轨制显示

### 8.1 气泡摘要（给用户的简报）

Agent 节点旁的气泡显示精简结果：

```
✅ dotnet build 成功 (0 errors, 2 warnings)
```

```
❌ npm install 失败 — 网络超时
```

### 8.2 穿透全文（点击展开）

- 气泡下方显示灰色标签：`[ 1250 行已精简 · 点击查看全文 ]`
- 点击后弹出深色终端窗口，可滚动查看完整日志
- 不同底色区分"LLM 看到的摘要版"与"完整版"

### 8.3 成本仪表

Swarm 节点下方显示实时 Token 消耗：

```
worker-auth: $0.02  │  已节省 ~85% (本地模型介入中 💠)
```

- 本地小模型工作时，节点周围显示淡蓝色微光
- 实时累计节省的 Token 数和美金

---

## 九、核心实现

### 9.1 IOutputDistiller 接口

```csharp
/// <summary>工具输出蒸馏器接口。</summary>
public interface IOutputDistiller
{
    /// <summary>蒸馏原始输出，返回适合传递给 LLM 的精简文本。</summary>
    DistillResult Distill(string rawOutput, DistillContext context);
}

public record DistillContext(
    string CommandName,
    int ExitCode,
    string? WorkingDirectory = null);

public record DistillResult(
    string Summary,
    int OriginalLines,
    int RetainedLines,
    bool IsTruncated,
    string? FullLogPath = null);
```

### 9.2 DefaultDistiller（物理截断 + 结构化蒸馏）

```csharp
public class DefaultDistiller : IOutputDistiller
{
    private readonly DistillerConfig _config;

    public DefaultDistiller(DistillerConfig? config = null)
        => _config = config ?? DistillerConfig.Default;

    public DistillResult Distill(string rawOutput, DistillContext context)
    {
        var lines = rawOutput.Split('\n');
        var totalLines = lines.Length;

        // 直接透传
        if (totalLines <= _config.PassthroughLimit)
            return new(rawOutput, totalLines, totalLines, false);

        // 失败时：增强模式
        if (context.ExitCode != 0)
            return DistillWithErrorEnhancement(lines, context);

        // 正常截断
        return TruncateHeadTail(lines, totalLines);
    }

    private DistillResult TruncateHeadTail(string[] lines, int total)
    {
        var head = lines.Take(_config.HeaderSize);
        var tail = lines.TakeLast(_config.FooterSize);
        var summary =
            $"{string.Join('\n', head)}\n" +
            $"\n... [{total - _config.HeaderSize - _config.FooterSize} lines truncated] ...\n\n" +
            string.Join('\n', tail);

        return new(summary, total, _config.HeaderSize + _config.FooterSize, true);
    }

    private DistillResult DistillWithErrorEnhancement(string[] lines, DistillContext context)
    {
        var errors = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!ContainsErrorKeyword(lines[i])) continue;

            // 提取错误行及上下文 (前后各 3 行)
            var start = Math.Max(0, i - 3);
            var end = Math.Min(lines.Length - 1, i + 3);
            var snippet = string.Join('\n',
                lines[start..(end + 1)]
                    .Select((l, idx) => idx + start == i ? $"> {l}" : $"  {l}"));
            errors.Add(snippet);

            if (errors.Count >= 5) break; // 最多 5 个错误片段
        }

        var summary =
            $"[FAILED] Exit code: {context.ExitCode}. " +
            $"Found {errors.Count} error(s) in {lines.Length} lines.\n\n" +
            string.Join("\n---\n", errors);

        if (summary.Length > _config.MaxLlmChars)
            summary = summary[.._config.MaxLlmChars] + "\n[...truncated]";

        return new(summary, lines.Length, errors.Count * 7, true);
    }

    private static bool ContainsErrorKeyword(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("FATAL", StringComparison.OrdinalIgnoreCase);
}
```

### 9.3 DistillerConfig

```csharp
public record DistillerConfig(
    int PassthroughLimit = 20,
    int TruncationLimit = 100,
    int HeaderSize = 5,
    int FooterSize = 10,
    int MaxLlmChars = 4000,
    string[] ErrorKeywords = null!)
{
    public static DistillerConfig Default => new()
    {
        ErrorKeywords = ["error", "fail", "exception", "FATAL"]
    };
}
```

### 9.4 LocalLlmDistiller（本地小模型，远期）

```csharp
/// <summary>利用本地小模型 (Qwen 0.5B / ONNX) 对长输出进行语义摘要。</summary>
public class LocalLlmDistiller : IOutputDistiller
{
    private readonly ILocalLlmRunner _runner; // ONNX Runtime / llama.cpp 绑定
    private readonly DefaultDistiller _fallback = new();

    public LocalLlmDistiller(ILocalLlmRunner runner) => _runner = runner;

    public DistillResult Distill(string rawOutput, DistillContext context)
    {
        var lines = rawOutput.Split('\n');

        // 行数不够多，回退到物理截断
        if (lines.Length <= 100)
            return _fallback.Distill(rawOutput, context);

        // 本地模型摘要
        var prompt = $"Summarize the following command output in one paragraph. " +
                     $"Focus on errors and key results:\n\n{rawOutput[..Math.Min(rawOutput.Length, 8000)]}";
        var summary = _runner.Generate(prompt, maxTokens: 200);

        return new(
            $"[Local Model Summary]\n{summary}",
            lines.Length, 0, true);
    }
}
```

### 9.5 集成到 ShellTool 的调用链

```
ShellTool.ExecuteAsync
  → PermissionGuard.ValidateCommand   (Task 11)
  → CliWrap 执行
  → 原始输出 → 写入 .log 文件        (L1: 原始层)
  → IOutputDistiller.Distill          (L2: 蒸馏层)
  → 返回 DistillResult.Summary 给 LLM (L3: 信号层)
```

---

## 十、实现路线

### ✅ 已完成

- `ShellTool` 基础实现（无过滤，全量返回）
- CliWrap 集成

### 🚧 下一步

| 优先级 | 任务 | 说明 |
|---|---|---|
| **P0** | `IOutputDistiller` 接口 + `DefaultDistiller` | 物理截断 + 错误增强模式 |
| **P0** | 集成到 `ShellTool` | 在 `ExecuteAsync` 返回前插入 Distill 管道 |
| **P0** | `.log` 文件持久化 | 原始输出流式写入磁盘，DistillResult 携带 logPath |
| **P1** | 结构化蒸馏模板 | `dotnet build` / `dotnet test` / `npm` 专用解析器 |
| **P1** | `get_full_log` / `set_output_filter` 技能 | LLM 按需调阅全文 + 动态规则 |
| **P1** | 语义折叠 | 重复行检测与合并 |
| **P2** | UI 双轨制 | 气泡摘要 + 点击穿透全文终端窗口 |
| **P2** | 成本仪表 | 每个 Agent 节点显示 Token 消耗与节省比例 |
| **P3** | `LocalLlmDistiller` | 本地 Qwen 0.5B (ONNX) 语义摘要 |
| **P3** | Leader 全局过滤器经验库 | 过滤规则写入 MEMORY.md，新 Worker 自动继承 |
