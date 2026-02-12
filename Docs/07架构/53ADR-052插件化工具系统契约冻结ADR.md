# 53 ADR-052 插件化工具系统契约冻结

> 状态：**accepted / Phase 1 implementing**  
> 日期：2026-06-25  
> 阶段：Phase 0 - ADR 和契约冻结  
> 关联：  
> - [tool-infrastructure-layering](tool-infrastructure-layering.md)  
> - [51ADR-050会话层统一投影与前端观察者模型ADR](51ADR-050会话层统一投影与前端观察者模型ADR.md)  
> - [2026-06-24-Pudding工具系统增强-CodeWhale参考设计](../04工具与技能/2026-06-24-Pudding工具系统增强-CodeWhale参考设计.md)  
> - [Task 14 - SKILL 插件化设计方案](../Tasks/task14-skill-plugin.md)

---

## 1. 背景

Pudding 目前已经具备统一工具基础设施：

- `PuddingCore.Tools` 定义 `IPuddingTool`、`ToolDescriptor`、`ToolAttribute`、`ToolExecutionContext`。
- `PuddingRuntime.Services.Tools` 负责工具注册、schema 生成、执行、权限策略、AgentFirewall 和 telemetry。
- `PuddingPlatform.Controllers.Api.CapabilityApiController` 从 `IPuddingToolCatalogService` 派生 `/api/capabilities`。
- `/admin/capability-management` 是工具管理页面，当前只读展示运行时工具注册表；工具授予在 Agent 模板中配置。
- Agent 模板通过 `SelectedCapabilityIds` 保存已授权能力，运行时由 `AgentRuntimeProfileResolver` 解析为 `CapabilityPolicy` 和 LLM tool definitions。

这意味着插件化的核心不是新增一套“插件工具执行系统”，而是让插件成为 `IPuddingTool` 的可发现来源。插件提供工具实现和元数据，但工具能否对 Agent 可见、能否执行、是否需要授权，仍必须走现有能力管理和 Tool 执行链路。

当前也已有 `SkillPackage`：

- `SkillPackageEntity`、`SkillPackageApiController`、`AgentSkillPackageRegistry`、`SkillPackageDownloadService` 负责上传、下载和解压 Skill 包。
- SkillPackage 面向提示词、上下文资源和 agent skill 内容，不是 DLL 执行宿主。

Phase 0 的目标是冻结插件包格式、SDK 边界、权限模型、加载生命周期，以及插件与现有 Tool / SkillPackage 的关系，避免后续实现继续出现 controller 直接读文件、执行器绕过 Service、插件工具旁路授权等架构异味。

---

## 2. 决策摘要

1. 插件是 Tool 的来源之一，不是独立执行面。
2. 插件 manifest 是插件元数据事实源；DLL 只提供行为实现。
3. 插件工具必须适配为 `IPuddingTool`，进入 `IPuddingToolRegistry`。
4. `/admin/capability-management` 继续作为工具能力管理入口；插件工具通过 `/api/capabilities` 自然出现。
5. `CapabilityPolicy` 仍是 Agent 模板授权事实源；插件不得绕过模板授权。
6. V0 插件为本地受信扩展，不承诺 in-process 安全沙箱。
7. Phase 1 支持 manifest-only 插件目录重扫和 ZIP 安装后即时进入运行时目录；DLL 加载、热替换和热卸载进入后续阶段。
8. SkillPackage 与 Plugin 分离：SkillPackage 是提示词/资源包，Plugin 是可执行扩展包。

---

## 3. 插件包格式

### 3.1 存储根目录

开发和本地部署默认从数据目录加载插件：

```text
D:\data\plugins\
  {pluginId}\
    plugin.json
    bin\
      Plugin.Assembly.dll
      dependency.dll
    resources\
    state\
```

容器部署时使用同一逻辑根：

```text
{PUDDING_DATA_ROOT}/plugins/{pluginId}/...
```

约束：

- 插件目录必须位于数据根目录下，加载器不得接受任意绝对路径。
- `plugin.json` 必须位于插件根目录。
- `entry.assembly` 必须解析到插件根目录内。
- `resources` 和 `state` 目录由插件宿主传入，插件不自行推断数据根路径。

### 3.2 manifest v1

