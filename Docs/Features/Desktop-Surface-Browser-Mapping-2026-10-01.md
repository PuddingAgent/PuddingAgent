# Desktop 表面 · 浏览器侧映射规格（映射到既有 `IBrowserRuntime`）

> 目的：把 `IDesktopUiSurface` 的浏览器部分实现为**对既有浏览器抽象的映射**，而不是重建 WebView2 逻辑。
> 依据：`Source/PuddingBrowser.Abstractions`（现有七个浏览器工具正基于它工作）；
> 新增的 DOM 脚本生成/解析（`DesktopDomScripts`）因此**不再需要**用于浏览器侧——
> 既有运行时已提供 Goto/Snapshot/Query/QueryAll/Evaluate/Click/Fill/Press/Hover/Scroll/Select/Check/WaitFor。

## 1. 已确认的目标类型（本轮实读，非推测）

```csharp
// 运行时
public interface IBrowserRuntime : IAsyncDisposable {
    BrowserRuntimeState State { get; }
    Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct);
    Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct);
    Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct);
    Task CloseContextAsync(BrowserContextId id, CancellationToken ct);
}

public interface IBrowserContext : IAsyncDisposable {
    BrowserContextId Id { get; }
    BrowserContextInfo Info { get; }
    Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct);
    Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct);
    Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct);
    Task ClosePageAsync(PageId id, CancellationToken ct);
}

public interface IBrowserPage : IAsyncDisposable {
    PageId Id { get; }  BrowserContextId ContextId { get; }
    long PageVersion { get; }        // ← 既有抽象**已有**页面版本
    PageInfo Info { get; }
    bool CanGoBack { get; } bool CanGoForward { get; } bool IsLoading { get; }

    Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct);
    Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct);
    Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct);
    Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct);
    Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct);
    Task ClickAsync(Locator, ClickOptions, CancellationToken ct);
    Task FillAsync(Locator, string value, FillOptions, CancellationToken ct);
    Task TypeAsync(Locator, string text, TypeOptions, CancellationToken ct);
    Task PressAsync(Locator, string key, KeyOptions, CancellationToken ct);
    Task HoverAsync(Locator, PointerOptions, CancellationToken ct);
    Task ScrollAsync(ScrollOptions, CancellationToken ct);
    Task SelectAsync(Locator, IReadOnlyList<string> values, CancellationToken ct);
    Task CheckAsync(Locator, bool isChecked, CancellationToken ct);
    Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct);
    Task BringToFrontAsync(CancellationToken ct);
}

public interface IElementHandle : IAsyncDisposable {
    ElementHandleId Id { get; }  PageId PageId { get; }
    long PageVersion { get; }    // ← 元素也带版本：陈旧 Ref 判定的天然依据
    int? BackendNodeId { get; }  string LocatorFingerprint { get; }
    BrowserElementInfo Info { get; }
    Task<BoundingBox?> GetBoundingBoxAsync(CancellationToken ct);
}
```

关键记录类型（已确认）：

```csharp
public sealed record BrowserContextInfo { required BrowserContextId Id; required string UserDataDirectory; bool Persistent; int PageCount; }
public sealed record PageInfo { required PageId Id; required BrowserContextId ContextId; required string Title; required string Url; long PageVersion; }
public sealed record NavigationOptions { int TimeoutMs = 30_000; string? Referer; }
public sealed record NavigationResult { required Uri Url; bool Ok; int? StatusCode; string? ErrorText; }
public readonly record struct BrowserContextId(string Value);
public readonly record struct PageId(string Value);
public readonly record struct ElementHandleId(string Value);
```

## 2. 九个操作 → 运行时调用（映射表）

