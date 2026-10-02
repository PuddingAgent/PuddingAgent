# 74 Phase 2A-2 最小 Remote Browser 与 Agent Tools 实施验收报告

> - 状态：**accepted（2026-08-02）**
> - 前置阶段：[73 Phase 2A-1 验收证据与准入](73Phase2A-1验收证据收口与Phase2A-2准入工作指令.md)
> - 适用平台：Windows 10/11、.NET 10、WPF、WebView2 Evergreen Runtime
> - 本批目标：让 DesktopChild 中的 Agent 通过 Core/Desktop Bridge 操作通用浏览器的 Context、Tab 和导航；不加入抖音专用逻辑

## 1. 验收结论

Phase 2A-2 的最小闭环已经完成并通过验收：

```text
Agent Tool
  -> IBrowserRuntime
  -> RemoteBrowserRuntime / RemoteBrowserContext / RemoteBrowserPage
  -> IDesktopBrowserCommandBroker
  -> authenticated Loopback WebSocket Bridge
  -> BrowserWorkspaceController
  -> WebView2BrowserRuntime / Context / Page
```

本批只开放模型已经能够稳定理解的三组基础工具：

- `browser_context`：创建、列出、查询和关闭 Context；
- `browser_tabs`：新建、列出、激活和关闭 Page/Tab；
- `browser_navigate`：跳转、后退、前进、刷新和停止。

Console Host 和禁用浏览器自动化的 Host 不注册上述 Runtime、Broker 或 Agent Tools。只有 Desktop 以 `--desktop-child` 启动且 `BrowserAutomationEnabled=true` 时才开放能力。

本阶段没有实现 DOM Snapshot、Locator、输入、Evaluate、CDP、Cookie、下载、上传或 Douyin 工具。这些调用明确返回稳定错误码 `browser_operation_not_supported`，不会伪装成空结果或静默成功。

## 2. 产品和进程边界

### 2.1 所有权

- Desktop 拥有真实 WebView2 Runtime、Context、Page、Surface、Tab 和 Agent target；
- Core 只持有远程代理对象，不引用 WPF、WebView2 或 `MainWindow`；
- Agent Tools 只依赖 `PuddingBrowser.Abstractions`，不直接知道 Bridge 或 Desktop；
- Remote proxy 的 `DisposeAsync()` 只释放 Core 代理，不关闭 Desktop 拥有的 Context/Page；
- Context/Page 只有收到显式 `close` 工具命令时才改变 Desktop 浏览器生命周期；
- Core Restart 后 Desktop 的 Tab/Context 继续存在，新 Core 重新通过 `context.list` 获取事实。

### 2.2 注册条件

唯一注册入口：

```csharp
public static IServiceCollection AddDesktopBrowserAutomation(
    this IServiceCollection services,
    PuddingHostOptions hostOptions)
```

条件：

```csharp
hostOptions.Mode == PuddingHostMode.DesktopChild
&& hostOptions.BrowserAutomationEnabled
```

满足条件时注册：

```text
IDesktopBrowserConnectionRegistry -> DesktopBrowserConnectionRegistry
IDesktopBrowserCommandBroker       -> DesktopBrowserCommandBroker
IBrowserBridgeClock                -> SystemBrowserBridgeClock
RemoteBrowserRuntime               -> singleton
IBrowserRuntime                    -> RemoteBrowserRuntime singleton
PuddingBrowser.AgentTools assembly -> ToolRegistry
```

不满足条件时不注册空实现，也不让 Console Agent 看见无法调用的 browser tools。

## 3. 项目和类清单

### 3.1 `PuddingBrowser.Abstractions`

新增：

```csharp
public sealed class BrowserOperationException : Exception
{
    public string Code { get; }
}
```

它是 Remote proxy 到 Agent Tool 之间的稳定领域错误载体。Tool 将 `Code` 原样投影到结构化失败结果。

### 3.2 `PuddingBrowser.Protocol`