`plugin.json` 使用 `pudding-plugin/v1`：

```json
{
  "schema": "pudding-plugin/v1",
  "id": "pudding.code-search",
  "name": "Code Search Plugin",
  "version": "1.0.0",
  "description": "Adds code search tools backed by a local index.",
  "entry": {
    "assembly": "bin/Pudding.Plugin.CodeSearch.dll",
    "type": "Pudding.Plugin.CodeSearch.CodeSearchPlugin"
  },
  "tools": [
    {
      "id": "plugin_code_search",
      "name": "Code search",
      "description": "Search code symbols and files.",
      "category": "Query",
      "permissionLevel": "Low",
      "safety": ["ReadOnly", "ConcurrencySafe"],
      "enabledByDefault": false,
      "sortOrder": 300,
      "parameters": {
        "properties": [
          {
            "name": "query",
            "type": "string",
            "description": "Search keyword or symbol name."
          }
        ],
        "required": ["query"]
      }
    }
  ],
  "permissions": {
    "filesystem": ["workspace-read"],
    "network": [],
    "shell": false
  },
  "compatibility": {
    "minHostVersion": "1.0.0",
    "targetFramework": "net10.0"
  },
  "integrity": {
    "sha256": "",
    "signature": ""
  }
}
```

### 3.3 manifest 字段契约

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `schema` | 是 | 固定为 `pudding-plugin/v1`。 |
| `id` | 是 | 插件 ID，可用小写字母、数字、点和连字符，例如 `pudding.code-search`。 |
| `name` | 是 | 后台显示名称。 |
| `version` | 是 | SemVer 字符串。 |
| `entry.assembly` | 是 | 相对插件根目录的 DLL 路径。 |
| `entry.type` | 否 | `IPuddingPlugin` 实现类型；为空时扫描程序集内唯一实现。 |
| `tools` | 是 | 插件声明的 Tool 清单。V0 插件必须至少声明一个 tool。 |
| `permissions` | 是 | 插件请求的宿主权限，不等同于 Agent 模板授权。 |
| `compatibility` | 是 | 宿主版本和 TFM 约束。 |
| `integrity` | 否 | V0 可为空；后续用于签名和 hash 校验。 |

### 3.4 Tool ID 命名

当前 `ToolDescriptorFactory` 和 `PuddingToolRegistry` 要求 Tool ID 满足：

```text
^[a-zA-Z0-9_]+$
```

因此插件 manifest 中的 `tools[].id` 必须直接满足该规则。推荐命名：

```text
plugin_{pluginShortName}_{action}
```

示例：

```text
plugin_code_search
plugin_code_outline
plugin_git_commit_draft
```

不采用 `plugin.id/action`、`plugin.id.action` 或连字符形式，因为它们无法进入现有 `PuddingToolRegistry`。

---

## 4. SDK 边界

### ADR-052-A：插件 SDK 放入 PuddingCore

**决定**：插件作者可见的稳定契约放入 `PuddingCore.Plugins`，不得依赖 Runtime、Platform、EF、ASP.NET 或具体文件服务。

建议契约：

```csharp
namespace PuddingCode.Plugins;

public interface IPuddingPlugin
{
    Task<PuddingPluginProbeResult> ProbeAsync(
        PuddingPluginContext context,
        CancellationToken ct = default);

    IReadOnlyList<IPuddingPluginTool> GetTools();
}

public interface IPuddingPluginTool
{
    string ToolId { get; }

    Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        PuddingPluginContext pluginContext,
        CancellationToken ct = default);
}

public sealed record PuddingPluginContext
{
    public required string PluginId { get; init; }
    public required string PluginRoot { get; init; }
    public required string ResourcesRoot { get; init; }
    public required string StateRoot { get; init; }
    public required IReadOnlyDictionary<string, string> Settings { get; init; }
}

public sealed record PuddingPluginProbeResult
{
    public required bool IsAvailable { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyDictionary<string, string> Diagnostics { get; init; } =
        new Dictionary<string, string>();
}
```

设计意图：

- `IPuddingPlugin` 只描述插件生命周期和工具集合。
- `IPuddingPluginTool` 只描述插件内部执行委托。
- 真正进入 Pudding 的执行对象仍是 Runtime 适配出来的 `IPuddingTool`。
- 插件上下文由宿主构造，避免插件自行读取 `D:\data`、数据库或全局配置。