| 我方能力 | 运行时调用 | 版本/不变量要点 |
|---|---|---|
| `webview.navigate` | `IBrowserPage.GotoAsync(url, NavigationOptions)` | 成功后用 `page.PageVersion` 作为**新**版本回带；`Ok=false` ⇒ 按错误语义映射（不是成功） |
| `webview.execute_javascript` | `IBrowserPage.EvaluateAsync(BrowserScript)` | 回带请求时的版本（脚本不推进版本） |
| `webview.page_state` | `IBrowserContext.GetPageAsync(pageId)` | 版本取 `PageInfo.PageVersion` |
| `browser.snapshot` | `IBrowserPage.SnapshotAsync(SnapshotOptions)` | 预算来自请求；截断必须如实标注；版本取 `PageVersion` |
| `browser.locate` | `IBrowserPage.QueryAllAsync(Locator)` | **每个 Ref 必须带 `IElementHandle.PageVersion`**；定位为空是成功而非失败 |
| `browser.interact` | `ClickAsync/FillAsync/PressAsync/HoverAsync/ScrollAsync/SelectAsync/CheckAsync` | 交互后**必须回带新的** `page.PageVersion`（旧 Ref 随之作废；服务侧已有 `DesktopMutationInvariants` 兜底） |
| `browser.wait_for` | `IBrowserPage.WaitForAsync(WaitCondition)` | 超时用结果标注（`TimedOut`），**不是失败** |
| `browser.contexts` | `IBrowserRuntime.ListContextsAsync` + 每个 `IBrowserContext.ListPagesAsync` | 每个页面必须带当前版本；`closed` 如实标注 |
| `browser.tabs` | `IBrowserContext.GetPageAsync` / `ClosePageAsync` / `BringToFrontAsync` + `ListPagesAsync` | 操作后回带**新的**活动页状态与剩余清单 |

## 3. 需要保留的既有语义（本系列已投入的成果）

- **Ref 随版本失效**：`IElementHandle.PageVersion` 与 `IBrowserPage.PageVersion` 天然提供依据；
  映射层不得用「当前页面版本」替代元素自身版本。
- **变更类必须推进版本**：`DesktopMutationInvariants` 已在服务咽喉点强制，映射层必须回带真实版本（不得伪造）。
- **预算与隐私**：快照预算由 `DesktopCapabilityBudgets` 强制；剪贴板/路径不进日志。
- **失败语义**：`NavigationResult.Ok=false`、`ErrorText` 等既有信号要映射成对应的 `CapabilityError`，
  不能一律 `internal_error`（否则上层无法区分"页面不存在"与"运行时坏了"）。

## 4. 实现前需再确认的项（不猜）

1. 我方 Contracts 的确切构造签名（`DesktopContexts` / `DesktopPageInfo` / `DesktopContextInfo` /
   `DesktopTabsResult` / `DesktopPageState` / `NavigateResult` / `DesktopSnapshot` / `DesktopLocateResult` /
   `DesktopInteractionResult` / `DesktopWaitResult` / `BrowserScript` 的构造形态）——
   实现时逐个读取，不凭记忆写。
2. `Locator` 的 `Kind` 枚举与我方 `DesktopLocatorKind` 的对应关系（需要逐项对齐，含不支持的策略如何拒绝）。
3. `BrowserScript`（脚本值）与 `BrowserScriptValue` 的取值形态（JSON 文本/句柄）。
4. `WaitCondition` 的判别字段与我方 `DesktopWaitConditionKind` 的对应关系。

## 5. 测试策略（关键收益）

因为映射只依赖接口，**可用假 `IBrowserRuntime` 完整测试**（不需要 WebView2）：

- `FakeBrowserRuntime`/`FakeBrowserPage` 记录调用、可注入版本推进与失败；
- 覆盖：导航成功推进版本、脚本不推进版本、定位项带元素版本、交互后版本推进、等待超时不是失败、
  上下文清单带版本、标签页关闭后回带新活动页、`Ok=false` 的错误映射。
- 这批测试与 WinUI 无关，因而可在 CI/离线完整运行——这正是"UI 操作统一经过 DesktopService"能落地的原因。

## 6. WinUI 薄层的落点与实现规格（第 65 轮实读确认）

### 6.1 落点（**编译期边界决定，不能放错**）