新增命令：

```text
context.list
context.getInfo
```

新增或扩展 payload：

```csharp
public sealed record ContextGetInfoArguments;
public sealed record BrowserContextListDescriptor;

public sealed record BrowserPageDescriptor
{
    public bool IsAgentTarget { get; init; }
    public bool CanGoBack { get; init; }
    public bool CanGoForward { get; init; }
    public bool IsLoading { get; init; }
}
```

所有 payload 继续进入 `BrowserBridgeJsonSerializerContext`，避免 Desktop/Core 两端使用不一致的隐式序列化契约。

### 3.3 `PuddingHost.BrowserBridge`

#### `RemoteBrowserRuntime`

```csharp
public sealed class RemoteBrowserRuntime : IBrowserRuntime
{
    public BrowserRuntimeState State { get; }
    public Task<IBrowserContext> CreateContextAsync(
        BrowserContextOptions options, CancellationToken ct);
    public Task<IBrowserContext?> GetContextAsync(
        BrowserContextId id, CancellationToken ct);
    public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(
        CancellationToken ct);
    public Task CloseContextAsync(
        BrowserContextId id, CancellationToken ct);
    public IAsyncEnumerable<BrowserEvent> WatchEventsAsync(
        BrowserEventFilter filter, CancellationToken ct);
    public ValueTask DisposeAsync();
}
```

内部统一由 `ExecuteAsync<T>` 创建 `OperationId`、传递 `ContextId/PageId/DeadlineUtc`，调用 `IDesktopBrowserCommandBroker.ExecuteAsync()`，并把失败转换成 `BrowserOperationException`。

#### `RemoteBrowserContext`

```csharp
public sealed class RemoteBrowserContext : IBrowserContext
{
    public BrowserContextId Id { get; }
    public BrowserContextInfo Info { get; }
    public Task<IBrowserPage> NewPageAsync(
        PageCreateOptions options, CancellationToken ct);
    public Task<IBrowserPage?> GetPageAsync(
        PageId id, CancellationToken ct);
    public Task<IReadOnlyList<PageInfo>> ListPagesAsync(
        CancellationToken ct);
    public Task ClosePageAsync(PageId id, CancellationToken ct);
}
```

`ListPagesAsync()` 只返回属于当前 Context 的页面；`GetPageAsync()` 对稳定的 `browser_page_not_found` 返回 `null`，其他 Bridge 错误继续抛出。

#### `RemoteBrowserPage`

```csharp
public sealed class RemoteBrowserPage : IBrowserPage
{
    public PageId Id { get; }
    public BrowserContextId ContextId { get; }
    public long PageVersion { get; }
    public PageInfo Info { get; }
    public bool CanGoBack { get; }
    public bool CanGoForward { get; }
    public bool IsLoading { get; }

    public Task<NavigationResult> GotoAsync(
        Uri url, NavigationOptions options, CancellationToken ct);
    public Task GoBackAsync(CancellationToken ct);
    public Task GoForwardAsync(CancellationToken ct);
    public Task ReloadAsync(CancellationToken ct);
    public Task StopAsync(CancellationToken ct);
    public Task BringToFrontAsync(CancellationToken ct);
}
```

每次导航结果都会刷新 `PageInfo`、历史状态和 loading 状态。尚未开放的 `IBrowserPage` 方法统一返回 `browser_operation_not_supported`。

### 3.4 `PuddingBrowser.AgentTools`

新增独立 `net10.0` 项目，不依赖 Host、WPF 或 WebView2。

#### `BrowserContextTool`

```csharp
public sealed record BrowserContextArgs
{
    public required string Action { get; init; }
    public string? ContextId { get; init; }
}
```

动作：`create | list | get | close`。

#### `BrowserTabsTool`

输入字段覆盖 `Action`、`ContextId`、`PageId`、`InitialUrl` 和 `Activate`。动作：`new | list | activate | close`。

#### `BrowserNavigateTool`