### ADR-052-B：插件不得拿到裸 `IServiceProvider`

**决定**：V0 不向插件暴露根 `IServiceProvider` 或完整 `IServiceCollection`。

原因：

- 裸 DI 容器会让插件绕过分层边界，直接读取数据库、配置文件或内部 Service。
- 一旦内部 Service 签名改变，插件和上层业务都会腐烂。
- 插件作者需要的是稳定能力，不是宿主内部实现。

后续如果需要注入宿主能力，应通过显式 Facade：

```csharp
public interface IPuddingPluginHostServices
{
    IPluginLogger Logger { get; }
    IPluginFileAccess FileAccess { get; }
    IPluginHttpClientFactory HttpClientFactory { get; }
}
```

这些 Facade 必须声明权限，并由 Runtime 实现。

### ADR-052-C：插件工具描述以 manifest 为准

**决定**：工具元数据优先来自 `plugin.json`，DLL 返回的 `IPuddingPluginTool.ToolId` 只能绑定 manifest 中已声明的 tool。

原因：

- Admin、能力管理、模板配置和审计需要在不执行 DLL 的情况下知道插件提供什么。
- Manifest-only 阶段可以先完成 UI 和权限链路。
- DLL 不应在运行时动态增加未声明工具，避免绕过审核。

---

## 5. Runtime 插件宿主

### ADR-052-D：Runtime 负责加载和适配

**决定**：插件加载、探活、AssemblyLoadContext、工具适配和运行状态归属 `PuddingRuntime.Services.Plugins`。

建议服务：

```text
PuddingRuntime.Services.Plugins
  PluginManifestLoader
  PluginPackageValidator
  PluginAssemblyLoadContext
  PluginLifecycleService
  PluginCatalog
  PluginToolAdapter
  PluginDiagnosticsSink
```

职责：

- 读取和验证 `plugin.json`。
- 校验插件路径、版本、TFM 和权限声明。
- 加载 DLL 并创建 `IPuddingPlugin`。
- 调用 `ProbeAsync`。
- 将插件 tool 适配为 `IPuddingTool`。
- 向 `IPuddingToolRegistry` 暴露插件工具。
- 写入加载、探活、执行失败和权限拒绝 telemetry。

### ADR-052-E：插件工具必须进入现有 Tool 链路

**决定**：插件执行路径必须是：

```text
AgentExecutionService
  -> ToolInvocationService
  -> PuddingToolExecutionService
  -> AgentFirewall
  -> PluginToolAdapter : IPuddingTool
  -> IPuddingPluginTool
```

禁止路径：

```text
Controller -> Plugin DLL
AgentExecutionService -> Plugin DLL
MessageDeliveryDispatcher -> Plugin DLL
Plugin -> DB / config file
```

原因：

- `ToolInvocationService` 负责运行时 fuse、workspace guard、耗时和结果归一化。
- `PuddingToolExecutionService` 负责 registry 查找、AgentFirewall 和 telemetry。
- `AgentFirewall` 是权限门禁。
- 插件绕过任何一层都会破坏当前工具治理闭环。

### ADR-052-F：Registry 需要支持多来源 Tool

当前 `PuddingToolRegistry` 从 DI 中读取 `IEnumerable<IPuddingTool>`。插件化后应保持兼容，同时引入来源提供者：

```csharp
public interface IPuddingToolSource
{
    string SourceId { get; }
    IReadOnlyList<IPuddingTool> ListTools();
}
```

V0 可采用保守实现：

- 内置工具仍通过 DI `IEnumerable<IPuddingTool>` 注册。
- 插件工具由 `PluginToolSource` 提供。
- `PuddingToolRegistry` 构造时合并内置工具和 source 工具，并继续做 ToolId 唯一性校验。

---

## 6. 权限模型

### ADR-052-G：插件权限分为三层

插件权限必须分层，不允许混用：

| 层级 | 事实源 | 作用 |
| --- | --- | --- |
| 插件宿主权限 | `plugin.json.permissions` | 插件包可以请求哪些宿主能力，例如文件、网络、shell。 |
| Tool 风险权限 | `ToolDescriptor.PermissionLevel` + `ToolSafetyFlags` | 工具本身风险分类，影响能力管理和运行时授权。 |
| Agent 模板授权 | `CapabilityPolicy` | 当前 Agent 是否可见和可执行该工具。 |

