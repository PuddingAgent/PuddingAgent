# Task 14 — SKILL 插件化设计方案

> **状态：** ✏️ 设计中
> **依赖：** Task 10 (Agent 能力体系)、Task 11 (权限沙盒)、Task 13 (上下文预热)
> **目标：** 将 Agent 能力从硬编码解耦为可热插拔的插件架构，支持内置插件、外部 DLL 加载和环境自适应探活

---

## 目录

1. [设计原则](#一设计原则)
2. [插件三层接口](#二插件三层接口)
3. [插件生命周期](#三插件生命周期)
4. [动态加载机制](#四动态加载机制)
5. [分级调用模型](#五分级调用模型)
6. [感知注入策略](#六感知注入策略)
7. [内置插件清单](#七内置插件清单)
8. [插件 SDK](#八插件-sdk)
9. [视觉交互](#九视觉交互)
10. [核心实现](#十核心实现)
11. [实现路线](#十一实现路线)

---

## 一、设计原则

借鉴操作系统"驱动程序"思想：内核只定义标准接口，具体能力由独立插件提供。

| 原则 | 说明 |
|---|---|
| **接口统一** | 所有插件实现 `ISkillPlugin` 接口，LLM 无需关心底层差异 |
| **热插拔** | 运行时可加载/卸载插件，无需重启 |
| **环境感知** | 插件自检运行环境，不满足条件时优雅降级 |
| **成本标记** | 每个插件声明消耗等级 (L0/L1/L2)，Leader 据此做成本决策 |
| **权限继承** | 插件操作受 `PermissionGuard` (Task 11) 统一管控 |

---

## 二、插件三层接口

每个插件必须提供三层信息：

```
┌─────────────────────────────────────────────┐
│  IMeta (身份层)                              │
│  名称、版本、描述、参数 JSON Schema           │
│  → 自动生成 LLM Function Definition          │
├─────────────────────────────────────────────┤
│  ILogic (执行层)                             │
│  C# 实现，调用底层 SDK/CLI                    │
│  → 接收反序列化参数，返回结果文本               │
├─────────────────────────────────────────────┤
│  IPolicy (策略层)                            │
│  消耗等级、允许的 Agent 角色、路径约束          │
│  → 与 PermissionGuard 联动                   │
└─────────────────────────────────────────────┘
```

---

## 三、插件生命周期

```
发现 → 探活 → 注册 → 激活 → 使用 → 卸载
 │       │       │       │       │       │
 │  检查运行环境  │  注入到     │  LLM    │  释放资源
 扫描目录   是否满足  SkillRegistry  调用  手动/自动
 或程序集   依赖条件  生成 Schema       卸载
```

| 阶段 | 说明 |
|---|---|
| **发现** | 扫描 `Plugins/` 目录下的 DLL + 程序集内的 `[PuddingSkill]` 标记 |
| **探活** | 插件自检环境（如 `EverythingPlugin` 检查 `Everything.exe` 是否在运行） |
| **注册** | 通过 `SkillRegistry` 注册，自动生成 OpenAI 兼容的 JSON Schema |
| **激活** | Leader/Worker 请求该技能时首次初始化 |
| **使用** | LLM Function Call → 桥梁层校验权限 → 插件执行 → 返回结果 |
| **卸载** | 手动移除或 `AssemblyLoadContext.Unload()` 热卸载 |

---

## 四、动态加载机制

### 4.1 两种加载源

| 来源 | 机制 | 说明 |
|---|---|---|
| **内置插件** | 程序集反射扫描 `[PuddingSkill]` | 随主程序编译，始终可用 |
| **外部插件** | `AssemblyLoadContext` 加载 `Plugins/*.dll` | 第三方/用户自定义，支持热插拔 |

### 4.2 加载流程

```
PuddingCode 启动
  │
  ├─→ 扫描主程序集中的 [PuddingSkill] 类
  │     → 注册为内置插件
  │
  ├─→ 扫描 Plugins/ 目录
  │     → 每个 DLL 创建独立 AssemblyLoadContext
  │     → 反射查找 ISkillPlugin 实现
  │     → 调用 ProbeAsync() 探活
  │     → 成功 → 注册；失败 → 标记为"不可用"
  │
  └─→ FileSystemWatcher 监控 Plugins/ 目录
        → 新增 DLL → 热加载
        → 删除 DLL → 热卸载
```

### 4.3 隔离与安全

- 每个外部 DLL 运行在独立的 `AssemblyLoadContext` 中
- 插件崩溃不会导致主进程闪退
- 外部插件的所有文件/命令操作必须经过 `PermissionGuard`

---

## 五、分级调用模型

每个插件声明自身的 **消耗等级 (SkillTier)**，Leader 据此做成本路由：

| 等级 | 含义 | 计算资源 | 示例插件 |
|---|---|---|---|
| **L0** | 纯软件，零 Token | 本地 CPU | `FileFinder` (Everything)、`CodeFormatter` (dotnet format) |
| **L1** | 本地小模型辅助 | 本地 GPU/CPU | `WebSummarizer` (Playwright + Qwen 0.5B)、`CommitDrafter` |
| **L2** | 云端大模型 | 远程 API | `CodeArchitect` (Roslyn + GPT-4o)、`ComplexRefactor` |

**Leader 决策规则：** 优先调用 L0 → L1 → L2，仅在低级别无法解决时升级。

---

## 六、感知注入策略

### 6.1 两步走告知

**初次握手：** Leader 启动时只注入所有插件的简短指纹：

```
Available Skills: [FileSystem:v2], [Search:v1], [Git:v1.2], [Everything:v1 ✓], [Docker:v1 ✗]
```

**动态拉取：** 当 LLM 首次调用某个插件时，桥梁层拦截请求并注入完整 Schema：

```
[SYSTEM]: You're about to use 'FileFinder'. Here's its full API:
  - search_files(pattern: string, path?: string, maxResults?: int)
  - Returns: list of file paths matching the pattern
```

### 6.2 环境感知状态

插件探活结果直接反映在 LLM 可见的状态中：

```
- [SKILL] Everything: ✅ Available (v1.4, 2.3M files indexed)
- [SKILL] Docker: ❌ Unavailable (Docker daemon not running)
- [SKILL] Roslyn: ✅ Available (.NET 10 SDK detected)
```

---

## 七、内置插件清单

### 7.1 搬砖类 (L0)

| 插件 | 底层软件 | 功能 |
|---|---|---|
| `FileFinderPlugin` | Everything SDK / System.IO | 毫秒级全盘文件搜索 |
| `CodeFormatterPlugin` | `dotnet format` / Prettier | 代码格式化 |
| `CompressionPlugin` | System.IO.Compression | 目录压缩/解压 |
| `RoslynAnalyzerPlugin` | Roslyn Syntax Tree | 符号提取、引用分析、类/方法签名导出 |
| `DependencyDoctorPlugin` | `dotnet --info` / `node -v` | 环境体检、SDK 版本检测 |

### 7.2 智能辅助类 (L1)

| 插件 | 底层软件 | 功能 |
|---|---|---|
| `WebSummarizerPlugin` | HttpClient + Qwen 0.5B | 网页抓取 → 本地模型压缩 → 返回摘要 |
| `CommitDrafterPlugin` | `git diff` + Qwen 0.5B | 自动生成 Commit Message 初稿 |
| `TestScaffolderPlugin` | Roslyn + Qwen 0.5B | 分析方法签名 → 生成测试用例框架 |
| `DocSyncPlugin` | FileWatcher + Qwen 0.5B | 代码变动时自动更新 README/API 文档 |
| `SentimentMonitorPlugin` | Qwen 0.5B | 监控 Agent 弹幕流，检测死循环/幻觉倾向 |

### 7.3 高级推理类 (L2)

| 插件 | 底层软件 | 功能 |
|---|---|---|
| `CodeArchitectPlugin` | Roslyn + 云端 LLM | 复杂重构、架构级代码修改 |
| `SecurityAuditorPlugin` | `dotnet list package --vulnerable` + LLM | 依赖漏洞分析 + 修复建议 |
| `MediaProcessorPlugin` | Magick.NET / ffmpeg | 图片格式转换、压缩 |

---

## 八、插件 SDK

### 8.1 核心接口

```csharp
/// <summary>技能插件接口。</summary>
public interface ISkillPlugin
{
    /// <summary>插件元数据。</summary>
    SkillPluginMeta Meta { get; }

    /// <summary>环境探活：检查插件运行条件是否满足。</summary>
    Task<ProbeResult> ProbeAsync(CancellationToken ct = default);

    /// <summary>获取该插件提供的所有 Action。</summary>
    IReadOnlyList<SkillAction> GetActions();
}

public record SkillPluginMeta(
    string Name,
    string Version,
    string Description,
    SkillTier Tier);

public enum SkillTier { L0_Software, L1_LocalModel, L2_CloudModel }

public record ProbeResult(bool IsAvailable, string? Reason = null);

public record SkillAction(
    string Name,
    string Description,
    ToolParameterSchema Parameters,
    Func<string, CancellationToken, Task<string>> Execute);
```

### 8.2 属性快捷方式

用 `[PuddingSkill]` + `[SkillAction]` 属性可以零样板代码定义插件：

```csharp
[PuddingSkill(Name = "GitHubTrend", Description = "获取当前热门开源项目",
    Tier = SkillTier.L1_LocalModel)]
public class GitHubTrendPlugin : ISkillPlugin
{
    [SkillAction("fetch_trending", "获取指定语言的热门项目")]
    public async Task<string> GetTrending(
        [Description("编程语言")] string lang,
        CancellationToken ct)
    {
        // L0: HttpClient 抓取数据
        var html = await httpClient.GetStringAsync($"https://github.com/trending/{lang}", ct);
        // L1: 本地小模型过滤
        var summary = await localLlm.SummarizeAsync(html, ct);
        return summary;
    }

    // 探活：检查网络是否可用
    public Task<ProbeResult> ProbeAsync(CancellationToken ct)
        => Task.FromResult(new ProbeResult(NetworkInterface.GetIsNetworkAvailable()));
}
```

### 8.3 Everything 插件示例

```csharp
[PuddingSkill(Name = "FileFinder", Description = "毫秒级全盘文件搜索",
    Tier = SkillTier.L0_Software)]
public class EverythingPlugin : ISkillPlugin
{
    [SkillAction("search_files", "按文件名模式搜索")]
    public Task<string> SearchFiles(
        [Description("搜索模式，如 *.cs")] string pattern,
        [Description("可选：限定目录")] string? path,
        [Description("最大结果数")] int maxResults = 20,
        CancellationToken ct = default)
    {
        // 调用 Everything SDK / es.exe
        var results = EverythingApi.Search(pattern, path, maxResults);

        // PermissionGuard 过滤：隐藏项目外路径
        return Task.FromResult(string.Join('\n',
            results.Where(r => r.StartsWith(_projectRoot))));
    }

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        // 检查 Everything 是否在运行
        var running = Process.GetProcessesByName("Everything").Length > 0;
        return new ProbeResult(running,
            running ? null : "Everything.exe is not running. Falling back to System.IO.");
    }
}
```

---

## 九、视觉交互

### 9.1 技能箱面板

点击 Agent 节点的"详情"，可查看已挂载的插件：

```
┌─────────────────────────────────────────┐
│  🧩 抹茶布丁 — 已挂载技能               │
│                                          │
│  ✅ FileFinder v1.4    [L0] ← 自动加载  │
│  ✅ CodeFormatter v2.0 [L0]             │
│  ✅ Git v1.2           [L0/L1]          │
│  ⏸️ WebSummarizer v1.0 [L1] ← 待激活   │
│  ❌ Docker v1.0        [L1] ← 未运行    │
│                                          │
│  [ + 添加插件 ]  [ 🗑️ 管理 ]            │
└─────────────────────────────────────────┘
```

### 9.2 热插拔交互

- 用户可以在任务运行中给 Agent 拖入新插件
- Agent 收到系统通知："你现在拥有了全局搜索能力"
- 拓扑节点闪烁紫色光圈表示能力变更

### 9.3 插件状态灯

| 状态 | 颜色 | 含义 |
|---|---|---|
| ✅ 可用 | 绿色 | 探活通过，随时可调用 |
| ⏸️ 待激活 | 黄色 | 已注册但未初始化 |
| ❌ 不可用 | 灰色 | 探活失败（缺少依赖/未安装） |
| 🔴 错误 | 红色 | 运行时崩溃 |

---

## 十、核心实现

### 10.1 SkillLoader

```csharp
/// <summary>插件加载器：扫描、探活、注册。</summary>
public class SkillLoader(IToolRegistry registry, PermissionGuard guard)
{
    private readonly List<AssemblyLoadContext> _contexts = [];

    /// <summary>扫描内置插件（主程序集）。</summary>
    public async Task LoadBuiltInPluginsAsync(CancellationToken ct)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await LoadFromAssemblyAsync(assembly, ct);
    }

    /// <summary>扫描外部插件目录。</summary>
    public async Task LoadExternalPluginsAsync(string pluginsDir, CancellationToken ct)
    {
        if (!Directory.Exists(pluginsDir)) return;

        foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll"))
        {
            var ctx = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(dll), isCollectible: true);
            _contexts.Add(ctx);
            var assembly = ctx.LoadFromAssemblyPath(Path.GetFullPath(dll));
            await LoadFromAssemblyAsync(assembly, ct);
        }
    }

    private async Task LoadFromAssemblyAsync(Assembly assembly, CancellationToken ct)
    {
        var pluginTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ISkillPlugin).IsAssignableFrom(t));

        foreach (var type in pluginTypes)
        {
            var plugin = (ISkillPlugin)Activator.CreateInstance(type)!;
            var probe = await plugin.ProbeAsync(ct);

            if (!probe.IsAvailable)
            {
                // 标记为不可用，UI 显示灰色
                continue;
            }

            // 将每个 Action 包装为 ITool 并注册
            foreach (var action in plugin.GetActions())
            {
                var tool = new PluginToolAdapter(plugin.Meta, action, guard);
                registry.Register(tool);
            }
        }
    }

    /// <summary>卸载所有外部插件。</summary>
    public void UnloadExternal()
    {
        foreach (var ctx in _contexts)
            ctx.Unload();
        _contexts.Clear();
    }
}
```

### 10.2 PluginToolAdapter

```csharp
/// <summary>将 SkillAction 适配为现有 ITool 接口。</summary>
internal class PluginToolAdapter(
    SkillPluginMeta meta, SkillAction action, PermissionGuard guard) : ITool
{
    public string Name => $"{meta.Name}.{action.Name}";
    public string Description => action.Description;
    public ToolParameterSchema Parameters => action.Parameters;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        // 权限拦截
        var check = guard.ValidateCommand(Name, targetPath: null);
        if (!check.IsAllowed)
            return $"[SECURITY ERROR]: {check.DenialReason}";

        return await action.Execute(argumentsJson, ct);
    }
}
```

---

## 十一、实现路线

### ✅ 已完成

- `ITool` / `IToolRegistry` 抽象（Task 03）
- `ShellTool`、`FileTool` 内置工具
- `PermissionGuard` 设计（Task 11）

### 🚧 下一步

| 优先级 | 任务 | 说明 |
|---|---|---|
| **P0** | `ISkillPlugin` 接口 + `SkillPluginMeta` | 定义插件三层接口 |
| **P0** | `SkillLoader` | 内置扫描 + 外部 DLL 加载 + 探活 |
| **P0** | `PluginToolAdapter` | 将 ISkillPlugin 适配到现有 IToolRegistry |
| **P1** | `[PuddingSkill]` / `[SkillAction]` 属性 | 零样板代码定义插件 |
| **P1** | 内置 L0 插件 | `FileFinder`、`CodeFormatter`、`RoslynAnalyzer` |
| **P1** | 热插拔 FileWatcher | 监控 Plugins/ 目录，自动加载/卸载 |
| **P2** | 技能箱 UI 面板 | 显示已挂载插件、状态灯、热插拔操作 |
| **P2** | 环境自适应降级 | Everything 未安装时回退到 System.IO |
| **P3** | 插件商店概念 | 从 GitHub 链接一键下载安装插件 |
| **P3** | L1 辅助插件集 | `WebSummarizer`、`CommitDrafter`、`TestScaffolder` |