输入字段覆盖 `Action`、`ContextId`、`PageId`、`Url` 和 `TimeoutMs`。动作：`goto | back | forward | reload | stop`。

#### 统一结果

所有工具都返回 JSON 结构：

```json
{
  "success": true,
  "data": {},
  "error": null
}
```

失败结构：

```json
{
  "success": false,
  "data": null,
  "error": {
    "code": "browser_bridge_disconnected",
    "message": "..."
  }
}
```

参数错误使用 `browser_invalid_arguments`；调用方取消继续抛出 `OperationCanceledException`，不能降级为普通工具失败。

## 4. Desktop 命令行为

`BrowserWorkspaceController` 增加 `context.list` 与 `context.getInfo` 分发，并以 Desktop 当前 Context/Page 集合作为唯一事实来源。

由 Bridge 执行 `page.create` 时，新页面自动成为 Agent target。用户在 UI 中切换可见 Tab 只改变 active page，不改变 Agent target。Page descriptor 返回：

```text
IsAgentTarget
CanGoBack
CanGoForward
IsLoading
```

这使 Core 不需要复制 Desktop UI 状态，也不需要访问 WPF ViewModel。

## 5. Agent 默认能力

新增 capability：

```text
cap-browser-context
cap-browser-tabs
cap-browser-navigate
```

新建的通用助手模板默认包含三项能力和对应工具说明。非审计内置模板也包含最小 Browser 工具集。

已经存在于用户 DataRoot 的 Agent 配置不会被静默改写。用户必须在 Agent 配置界面显式选择上述 capability，或创建新的通用助手。这样可以避免产品升级意外扩大既有 Agent 的外部操作面。

## 6. 自动化测试证据

### 6.1 新增测试

- `PuddingBrowser.AgentTools.Tests`：7/7；覆盖描述符、Context、Tabs、Navigate、参数错误、领域错误和取消传播。
- `RemoteBrowserRuntimeTests`：6 个用例；覆盖 descriptor 映射、Desktop 事实源、Context/Page/Goto、not-found、错误码和 Dispose 所有权。
- `BrowserBridgeServiceCollectionExtensionsTests`：DesktopChild 注册与 Console/disabled 隔离。
- `BrowserAgentToolBridgeIntegrationTests`：真实 Tool → Runtime → Broker → 认证 WebSocket → Desktop result 链路。
- `BrowserWorkspaceControllerTests` 新增 Context list 和 Bridge page-create/Agent-target 用例。
- `BuiltInAgentTemplatesTests` 新增非审计模板最小 Browser 工具集断言。

### 6.2 最终命令结果

```text
PuddingBrowser.Protocol build             0 warning / 0 error
PuddingBrowser.AgentTools build           0 warning / 0 error
PuddingHost build                         0 error
PuddingDesktop build                      0 warning / 0 error
PuddingBrowser.AgentTools.Tests           7/7 passed
PuddingHost.Tests                         54/54 passed
PuddingDesktop.Tests                      94/94 passed
BuiltInAgentTemplatesTests filtered       3/3 passed
Release Desktop publish                   passed
git diff --check                          passed
```

Host 仍报告仓库既有的 NuGet 安全警告，例如 Newtonsoft.Json `NU1903` 和 System.Drawing `NU1904`；本批没有隐藏或放宽这些告警。

发布目录 `.tmp-build/phase2a2-minimal-preview` 已验证包含：

```text
PuddingDesktop.exe
core/PuddingAgent.exe
core/PuddingBrowser.AgentTools.dll
core/PuddingBrowser.Abstractions.dll
core/PuddingBrowser.Protocol.dll
core/wwwroot/admin/index.html
core/default-data/agent-template-presets/general-assistant.json
```

## 7. 可见 Desktop smoke

smoke 使用系统 Temp 隔离目录，没有读取或修改 `D:\data`：