插件声明了宿主权限，不代表 Agent 自动获得工具；Agent 模板授权了插件工具，也不代表插件可以使用未声明宿主能力。

### ADR-052-H：权限采用双重门禁

执行前必须同时满足：

1. 插件请求过该能力。
2. 当前 Agent 的 `CapabilityPolicy` 授权了对应 Tool。

示例：

- 插件 manifest 声明 `network: []`，即使 Tool 被模板勾选，也不得发起网络请求。
- 插件 manifest 声明 `filesystem: ["workspace-read"]`，但模板未授权 `plugin_code_search`，模型不可见且执行被 `AgentFirewall` 拒绝。

### ADR-052-I：V0 安全边界是受信本地扩展

**决定**：V0 插件是 in-process DLL，只能定位为受信本地扩展。

必须明确：

- `AssemblyLoadContext` 是依赖隔离，不是安全沙箱。
- 恶意 DLL 可以在进程内执行任意托管代码。
- 对不可信第三方插件，后续必须使用 out-of-process host、MCP adapter 或容器隔离。

V0 必做防线：

- 插件路径限制在 `PUDDING_DATA_ROOT/plugins`。
- manifest 必须声明权限。
- Tool 执行必须走 `AgentFirewall`。
- 插件加载失败不得阻止后端启动。
- 插件异常不得导致 Agent 执行循环崩溃。
- 记录插件加载和执行 telemetry。

---

## 7. 加载生命周期

### 7.1 状态机

```text
Discovered
  -> ManifestInvalid
  -> Disabled
  -> LoadFailed
  -> Loaded
  -> ProbeFailed
  -> Available
  -> Degraded
  -> ExecutionFaulted
```

状态含义：

| 状态 | 含义 |
| --- | --- |
| `Discovered` | 发现插件目录。 |
| `ManifestInvalid` | `plugin.json` 缺失或校验失败。 |
| `Disabled` | 管理配置禁用。 |
| `LoadFailed` | DLL 加载或 entry type 创建失败。 |
| `Loaded` | DLL 已加载，尚未完成探活。 |
| `ProbeFailed` | 探活失败，工具不进入 Agent 可见目录。 |
| `Available` | 探活通过，工具可进入 catalog。 |
| `Degraded` | 插件可用但部分依赖缺失。 |
| `ExecutionFaulted` | 执行阶段发生异常，保留 catalog 但标记异常。 |

### 7.2 Phase 1 manifest-only 加载时机

**决定**：Phase 1 不加载 DLL，但 manifest catalog 可以在运行时重扫。

流程：

```text
PuddingAgent composition root
  -> AddPuddingRuntime(...)
  -> register PluginManifestCatalog / PluginPackageInstaller / PluginToolSource
  -> register AddPuddingToolRegistry(...)
  -> first catalog read loads plugin manifests into PluginCatalog snapshot
  -> PluginToolSource exposes PluginCatalog tools
  -> PuddingToolRegistry merges built-in tools and current PluginToolSource tools
```

说明：

- 插件宿主服务和 `PluginToolSource` 必须先进入 DI，`PuddingToolRegistry` 不持久缓存插件工具快照，而是在 catalog 查询时读取当前 source。
- `GET /api/plugins` 读取当前 catalog snapshot；`POST /api/plugins/reload` 清空 snapshot 并在下一次读取时重扫插件目录。
- `POST /api/plugins/upload` 由 Runtime 侧 `PluginPackageInstaller` 校验 ZIP、校验 `plugin.json`、防路径穿越并安装到 `PUDDING_DATA_ROOT/plugins/{pluginId}`。
- 上传或 reload 后 manifest-only 工具可立即通过 `/api/capabilities` 和 `/admin/capability-management` 出现。
- 修改 DLL 后仍不会生效，因为 Phase 1 不加载 DLL；真正 DLL 加载、版本替换、卸载和 FileSystemWatcher 进入后续阶段。

### 7.3 热卸载暂缓

**决定**：Phase 0 不承诺热卸载。

原因：