| 内容 | 允许放哪 | 依据（实读） |
|---|---|---|
| 组合装配（把调度器 + 两个端口接到 `DesktopService`/`DesktopCapabilityHost`） | **`PuddingDesktop` 应用**（组合根） | `PuddingDesktop.CapabilityHost` 有边界目标 `EnforceCapabilityHostBoundary`：**只允许引用 `Pudding.Contracts`**（"service logic belongs to Pudding.DesktopService"）⇒ 它 **不能** 引用 DesktopService/DesktopSurface.Browser |
| `WinUiDesktopUiDispatcher`（已完成） | `PuddingDesktop.CapabilityHost` | 只依赖 Contracts ✓ |
| `IDesktopShellFacilities` 实现（对话框/文件选择器/剪贴板） | `PuddingDesktop`（需要窗口 HWND / XamlRoot / WinRT 剪贴板） | 需要 Windows App SDK + WinRT，CapabilityHost 有边界但同为 WinUI 工程，**实现放应用内最简**（也可放 CapabilityHost 的独立文件，只要不引 DesktopService） |
| `IDesktopBrowserTargetRegistry` 的**驱动**（页面创建/关闭/切换） | `PuddingDesktop`（`MainWindow` 的页面生命周期回调） | 注册表实现已在 `Pudding.DesktopService.BrowserTargetRegistry`（平台无关、已测） |
| 浏览器 9 项能力的映射（已完成） | `Pudding.DesktopSurface.Browser` | 只依赖抽象 ✓ |

### 6.2 三个动作的 WinUI 实现要点（实现时逐个确认，勿凭记忆）

- **对话框**：`ContentDialog` 必须设置 `XamlRoot`（无 `XamlRoot` 会抛异常）⇒ 端口实现需能取到主窗口内容根的访问器；
  `ShowAsync()` 的 `ContentDialogResult`（`Primary`/`Secondary`/`None`）↔ 我方 `DesktopDialogChoice` 的映射要**显式**写清：
  把 `None`（用户按 ESC/点遮罩关闭）映射为 **`Cancel`**（取消是结果，不是失败）。
- **文件选择器**：`Windows.Storage.Pickers.FileOpenPicker` 在桌面应用中必须先 `InitializeWithWindow`（传窗口 HWND），
  否则会抛 `COMException`；`PickSingleFileAsync`/`PickMultipleFilesAsync` 返回 `null` ⇒ **取消**（映射为 `Canceled=true`）。
- **剪贴板**：`Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()` + `GetTextAsync()`；
  非文本内容 ⇒ 返回 `HasText=false` 的成功结果（**不是错误**）；读取必须在 UI 线程。
  内容与路径**不得写日志/审计**（契约已声明）。
- **线程访问**：`WinUiDesktopUiDispatcher` 的 `TryEnqueue` 失败即抛（不悬挂）；真实 `DispatcherQueue` 下的
  线程访问验证属窗口期验收项（`HasThreadAccess` 分支要真的被走到）。

### 6.3 组合根要做的四件事（`PuddingDesktop`）

1. 读 `desktop.json` 的 `Desktop:CapabilityChannel`（`DesktopCapabilityChannelSettings`，缺省关闭）；
2. 关闭时**什么都不做**（继续走既有 WebSocket Bridge，行为与今天一致）；
3. 启用时：`DesktopCapabilityChannelSettings.CreateConnectionOptions(...)` + `ResolveTransportFromDescription(...)`
   解析 Core 发布的分段描述（解析失败即**不启通道**，不做跨传输回退）；
4. 构造 `DesktopCapabilityHost(dispatcher, surface, options, supervisorFactory, auditSink)` 并 `StartAsync`；
   同时**在页面生命周期回调里驱动 `BrowserTargetRegistry`**（创建即登记信任级别、关闭即注销、切换即设活动页）。

> 上述 4 步是**唯一会改变产品行为**的部分，必须在重启窗口内由外部控制器验收（见外部验收单）。
## 7. 组合根配方（第 69 轮实读 `DesktopCapabilityHost` 后确定）

