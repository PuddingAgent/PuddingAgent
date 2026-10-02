# Task 13 — 上下文预热设计方案 (Context Pre-warming)

> **状态：** ✏️ 设计中
> **依赖：** Task 10 (Agent 能力体系)、Task 12 (感官过滤)、D03 (Agent 编排器)
> **目标：** 消除 Agent 冷启动延迟，通过后台异步预热和并发启动让 Leader/Worker 拥有即时项目认知

---

## 目录

1. [设计原则](#一设计原则)
2. [三级预热机制](#二三级预热机制)
3. [并发启动](#三并发启动)
4. [认知资产预处理](#四认知资产预处理)
5. [后台索引引擎](#五后台索引引擎)
6. [认知包结构](#六认知包结构)
7. [功耗控制](#七功耗控制)
8. [视觉反馈](#八视觉反馈)
9. [核心实现](#九核心实现)
10. [实现路线](#十实现路线)

---

## 一、设计原则

| 原则 | 说明 |
|---|---|
| **能用软件解决的不用 AI** | 文件扫描、符号提取、目录树由 C# 直接完成 |
| **能用本地模型解决的不用云端** | 类摘要、代码简述由 Qwen 0.5B 本地处理 |
| **预测性加载** | 在用户点击"开始"之前，Leader 已在后台就绪 |
| **低侵入性** | 预热不抢占开发者 CPU/磁盘资源 |

---

## 二、三级预热机制

根据用户行为触发，逐级深入：

```
┌────────────────────────────────────────────────────────────┐
│  L1: 结构预热 — 打开项目时                                   │
│  扫描 FileTree、识别技术栈（.NET 10 / Node.js / Rust）       │
│  生成目录索引 + llms.txt 全景图                               │
│  成本: 0 Token · 延迟: <1s                                   │
├────────────────────────────────────────────────────────────┤
│  L2: 语义预热 — Leader 规划时                                │
│  本地 Qwen 0.5B 生成关键类/模块的一句话摘要                    │
│  读取最近 MEMORY.md（长期记忆）                               │
│  建立初始 Context Cache                                      │
│  成本: 0 Token（本地） · 延迟: ~2-5s                         │
├────────────────────────────────────────────────────────────┤
│  L3: 领域预热 — Worker 启动时                                │
│  拼装认知包：模块摘要 + 相关记忆片段 + 2-3 个核心文件全文       │
│  注入到 Worker 的初始 System Prompt                          │
│  成本: 最小化 · 延迟: ~1s                                    │
└────────────────────────────────────────────────────────────┘
```

### 触发时机

| 触发行为 | 预热级别 | 执行内容 |
|---|---|---|
| 打开项目文件夹 | L1 结构预热 | 文件树、技术栈识别、路径索引 |
| 鼠标悬停"开始"按钮 / Leader 启动 | L2 语义预热 | 本地模型摘要、MEMORY.md 加载、Context Cache |
| Leader 派生 Worker | L3 领域预热 | 拼装该 Worker 任务相关的认知包 |

---

## 三、并发启动

### 3.1 从串行到并行

Leader 派生多个 Worker 时，使用 `Task.WhenAll` 并行下发，整体耗时取决于最慢的单个 LLM 请求：

```
传统串行: Worker1(2s) → Worker2(2s) → Worker3(2s) = 6s
并行启动: Worker1(2s) ┐
          Worker2(2s) ├→ max = 2s
          Worker3(2s) ┘
```

### 3.2 并发节流器

同时启动过多 Agent 可能触发云服务商 TPM/RPM 限制或压垮 UI。引入 `ConcurrencyThrottle`：

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `MaxParallelSpawns` | 3 | 同时启动的最大 Worker 数 |
| `SpawnCooldownMs` | 500 | 每批启动后的冷却间隔 |
| `RateLimitRetryMs` | 2000 | 触发速率限制后的重试间隔 |

逻辑：先起 3 个，一旦其中一个握手成功，立即填补下一个。

---

## 四、认知资产预处理

在 Agent 启动前，软件层已完成"搬砖"工作：

### 4.1 分级执行

| 级别 | 执行者 | 任务 | 示例 |
|---|---|---|---|
| **L0** | 纯软件（0 Token） | 文件搜索、目录树、符号导出 | `Directory.GetFiles`、Roslyn 提取方法签名 |
| **L1** | 本地小模型（~0 Token） | 类摘要、代码简述、搜索结果压缩 | Qwen 0.5B: "用一句话描述这个类的职责" |
| **L2** | 云端大模型（高成本） | 仅处理经 L0+L1 压缩后的精炼上下文 | GPT-4o: 逻辑推理、架构决策 |

### 4.2 预处理任务清单

| 任务 | 执行层 | 触发时机 | 产出 |
|---|---|---|---|
| 项目目录树 | L0 | 打开项目 | `FileTree` JSON |
| 技术栈识别 | L0 | 打开项目 | `.NET 10` / `Node.js 22` 等标签 |
| 方法签名提取 | L0 | 打开项目 | 类名、方法名、参数列表 |
| 依赖拓扑 | L0 | 打开项目 | 类与类之间的引用关系图 |
| 类/模块摘要 | L1 | Leader 启动 | 每个类一句话摘要 |
| Commit 日志摘要 | L1 | Leader 启动 | 最近 10 次提交的简述 |
| llms.txt 压缩 | L1 | 大型项目 | 精华版索引（降低 70-90% Token） |

---

## 五、后台索引引擎

### 5.1 双索引架构

| 索引类型 | 技术实现 | 解决的问题 |
|---|---|---|
| **文件/路径索引** | Everything SDK / 内存字典 | "找所有 `.css` 文件"、"定位 `AuthService` 在哪" |
| **全文/语义索引** | SQLite-VSS + BM25 | "搜索处理微信支付的代码"、"找到 Hardcode 密钥" |

### 5.2 FileSystemWatcher 实时同步

静态索引会过时，需通过 `FileSystemWatcher` 实现索引活化：

| 事件 | 处理 |
|---|---|
| `OnCreated` / `OnDeleted` | 实时更新文件索引 |
| `OnChanged` | 加入待重处理队列 |

**防抖机制：** 设置 3-5 秒观察期，文件"冷静"后才触发向量化和摘要更新。

**增量更新：** 只对变化的文件重新索引，不重扫整个项目。

### 5.3 低优先级调度

| 策略 | 说明 |
|---|---|
| 低线程优先级 | `ThreadPriority.Lowest`，只在 CPU 空闲时运行 |
| 分片扫描 | 每 50 个文件 `Task.Delay(100)`，防止磁盘 IO 飙升 |
| 内存感知 | 检测系统内存压力，自动进入暂停/慢速模式 |

---

## 六、认知包结构

Worker 启动时，不再接收全部文件，而是接收精准拼装的 **认知包 (Context Packet)**：

### 6.1 ContextManifest 定义

```csharp
public record ContextManifest(
    string ProjectStructure,           // 目录树（精简版）
    string TechStack,                  // 技术栈标签
    List<ModuleSummary> Summaries,     // 本地模型生成的模块摘要
    List<string> RelevantFiles,        // 任务直接相关的 2-3 个核心文件全文
    string? RecentMemory,              // MEMORY.md 中的相关片段
    string TaskInstruction);           // Leader 分配的具体任务

public record ModuleSummary(
    string FilePath,
    string ClassName,
    string OneLinerSummary);           // "处理用户认证和 JWT Token 生成"
```

### 6.2 Token 节省效果

| 场景 | 传统方式 | 认知包方式 | 节省比例 |
|---|---|---|---|
| 中型项目 (50 文件) | ~30,000 tokens | ~5,000 tokens | ~83% |
| 大型项目 (500 文件) | 超出窗口限制 | ~8,000 tokens | 可用 |

---

## 七、功耗控制

### 7.1 绿色预热模式

| 电源状态 | 预热策略 |
|---|---|
| **插电** | 全速预热：向量索引 + LLM 摘要 + 实时 FileWatcher |
| **电池** | 节能模式：停止向量化和 LLM 摘要，仅保持文件名/文本索引 |

### 7.2 资源占用上限

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `MaxMemoryMb` | 256 | 预热引擎最大内存占用 |
| `MaxCpuPercent` | 15 | 后台索引最大 CPU 占比 |
| `IdleOnlyEmbedding` | true | 向量化仅在 CPU 空闲时执行 |

---

## 八、视觉反馈

### 8.1 预热进度

| 状态 | 视觉效果 |
|---|---|
| 索引中 | Leader 节点中心微弱旋转光环，标签 `Context Indexing...` |
| 就绪 | 光环变为常亮，输入框提示 `Commander is ready to lead.` |
| 节能模式 | 光环变为虚线环，标签 `Low-power mode` |

### 8.2 认知进度条

- 拓扑图底部淡紫色进度条：`Indexing Project Intelligence... 42%`
- 点击文件可查看本地模型已生成的摘要
- 进度完成后自动消失

### 8.3 分级算力视觉标识

| 级别 | 连线颜色 | 含义 |
|---|---|---|
| L0 软件层 | 深蓝色实线 | 纯软件处理，零成本 |
| L1 本地模型 | 淡紫色虚线 | 本地廉价算力介入 |
| L2 云端模型 | 金色流光 | 昂贵的远程推理 |

---

## 九、核心实现

### 9.1 SwarmBootstrapper

```csharp
/// <summary>Swarm 启动引导器：预热 + 并行 Worker 创建。</summary>
public class SwarmBootstrapper(
    IAgentFactory agentFactory,
    IProjectIndexer indexer,
    SwarmBootstrapConfig config)
{
    /// <summary>并行启动多个 Worker，受节流器控制。</summary>
    public async Task<IReadOnlyList<IAgent>> SpawnWorkersAsync(
        List<WorkerRequirement> requirements, CancellationToken ct)
    {
        // 并发节流
        using var semaphore = new SemaphoreSlim(config.MaxParallelSpawns);
        var tasks = requirements.Select(async req =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var manifest = await indexer.BuildManifestAsync(req.TaskScope, ct);
                return await agentFactory.CreateAndWarmUpAsync(req, manifest, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }
}

public record SwarmBootstrapConfig(
    int MaxParallelSpawns = 3,
    int SpawnCooldownMs = 500,
    int RateLimitRetryMs = 2000);
```

### 9.2 BackgroundIndexer

```csharp
/// <summary>后台异步索引引擎：FileWatcher + 分片扫描 + 防抖。</summary>
public class BackgroundIndexer : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Channel<string> _updateQueue = Channel.CreateUnbounded<string>();
    private readonly ILocalLlmRunner? _localLlm;
    private readonly TimeSpan _debounce = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, string> _summaryCache = new();

    public void Start(string rootPath)
    {
        _watcher = new FileSystemWatcher(rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
        _watcher.Changed += (_, e) => _updateQueue.Writer.TryWrite(e.FullPath);
        _watcher.Created += (_, e) => _updateQueue.Writer.TryWrite(e.FullPath);
        _watcher.Deleted += (_, e) => _summaryCache.TryRemove(e.FullPath, out _);
        _watcher.EnableRaisingEvents = true;

        _ = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        var pending = new Dictionary<string, DateTime>();
        await foreach (var path in _updateQueue.Reader.ReadAllAsync())
        {
            pending[path] = DateTime.UtcNow;

            // 防抖：等待文件冷静
            await Task.Delay(_debounce);
            if (pending.TryGetValue(path, out var ts)
                && DateTime.UtcNow - ts >= _debounce)
            {
                pending.Remove(path);
                await IndexFileAsync(path);
            }
        }
    }

    private async Task IndexFileAsync(string path)
    {
        // L0: 符号提取
        var signatures = ExtractSignatures(path);

        // L1: 本地模型摘要（如果可用）
        if (_localLlm is not null)
        {
            var summary = await _localLlm.SummarizeAsync(signatures);
            _summaryCache[path] = summary;
        }

        // 更新全文索引（SQLite-VSS）
        await UpdateFullTextIndexAsync(path);
    }

    public void Dispose() => _watcher?.Dispose();
}
```

### 9.3 ProjectContextAnalyzer

```csharp
/// <summary>项目认知分析器：构建 ContextManifest。</summary>
public class ProjectContextAnalyzer(
    BackgroundIndexer indexer,
    ILocalLlmRunner? localLlm) : IProjectIndexer
{
    public async Task<ContextManifest> BuildManifestAsync(
        string taskScope, CancellationToken ct)
    {
        // L0: 纯软件 — 目录树 + 技术栈
        var structure = ScanProjectStructure();
        var techStack = DetectTechStack();

        // L1: 本地模型 — 相关模块摘要
        var summaries = GetCachedSummaries(taskScope);

        // L3: 精准上下文 — 只取任务相关的核心文件
        var files = SelectRelevantFiles(taskScope, maxFiles: 3);

        return new ContextManifest(
            structure, techStack, summaries, files,
            RecentMemory: await LoadRecentMemoryAsync(ct),
            TaskInstruction: taskScope);
    }
}
```

---

## 十、实现路线

### ✅ 已完成

- `ITool` / `IToolRegistry` 抽象
- `ShellTool` / `FileTool` 基础实现
- Agent → LLM Tool Calling 闭环

### 🚧 下一步

| 优先级 | 任务 | 说明 |
|---|---|---|
| **P0** | `SwarmBootstrapper` | 并行 Worker 启动 + 节流器 |
| **P0** | L1 结构预热 | 项目打开时扫描文件树 + 技术栈识别 |
| **P1** | `BackgroundIndexer` | FileWatcher + 防抖 + 增量更新 |
| **P1** | `ContextManifest` 认知包 | 定义结构 + 拼装逻辑 |
| **P1** | L2 语义预热 | 本地 Qwen 0.5B 类摘要集成 |
| **P2** | 双索引引擎 | Everything SDK + SQLite-VSS 联合查询 |
| **P2** | 功耗控制 | 电源感知 + 内存/CPU 上限 |
| **P3** | 认知进度条 UI | 淡紫色进度条 + Leader 就绪指示 |
| **P3** | 首次冷启动优化 | 大型项目（万级文件）的分片首次索引 |