- .NET collectible `AssemblyLoadContext` 只有在没有任何静态引用、委托、线程、timer、单例缓存时才能释放。
- 当前工具 registry 是 singleton，热替换会影响 Agent 正在执行的工具调用。
- 先保证启动加载、权限闭环和诊断可靠，再做热卸载更稳。

---

## 8. 与现有 Tool 的关系

### ADR-052-J：插件工具是 ToolDescriptor 的一种来源

插件工具在 `/api/tools` 和 `/api/capabilities` 中应与内置工具一致出现。

当前 capability ID 由 Tool ID 派生：

```text
toolId: plugin_code_search
capabilityId: cap-plugin-code-search
```

这保持与 `CapabilityApiController.ToolIdToCapabilityId(...)` 的现有逻辑一致。

### ADR-052-K：能力管理页面不改职责

`/admin/capability-management` 的职责保持：

- 展示运行时工具注册表。
- 展示工具风险标签。
- 显示注册状态。

后续可增强显示：

- 来源：BuiltIn / Plugin / LegacySkill。
- 插件 ID 和版本。
- 插件状态：Available / ProbeFailed / Disabled。

但它不负责：

- 直接上传 DLL。
- 直接加载 DLL。
- 绕过 Runtime 修改工具 registry。

### ADR-052-L：Agent 模板仍保存 capability selection

Agent 模板继续保存 `SelectedCapabilityIds`，不直接保存 plugin tool implementation type。

原因：

- 模板授权的是能力，不是 DLL 类型。
- 插件升级后，只要 Tool ID 稳定，模板授权继续有效。
- 如果插件删除某个 Tool，解析时应跳过并记录诊断，而不是静默生成不存在的 LLM tool。

---

## 9. 与 SkillPackage 的关系

### ADR-052-M：SkillPackage 和 Plugin 分离

**决定**：不把 DLL 插件塞入现有 SkillPackage。

| 项 | SkillPackage | Plugin |
| --- | --- | --- |
| 主要用途 | prompt、上下文、资源、Skill 文档 | 可执行工具扩展 |
| 存储 | `SkillPackages` 表 + MinIO object | `PUDDING_DATA_ROOT/plugins` + plugin registry |
| 运行态 | 解压后由上下文/Skill 读取 | DLL 加载为 `IPuddingPlugin` |
| 权限 | Agent 模板选择 Skill 包 | Agent 模板选择 Tool capability |
| 安全风险 | 文本和资源为主 | 进程内代码执行 |

后续可以让插件包携带 `skills/` 目录，但这仍是插件贡献的 SkillPackage 内容，不改变两者边界：

```text
plugin.json
bin/plugin.dll
skills/{skillId}/SKILL.md
```

插件贡献的 Skill 必须作为独立贡献项进入 SkillPackage/Skill Registry，而不是由 DLL 在运行时直接拼接 prompt。

---

## 10. Platform 和 API 边界

### ADR-052-N：Platform API 只读插件状态，不执行插件逻辑

建议新增：

```text
GET  /api/plugins
GET  /api/plugins/{pluginId}
GET  /api/plugins/diagnostics
POST /api/plugins/{pluginId}/enable
POST /api/plugins/{pluginId}/disable
POST /api/plugins/{pluginId}/reload
```

这些 API 调用 Runtime 插件服务或插件状态服务，不直接：

- `Directory.GetFiles(...)`
- `AssemblyLoadContext.LoadFromAssemblyPath(...)`
- `Activator.CreateInstance(...)`
- 解析 DLL 类型

### ADR-052-O：Capability API 继续从 Tool Catalog 派生

`CapabilityApiController` 当前已经声明：

> Tool capabilities are derived from the runtime tool registry and cannot be created through this API.

插件化后该原则不变：

- 插件工具进入 `IPuddingToolCatalogService`。
- `/api/capabilities` 自动展示插件工具。
- `/api/capabilities` 不新增 create/update/delete 插件能力。

---

## 11. 可观测性

插件化必须同时记录 Trace 和 Metrics。

### 11.1 Trace

开发环境写入：

```text
data/logs/diagnostics/plugins/YYYYMMDD.jsonl
```

事件：

