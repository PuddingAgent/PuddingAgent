# Task 16 — 服务商与模型管理设计方案

> **状态：** ✏️ 设计中
> **依赖：** Task 12 (感官过滤)、Task 13 (上下文预热)、D03 (LLM 网关)
> **目标：** 构建 AI 资产调度中心——管理多服务商/多模型的元数据、能力标签、成本预算和动态路由，支撑模型异构调度

---

## 目录

1. [设计原则](#一设计原则)
2. [模型元数据结构](#二模型元数据结构)
3. [能力标签系统](#三能力标签系统)
4. [成本预估与预算控制](#四成本预估与预算控制)
5. [动态路由决策](#五动态路由决策)
6. [抽样评分机制](#六抽样评分机制)
7. [UI 交互设计](#七ui-交互设计)
8. [核心实现](#八核心实现)
9. [与现有架构集成](#九与现有架构集成)
10. [实现路线](#十实现路线)

---

## 一、设计原则

| 原则 | 说明 |
|---|---|
| **模型异构** | 大模型决策 + 小模型蒸馏，不同角色用不同模型 |
| **数据驱动** | 基于真实项目表现评分，而非仅依赖官方 Benchmark |
| **成本可控** | 实时计费 + 每日限额熔断 + 自动降级 |
| **自适应进化** | 历史表现自动更新能力标签，Leader 据此做精准匹配 |
| **兼容现有** | 扩展现有 `LlmOptions` / `ILlmGateway`，不破坏已有接口 |

---

## 二、模型元数据结构

### 2.1 ModelMetadata 定义

```csharp
public record ModelMetadata
{
    // ── 连接信息 ──
    public required string ProviderId { get; init; }     // "openai" / "anthropic" / "ollama"
    public required string ModelId { get; init; }        // "gpt-4o" / "qwen2.5-0.5b"
    public required string DisplayName { get; init; }    // "GPT-4o"
    public required string BaseUrl { get; init; }        // "https://api.openai.com/v1"
    public string? ApiKey { get; init; }                 // null 表示本地模型

    // ── Token 计费 ──
    public decimal PriceInputPer1M { get; init; }        // 输入价格 ($/1M tokens)
    public decimal PriceOutputPer1M { get; init; }       // 输出价格
    public bool IsLocal { get; init; }                   // 本地模型 = true

    // ── 窗口限制 ──
    public int ContextWindow { get; init; }              // 上下文窗口 (tokens)
    public int MaxOutputTokens { get; init; }            // 最大输出

    // ── 能力 ──
    public ModelModality[] Modalities { get; init; } = [ModelModality.Text];
    public HashSet<string> CapabilityTags { get; init; } = [];
    public HashSet<string> ExpertiseTags { get; init; } = [];

    // ── 运行时评分 ──
    public ModelPerformanceScore Performance { get; init; } = new();

    // ── 预算 ──
    public decimal? DailyBudgetLimit { get; init; }      // 每日限额 (美元)
}

public enum ModelModality { Text, Image, Audio, Video }
```

### 2.2 字段用途映射

| 字段类别 | 消费者 | 用途 |
|---|---|---|
| **连接信息** | `ILlmGateway` | 物理连接服务商 |
| **Token 计费** | 成本仪表 / 熔断器 | 实时计费 + 限额控制 |
| **窗口限制** | 感官过滤 (Task 12) | 触发截断/长上下文警告 |
| **模态** | Leader 路由 | 判断是否能处理图片/多媒体任务 |
| **能力标签** | Leader 路由 | 自动选择最优模型 |
| **评分** | 动态路由 | 基于历史表现排序 |

---

## 三、能力标签系统

### 3.1 能力标签 (Capability Tags)

描述模型的技术能力：

| 标签 | 含义 | 典型模型 |
|---|---|---|
| `FunctionCalling` | 稳定处理 Tool Calling / MCP 指令 | GPT-4o, Claude 3.5 |
| `LongContext` | 擅长超长上下文 (>100K tokens) | Claude 3.5, Gemini 1.5 |
| `Reasoning` | 强逻辑推理，适合做 Leader | GPT-4o, DeepSeek-R1 |
| `Streaming` | 支持流式输出 | 大部分云端模型 |
| `Distill` | 专为摘要/压缩优化 | Qwen 0.5B, Phi-3 Mini |
| `Vision` | 支持图像输入 | GPT-4o, Claude 3.5 |
| `LocalInference` | 可本地内存运行 | Qwen 0.5B, Llama 3.2 |

### 3.2 专家标签 (Expertise Tags)

描述模型在特定任务上的表现：

| 标签 | 含义 |
|---|---|
| `Code-Generation` | C# / Rust / Python 代码生成 |
| `Code-Review` | 能发现细微逻辑漏洞 |
| `Text-Summarization` | 极适合做 Distiller |
| `Refactoring` | 擅长大规模重构 |
| `Documentation` | API 文档 / README 生成 |
| `Debugging` | 错误诊断与修复 |

### 3.3 自适应标签更新

基于历史表现自动更新标签：

```
模型 A 最近 5 次 FunctionCalling 失败率 > 40%
  → 自动添加 [Weak-FunctionCalling] 标签
  → Leader 下次避开该模型执行复杂插件任务

模型 B 在 Refactoring 任务中连续 3 次得高分
  → 自动添加 [Strong-Refactoring] 标签
  → Leader 优先选择该模型做重构
```

---

## 四、成本预估与预算控制

### 4.1 实时计费

每个 Agent 节点实时追踪 Token 消耗：

```
累计消耗 = Σ (input_tokens × price_input + output_tokens × price_output)
```

### 4.2 成本试算（模拟器模式）

用户输入任务描述，系统预估成本：

```
输入: "重构支付模块"
预估:
  Leader (GPT-4o):  ~5K tokens  → $0.025
  Worker ×2:        ~20K tokens → $0.10
  Distiller (本地):  0           → $0.00
  ──────────────────────────────
  预计总消耗: $0.12 - $0.25
```

### 4.3 熔断机制

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `DailyBudgetLimit` | 无限制 | 单个 Provider 每日限额 |
| `TaskBudgetLimit` | 无限制 | 单次任务限额 |
| `AutoDowngrade` | true | 超限后自动降级到本地/廉价模型 |
| `PauseOnBudgetExhaust` | false | 超限后暂停任务（等待用户确认） |

熔断逻辑：

```
当前消耗 > DailyBudgetLimit × 80%  → 黄色警告
当前消耗 > DailyBudgetLimit        → 触发熔断
  → AutoDowngrade = true  → 自动切换到本地模型
  → PauseOnBudgetExhaust  → 暂停并通知用户
```

---

## 五、动态路由决策

### 5.1 路由流程

Leader 派发任务时，`ModelRegistry` 提供最优模型推荐：

```
Leader: "谁最擅长处理这个重构任务？"
  │
  ├─→ ModelRegistry.Query(task: "refactoring", capabilities: ["FunctionCalling"])
  │
  ├─→ 过滤: 具备 FunctionCalling + Code-Generation 标签
  ├─→ 排序: Performance.CodeQuality 降序
  ├─→ 权衡: 价格 × 质量评分 = 性价比
  │
  └─→ 推荐: "DeepSeek-V3 (性价比最优) 或 GPT-4o (质量最优)"
```

### 5.2 角色-模型映射建议

| Agent 角色 | 推荐能力 | 典型模型 |
|---|---|---|
| **Leader** | Reasoning, LongContext | GPT-4o, Claude 3.5 Sonnet |
| **Coder** | Code-Generation, FunctionCalling | DeepSeek-V3, GPT-4o |
| **Reviewer** | Code-Review, Reasoning | Claude 3.5, GPT-4o |
| **Distiller** | Text-Summarization, LocalInference | Qwen 0.5B, Phi-3 Mini |
| **Tester** | Code-Generation, Debugging | DeepSeek-V3, GPT-4o Mini |

---

## 六、抽样评分机制

### 6.1 触发策略

为节省 Token，不是每轮都评分：

| 触发条件 | 说明 |
|---|---|
| 随机抽样 10-20% | 正常任务的基准评估频率 |
| 任务复杂度 > 阈值 | 复杂任务强制评估 |
| 模型首次执行某类任务 | 建立基线评分 |
| 编译/测试失败 | 自动触发评估 |

### 6.2 评分维度

| 维度 | 评分方式 | 说明 |
|---|---|---|
| **指令遵循度** | Leader 主观评分 (0-10) | 是否正确生成插件参数 |
| **代码质量** | 软件客观检测 | 编译是否通过、测试是否成功 |
| **推理深度** | Leader 主观评分 (0-10) | 是否解决核心逻辑问题 |
| **响应速度** | 软件计时 | 首 Token 延迟、总耗时 |
| **Token 效率** | 软件统计 | 输出 Token 数 / 有效代码行数 |

### 6.3 ModelPerformanceScore

```csharp
public record ModelPerformanceScore
{
    public double Compliance { get; init; }     // 指令遵循度 (0-10)
    public double CodeQuality { get; init; }    // 代码质量 (0-10)
    public double Reasoning { get; init; }      // 推理深度 (0-10)
    public double AvgLatencyMs { get; init; }   // 平均延迟 (ms)
    public double TokenEfficiency { get; init; } // Token 效率
    public int SampleCount { get; init; }       // 已采样次数

    /// <summary>加权综合评分。</summary>
    public double OverallScore =>
        Compliance * 0.25 + CodeQuality * 0.35 +
        Reasoning * 0.25 + TokenEfficiency * 0.15;
}
```

### 6.4 评分持久化

- 存储在 `model_performance.db` (SQLite)
- 使用加权移动平均：近期表现权重更高
- 跨会话累积，构成模型的"终身档案"
- 可导出为 JSON 供社区分享"模型红黑榜"

---

## 七、UI 交互设计

### 7.1 布局结构

```
┌─────────────────────────────────────────────────────────────┐
│  🏪 服务商与模型管理                                          │
├──────────────┬──────────────────────────────────────────────┤
│              │                                               │
│  服务商列表   │  模型卡片阵列                                  │
│              │                                               │
│  ● OpenAI    │  ┌─────────────┐  ┌─────────────┐           │
│  ● Anthropic │  │ 🌟 GPT-4o   │  │ ⚡ GPT-4o    │           │
│  ● DeepSeek  │  │             │  │    Mini      │           │
│  ● Ollama 🏠 │  │ 综合: 8.7   │  │ 综合: 7.2   │           │
│  ● 自定义    │  │ $5/1M in    │  │ $0.15/1M in │           │
│              │  │ 🎯🧠📝     │  │ 🎯📝        │           │
│              │  └─────────────┘  └─────────────┘           │
│              │                                               │
│              │  ┌─────────────┐  ┌─────────────┐           │
│              │  │ 🏠 Qwen     │  │ 🏠 Llama    │           │
│              │  │    0.5B     │  │    3.2       │           │
│              │  │ 本地免费    │  │ 本地免费     │           │
│              │  │ 📝          │  │ 🎯📝        │           │
│              │  └─────────────┘  └─────────────┘           │
│              │                                               │
├──────────────┴──────────────────────────────────────────────┤
│  💰 今日消耗: $0.42 / $2.00 限额  │  已节省: ~$3.20 (本地)  │
└─────────────────────────────────────────────────────────────┘
```

### 7.2 模型卡片视觉元素

| 图标 | 含义 |
|---|---|
| 🌟 | 多模态模型（彩色光圈） |
| 🏠 | 本地模型（小房子） |
| ⚡ | 高性价比（性价比 > 阈值） |
| 🎯 | FunctionCalling 能力 |
| 🧠 | Reasoning 能力 |
| 📝 | Text-Summarization 能力 |
| 👁️ | Vision 能力 |

### 7.3 模型详情面板

点击模型卡片展开详情：

```
┌─────────────────────────────────────────────┐
│  GPT-4o — 详细信息                           │
│                                              │
│  ── 能力雷达图 ──                            │
│         代码 9.2                             │
│        /    \                                │
│   推理 8.8    摘要 7.5                       │
│        \    /                                │
│         工具 9.0                             │
│                                              │
│  ── 统计 ──                                  │
│  已使用: 42 次 │ 本月消耗: $1.25             │
│  平均延迟: 850ms │ 编译通过率: 94%            │
│                                              │
│  ── 标签 ──                                  │
│  [FunctionCalling] [Reasoning] [Vision]      │
│  [Code-Generation] [Strong-Refactoring]      │
│                                              │
│  [ 🔧 编辑 ]  [ 📊 测速 ]  [ 🗑️ 删除 ]     │
└─────────────────────────────────────────────┘
```

### 7.4 一键测速/测通

- 发送标准 Prompt 测试连通性和延迟
- 显示：连通状态 ✅/❌、首 Token 延迟、吞吐量
- 结果写入 ModelPerformanceScore

---

## 八、核心实现

### 8.1 ModelRegistry

```csharp
/// <summary>模型注册表：管理所有可用模型的元数据、查询和路由。</summary>
public class ModelRegistry
{
    private readonly ConcurrentDictionary<string, ModelMetadata> _models = new();

    public void Register(ModelMetadata model)
        => _models[$"{model.ProviderId}/{model.ModelId}"] = model;

    public ModelMetadata? GetModel(string providerId, string modelId)
        => _models.GetValueOrDefault($"{providerId}/{modelId}");

    public IReadOnlyList<ModelMetadata> GetAllModels()
        => [.. _models.Values];

    /// <summary>获取最适合指定任务的模型。</summary>
    public ModelMetadata? GetBestForTask(string taskType, params string[] requiredCapabilities)
    {
        return _models.Values
            .Where(m => requiredCapabilities.All(c => m.CapabilityTags.Contains(c)))
            .Where(m => !IsBudgetExhausted(m))
            .OrderByDescending(m => m.Performance.OverallScore)
            .ThenBy(m => m.PriceInputPer1M)
            .FirstOrDefault();
    }

    /// <summary>获取最佳本地蒸馏模型。</summary>
    public ModelMetadata? GetBestDistiller()
    {
        return _models.Values
            .Where(m => m.IsLocal && m.CapabilityTags.Contains("Distill"))
            .OrderByDescending(m => m.Performance.OverallScore)
            .FirstOrDefault();
    }

    /// <summary>获取具备视觉能力的 Leader 模型。</summary>
    public ModelMetadata? GetVisionLeader()
    {
        return _models.Values
            .Where(m => m.Modalities.Contains(ModelModality.Image)
                     && m.CapabilityTags.Contains("Reasoning"))
            .OrderByDescending(m => m.Performance.OverallScore)
            .FirstOrDefault();
    }

    /// <summary>更新模型的运行时评分。</summary>
    public void UpdatePerformance(string providerId, string modelId, ModelPerformanceScore score)
    {
        var key = $"{providerId}/{modelId}";
        if (_models.TryGetValue(key, out var model))
            _models[key] = model with { Performance = score };
    }

    private bool IsBudgetExhausted(ModelMetadata model)
    {
        if (model.DailyBudgetLimit is null) return false;
        var todaySpend = GetTodaySpend(model.ProviderId, model.ModelId);
        return todaySpend >= model.DailyBudgetLimit;
    }

    private decimal GetTodaySpend(string providerId, string modelId)
    {
        // TODO: 从 UsageTracker 查询今日消耗
        return 0;
    }
}
```

### 8.2 UsageTracker

```csharp
/// <summary>用量追踪器：实时记录每个模型的 Token 消耗和费用。</summary>
public class UsageTracker
{
    private readonly ConcurrentDictionary<string, UsageRecord> _daily = new();

    public void RecordUsage(string providerId, string modelId,
        int inputTokens, int outputTokens, ModelMetadata metadata)
    {
        var key = $"{providerId}/{modelId}/{DateTime.UtcNow:yyyy-MM-dd}";
        _daily.AddOrUpdate(key,
            _ => new UsageRecord(inputTokens, outputTokens, CalculateCost(inputTokens, outputTokens, metadata)),
            (_, existing) => existing with
            {
                InputTokens = existing.InputTokens + inputTokens,
                OutputTokens = existing.OutputTokens + outputTokens,
                Cost = existing.Cost + CalculateCost(inputTokens, outputTokens, metadata)
            });
    }

    public decimal GetTodayCost(string providerId, string modelId)
    {
        var key = $"{providerId}/{modelId}/{DateTime.UtcNow:yyyy-MM-dd}";
        return _daily.TryGetValue(key, out var record) ? record.Cost : 0;
    }

    public decimal GetTodayTotalCost()
        => _daily.Where(kv => kv.Key.EndsWith(DateTime.UtcNow.ToString("yyyy-MM-dd")))
                 .Sum(kv => kv.Value.Cost);

    public decimal GetTodaySavedByLocal()
    {
        // 估算：本地模型处理的 Token 量 × 假设使用云端的价格
        return 0; // TODO: 实现
    }

    private static decimal CalculateCost(int input, int output, ModelMetadata m)
        => input * m.PriceInputPer1M / 1_000_000m + output * m.PriceOutputPer1M / 1_000_000m;
}

public record UsageRecord(int InputTokens, int OutputTokens, decimal Cost);
```

### 8.3 EvaluationInterceptor

```csharp
/// <summary>评估拦截器：抽样评估 Worker 表现并更新模型评分。</summary>
public class EvaluationInterceptor(ModelRegistry registry)
{
    private readonly Random _random = new();
    private const double SampleRate = 0.15; // 15% 抽样率

    public async Task OnTaskCompletedAsync(TaskEvalContext context, CancellationToken ct)
    {
        if (!ShouldSample(context)) return;

        // 软件层客观指标
        var compileSuccess = context.CompileResult?.Success ?? false;
        var latencyMs = context.ElapsedMs;

        // 结合 Leader 主观评分（如果有）
        var leaderScore = context.LeaderEvaluation;

        // 计算加权移动平均
        var existing = context.Model.Performance;
        var updated = new ModelPerformanceScore
        {
            Compliance = WeightedAvg(existing.Compliance, leaderScore?.Compliance ?? existing.Compliance, existing.SampleCount),
            CodeQuality = compileSuccess
                ? WeightedAvg(existing.CodeQuality, leaderScore?.CodeQuality ?? 8.0, existing.SampleCount)
                : WeightedAvg(existing.CodeQuality, 3.0, existing.SampleCount),
            Reasoning = WeightedAvg(existing.Reasoning, leaderScore?.Reasoning ?? existing.Reasoning, existing.SampleCount),
            AvgLatencyMs = WeightedAvg(existing.AvgLatencyMs, latencyMs, existing.SampleCount),
            TokenEfficiency = existing.TokenEfficiency, // TODO: 计算
            SampleCount = existing.SampleCount + 1
        };

        registry.UpdatePerformance(context.Model.ProviderId, context.Model.ModelId, updated);

        // 自适应标签更新
        UpdateCapabilityTags(context.Model, updated);
    }

    private bool ShouldSample(TaskEvalContext ctx)
        => ctx.CompileResult is { Success: false }  // 编译失败必评
           || ctx.IsFirstTimeForTaskType             // 首次执行该类任务必评
           || _random.NextDouble() < SampleRate;     // 随机抽样

    private static double WeightedAvg(double existing, double newValue, int count)
        => count == 0 ? newValue : (existing * 0.7 + newValue * 0.3);

    private void UpdateCapabilityTags(ModelMetadata model, ModelPerformanceScore score)
    {
        // 自动添加/移除弱能力标签
        if (score.Compliance < 5.0 && score.SampleCount >= 5)
            model.CapabilityTags.Add("Weak-FunctionCalling");
        if (score.CodeQuality > 8.0 && score.SampleCount >= 5)
            model.ExpertiseTags.Add("Strong-CodeGeneration");
    }
}

public record TaskEvalContext(
    ModelMetadata Model,
    CompileResult? CompileResult,
    double ElapsedMs,
    bool IsFirstTimeForTaskType,
    ModelPerformanceScore? LeaderEvaluation);

public record CompileResult(bool Success, int ErrorCount);
```

---

## 九、与现有架构集成

### 9.1 扩展 LlmOptions

当前 `LlmOptions` 是单模型配置，需扩展为从 `ModelRegistry` 动态获取：

```
现有:  LlmOptions → 固定单个 Endpoint + ApiKey + Model
扩展:  ModelRegistry → 按任务类型 / Agent 角色动态选择 ModelMetadata
       → 构造 LlmOptions 传递给 OpenAiLlmGateway
```

### 9.2 集成点

```
Agent 请求 LLM 调用
  → SwarmOrchestrator 确定 Agent 角色
  → ModelRegistry.GetBestForTask(role, capabilities)
  → 构造 LlmOptions(metadata.BaseUrl, metadata.ApiKey, metadata.ModelId)
  → OpenAiLlmGateway.ChatAsync(...)
  → 返回结果
  → UsageTracker.RecordUsage(...)
  → EvaluationInterceptor.OnTaskCompletedAsync(...)  [抽样]
```

---

## 十、实现路线

### ✅ 已完成

- `LlmOptions` 单模型配置
- `ILlmGateway` / `OpenAiLlmGateway` 兼容协议网关

### 🚧 下一步

| 优先级 | 任务 | 说明 |
|---|---|---|
| **P0** | `ModelMetadata` + `ModelRegistry` | 模型元数据定义 + 注册/查询 |
| **P0** | 配置文件 `models.json` | 多服务商/多模型声明式配置 |
| **P0** | 动态 `LlmOptions` 构造 | 从 ModelRegistry 获取配置注入 Gateway |
| **P1** | `UsageTracker` | Token 消耗实时追踪 + 每日统计 |
| **P1** | 熔断器 | 每日限额 + 自动降级逻辑 |
| **P1** | 能力标签系统 | 静态标签 + 自适应更新 |
| **P2** | `EvaluationInterceptor` | 抽样评分 + 加权移动平均 + 标签进化 |
| **P2** | 服务商管理 UI | 左右布局 + 模型卡片 + 能力雷达图 |
| **P2** | 一键测速 | 连通性/延迟/吞吐量测试 |
| **P3** | 成本试算器 | 任务描述 → 预估消耗 |
| **P3** | 评分导出 | JSON 格式社区分享"模型红黑榜" |