### 7.1 宿主的确切契约（实读，不凭记忆）

```csharp
// 构造：选项 + 监督器工厂
new DesktopCapabilityHost(DesktopCapabilityHostOptions options, Func<IDesktopConnectionSupervisor> supervisorFactory);

public sealed record DesktopCapabilityHostOptions {
    public required DesktopInstanceId DesktopId { get; init; }
    public DesktopCapabilityTransportMode Mode { get; init; } = DesktopCapabilityTransportMode.GrpcCapabilityChannel;
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public bool EnforceSingleActiveTransport { get; init; } = true;   // 同一 DesktopId 只允许一个活动传输
}

// 真实监督器（gRPC 双向流 + 退避重连）
DesktopCapabilityHost.CreateGrpcSupervisorFactory(
    IDesktopChannelStreamFactory streamFactory,      // GrpcDesktopChannelStreamFactory
    IDesktopCapabilityExecutor executor,              // DesktopService 一侧的执行器接缝
    DesktopConnectionOptions connectionOptions,       // 来自 DesktopCapabilityChannelSettings.CreateConnectionOptions
    TimeSpan? initialBackoff = null, TimeSpan? maxBackoff = null, double jitter = 0.2);

host.StateChanged += state => ...;   // 状态变化（转发自监督器）
await host.StartAsync(ct);           // 返回时监督循环已在运行（不等待握手）
await host.StopAsync(ct);            // 可重复调用；释放单实例占用
```

### 7.2 **必须避开的陷阱**（实读发现）

`DesktopCapabilityHost.StartAsync` 在 `Mode == LegacyWebSocketBridge` 时**直接抛 `InvalidOperationException`**
（"不因为 gRPC 没起来就静默切回旧 Bridge"）。

⇒ 组合根的正确形态是：**关闭时根本不构造、不启动宿主**，而不是"构造后用旧模式启动"。
这与 `DesktopCapabilityChannelPreflight.Evaluate(...).ShouldStart == false` 的分支天然吻合：

```csharp
var preflight = DesktopCapabilityChannelPreflight.Evaluate(settings, processInstanceId, authentication, endpointDescription);
if (!preflight.ShouldStart) {
    // 什么都不做：继续走既有 WebSocket Bridge，行为与今天一致（回滚即回到这里）。
    log(preflight.Summary);
    return;
}
var connectionOptions = settings.CreateConnectionOptions(processInstanceId, authentication).Value;
var host = new DesktopCapabilityHost(
    new DesktopCapabilityHostOptions { DesktopId = new DesktopInstanceId(settings.DesktopId) },
    DesktopCapabilityHost.CreateGrpcSupervisorFactory(streamFactory, executor, connectionOptions));
host.StateChanged += state => log($"[CapabilityChannel] {state}");   // 不含页面内容/凭据
await host.StartAsync(lifetimeToken);
```

### 7.3 装配清单（`PuddingDesktop` 组合根，默认关闭）

1. 读 `desktop.json` 的 `Desktop:CapabilityChannel`（映射为 `DesktopCapabilityChannelSettings`；缺省即关闭）；
2. 解析 Core 发布的**端点描述**（就绪协议/stdout；由 Core 侧打印，格式 `kind:address|protocolVersion|coreInstanceId`）；
3. 调 `DesktopCapabilityChannelPreflight.Evaluate(...)`；`ShouldStart == false` ⇒ 只记日志、**什么都不做**；
4. 启动时组装：`WinUiDesktopUiDispatcher(DispatcherQueue)`（已在 CapabilityHost）+
   `WinUiShellFacilities(主窗口句柄, XamlRoot 访问器)`（本轮已实现）+ 浏览器表面（`BrowserRuntimeDesktopSurface` +
   既有 `WebView2BrowserRuntime`）+ `BrowserTargetRegistry`（在页面生命周期回调里驱动）；
5. 退出路径：`await host.StopAsync()`（在 Desktop 退出/切回旧传输时），确保单实例占用被释放。