```text
publish root : E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\phase2a2-minimal-preview
Desktop PID  : 32744
Core PID     : 7996
Core address : 127.0.0.1:8430
DesktopHome  : %TEMP%\PuddingAgent\phase2a1-browser-2e64a02a5db549da9ac239d52ff7721b\desktop-home
DataRoot     : %TEMP%\PuddingAgent\phase2a1-browser-2e64a02a5db549da9ac239d52ff7721b\data
```

可见操作结果：

1. Desktop 在 Core 停止时保持可用，运行中心可启动 Core；
2. Core 就绪后显示动态 Loopback `127.0.0.1:8430`；
3. Agent Browser 显示 Bridge `Connected`；
4. 可以创建真实 `about:blank` WebView2 标签页；
5. 标签页可成为 Agent target，右侧 Control 显示 `AgentControlling`；
6. 切回 Workbench 后再返回 Agent Browser，标签和 target 仍存在；
7. 点击窗口关闭后 Desktop 正常退出，退出码为 0；
8. Core 子进程被 Desktop 回收，`remainingChildProcessIds=[]`。

自动化集成测试已经覆盖真实 Agent Tool 到认证 Bridge 的命令链；可见 smoke 覆盖 Desktop/WebView2/Bridge/退出表现。本轮没有调用真实 LLM 让模型自主选择 Browser Tool，因此不能把它记录为“真实模型决策 smoke”。

## 8. Definition of Done

- [x] Core 侧 Remote Runtime/Context/Page 不引用 WPF/WebView2。
- [x] Desktop 是 Context/Page/Tab/target 唯一事实源。
- [x] Core Restart 不通过 proxy Dispose 关闭 Desktop 浏览器状态。
- [x] `browser_context`、`browser_tabs`、`browser_navigate` 返回结构化结果和稳定错误码。
- [x] Caller cancellation 原样传播。
- [x] 仅 DesktopChild + BrowserAutomationEnabled 注册工具。
- [x] Console/disabled Host 不暴露 Browser Tool。
- [x] 新 Agent 模板包含三项 capability；既有 Agent 不被静默扩权。
- [x] Tool → Runtime → Broker → authenticated WebSocket → Desktop result 集成测试通过。
- [x] Build、定向测试、Release publish、可见 Desktop smoke 和明确退出全部通过。
- [x] DataRoot、dirty worktree 和用户浏览器 Profile 边界未越界。

## 9. 下一批次：Phase 2A-3

Phase 2A-3 聚焦“模型能够看见并操作页面”，不得提前混入 Douyin DOM 定制：

1. 实现 `PageSnapshot` 和稳定 ref 生成，提供结构化可访问性/DOM 摘要；
2. 实现 Locator resolve、click、fill、type、press、hover、scroll 和 wait；
3. 增加 `browser_snapshot`、`browser_locate`、`browser_interact`、`browser_wait`；
4. 为 Snapshot 设置字符、节点和深度预算，超限返回截断元数据；
5. 实现页面版本校验，旧 ref 返回 `stale_element_reference`；
6. Activity 只记录动作摘要和稳定错误码，不记录表单值、Cookie、Token 或完整脚本；
7. 建立本地 TestSite，覆盖表单、动态 DOM、iframe、popup 和 stale ref；
8. 增加一次真实 DeepSeek Agent 从空白页完成 TestSite 任务的可见 smoke；
9. 完成后再评审 Evaluate/CDP/Cookie/Download/Upload，不与本批混合。

Phase 2A-3 的准入条件是先将上述范围拆成独立开发工作指令和测试矩阵，再开始实现。

> 进度更新（2026-08-02）：Phase 2A-3 的开发契约已写入 [75](75Phase2A-3SnapshotLocatorInteractWait开发工作指令.md)，确定性实现、真实 WebView2 TestSite、Release/Desktop smoke 与验收结果见 [76](76Phase2A-3通用WebView2页面操作实施验收报告.md)。真实 DeepSeek Agent 可见 smoke 仍为进入 Douyin Adapter 前的准入项。