- `plugin.discovered`
- `plugin.manifest_invalid`
- `plugin.load_failed`
- `plugin.loaded`
- `plugin.probe_failed`
- `plugin.available`
- `plugin.execution_failed`
- `plugin.disabled`

### 11.2 Metrics

写入 `telemetry_metric_events`：

| metric | dimensions |
| --- | --- |
| `plugin.load` | `plugin_id`, `version`, `status`, `duration_ms` |
| `plugin.probe` | `plugin_id`, `status`, `reason_code` |
| `plugin.tool.execution` | `plugin_id`, `tool_name`, `status`, `duration_ms` |
| `plugin.permission.denied` | `plugin_id`, `tool_name`, `permission`, `stage` |

禁止长期保存完整插件参数、完整输出和完整异常堆栈。长期指标保存 hash、长度、分类、短预览和错误码。

---

## 12. 测试基线

Phase 1 开始实现前，测试必须覆盖以下契约：

1. manifest schema 校验。
2. 插件目录路径穿越拒绝。
3. `entry.assembly` 不在插件根目录时拒绝。
4. Tool ID 不满足 `^[a-zA-Z0-9_]+$` 时拒绝。
5. manifest 声明 tool 但 DLL 不提供对应 `ToolId` 时拒绝。
6. DLL 提供 manifest 未声明 tool 时拒绝。
7. 插件 tool 可出现在 `IPuddingToolCatalogService.ListTools()`。
8. 插件 tool 可通过 `/api/capabilities` 派生为 `cap-plugin-...`。
9. 未被 `CapabilityPolicy` 授权时，插件 tool 不进入 LLM tool definitions。
10. 未授权执行时，`AgentFirewall` 拒绝调用。
11. 插件执行异常返回结构化 `ToolExecutionResult.Fail`，不打断 Agent loop。
12. 插件加载失败不阻塞后端启动。

---

## 13. 非目标

Phase 0 不解决：

- 插件市场。
- 远程下载安装。
- 真正热卸载。
- 不可信第三方插件沙箱。
- MCP server 动态管理。
- UI 上传 DLL。
- 插件自动审批。
- 插件跨进程隔离。

这些能力应在 Tool 主链路、权限闭环和诊断闭环稳定后再进入后续 ADR。

---

## 14. 后续阶段

### Phase 1：Manifest-only

- 实现 `PluginManifestLoader` 和 schema 校验。
- 不加载 DLL。
- 插件工具以 `Unavailable` 或 `ManifestOnly` 状态展示。
- `/admin/capability-management` 可看到插件来源和状态。
- 支持上传 ZIP 插件包，先做包结构、路径穿越、manifest 和大小限制校验，再安装到数据根插件目录。
- 支持 catalog reload，使 manifest-only 工具无需重启即可进入工具目录。
- 写入 `data/logs/diagnostics/plugins/YYYYMMDD.jsonl`，记录上传、拒绝、manifest 无效和 manifest-only 可见等基础设施事件。
- 暴露 `GET /api/plugins/diagnostics?pluginId=&limit=`，让管理界面能读取最近插件诊断事件，而不是只依赖文件系统排查。

### Phase 2：DLL 启动加载

- 实现 `IPuddingPlugin` SDK。
- 使用 `AssemblyLoadContext` 启动加载。
- `PluginToolAdapter` 适配为 `IPuddingTool`。
- 插件工具进入 `PuddingToolRegistry`。

### Phase 3：管理和重启生效

- `/api/plugins` 支持 enable/disable/reload。
- V0 reload 返回 `requiresRestart=true`。
- 前端插件管理页展示状态、版本、错误和贡献工具。

### Phase 4：安全和隔离增强

- hash/signature 校验。
- 插件权限 Facade。
- out-of-process host 或 MCP adapter。
- 插件执行资源限制。

---

## 15. 验收标准

Phase 0 完成标准：

1. 插件包目录和 manifest v1 字段明确。
2. Core SDK、Runtime 宿主、Platform API、Agent composition root 的职责明确。
3. 插件权限、Tool 风险权限、Agent 模板授权三层边界明确。
4. 插件加载状态机和 V0 启动加载策略明确。
5. 与 `/admin/capability-management`、`/api/capabilities`、`SkillPackage` 的关系明确。
6. 后续 Phase 1/2 的测试基线明确。