> 其中第 4 步的"浏览器表面 + 注册表驱动"需要 `PuddingDesktop` 的 WebView2 宿主与页面生命周期接入点；
> 第 1~3、5 步只依赖已完成的平台无关件。

### 7.4 仍需在重启窗口内由外部控制器判定的项

- 关闭态：REST 正常、无新端点、无 `pudding-capability-*` 管道（行为与今天逐字一致）；
- 启用态：握手成功、无凭据被拒、Desktop 退出后 Core 侧注册表清空且管道释放；
- 真实 `DispatcherQueue` 下的线程访问（`HasThreadAccess` 分支要真的被走到）。

## 8. 本轮（第 73 轮）进展与剩余（逐项实测，不推测）

### 8.1 已落地并通过验证

| # | 内容 | 落点 | 验证 |
|---|---|---|---|
| 1 | 就绪端点描述变成**可解析**（并进 `PUDDING_DESKTOP_READY`） | `CapabilityChannelReadySignal`、`PuddingApplicationHost`、`PuddingAgent/Program.cs`、`CoreReadyMessage(Parser)` | 适配器 39/39（含版本段必须为整数、`v` 前缀必须被拒）；Desktop 266/266（含未知字段向前兼容）。**此前启用后通道永不启动** |
| 2 | Shell 放行并引用 Desktop 侧能力通道组件 | `PuddingDesktop.csproj` | Shell 0 错误；restore 后确认 `Grpc.Net.Client`/`Grpc.Core.Api` 等已进输出目录 |
| 3 | `IDesktopShellHostFacilities`（窗口/托盘/通知端口）+ `DesktopShellSurface` 扩到 5 项 | Contracts / DesktopService | DesktopService 150/150（通知未弹出是结果、只读状态不重复补齐、异常不越界） |
| 4 | `DesktopSurfaceComposition`（14 项各归其位） | DesktopSurface.Browser | 54/54：用 Shell 侧哨兵把「接错协作者」变成可判定 |
| 5 | 目标注册表驱动桥 + **撤销 Agent 目标** | DesktopSurface.Browser | 54/54（14 项新用例）；`SetAgentTarget` 关闭了「Agent 目标只增不减」的授权漏洞 |

合计 7 套件 517 用例 + 真实端点探针 53/53。

### 8.2 剩余（**唯一尚未接线的一段**，全部在 Shell 内）

1. `IDesktopShellHostFacilities` 的 Shell 实现：窗口形态/托盘可见性 + 托盘气泡通知
   （通知需要托盘图标的 HWND 与 uID，因此实现方必须是 Shell 本身，见 `DesktopTrayIcon`）；
2. 在 `MainWindow` 拿到 `IBrowserRuntime` 与 `BrowserWorkspaceController` 之后，
   把 `DesktopTargetRegistry` + `BrowserTargetRegistry` 交给 `BrowserWorkspaceTargetBridge`，
   并在页面创建/激活/关闭/版本推进/Agent 目标变更这几处各调一行；
3. 组合根：读 `desktop.json` 的 `Desktop:CapabilityChannel` → 从 `CoreReadyMessage.CapabilityEndpoint`
   取描述 → `DesktopCapabilityChannelPreflight.Evaluate` → `ShouldStart` 时
   `DesktopCapabilityHostFactory.Create` → `StartAsync`；退出路径 `StopAsync`。

> **并发注意（不是技术难题，是工程约束）**：`Source/PuddingDesktop/MainWindow.xaml.cs` 与
> `MainWindow.xaml` 当前有**另一方 AI 的未提交改动**（运行中心内存口径：专用工作集 + 副标题）。
> 上述第 2 步必须改动同一个文件，因此**接手前先确认该改动已提交或已确定归属**，
> 否则提交时会把他方 WIP 一起带进去（违反仓库卫生纪律）。
> 该文件的改动区（约第 195 行 `UpdateRuntimeMetrics`）与接线区（约第 926 行浏览器工作区初始化）不重叠。
