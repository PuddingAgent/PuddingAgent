---
title: "Desktop 表面 · 浏览器侧映射规格（映射到既有 `IBrowserRuntime`）"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: active
description: "csharp // 运行时 public interface IBrowserRuntime : IAsyncDisposable { BrowserRuntimeState State { get; } Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct); Ta"
categories: [docs, features]
tags: [desktop, surface, browser, mapping, features]
related_docs: []
related_files: [Source/PuddingDesktop/MainWindow.xaml.cs, Source/PuddingDesktop/code_map.md, Source/PuddingHost/BrowserBridge/RemoteBrowserRuntime.cs, Source/Pudding.CapabilityBroker/DesktopSession.cs]
slug: features-desktop-surface-browser-mapping-2026-10-01
draft: false
---

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
| 6 | **组合根做成可测试组件调用**（`DesktopCapabilityChannelComposition`） | DesktopService | DesktopService **170/170**（+10）：关闭 = 什么都不做、启用却起不来 = **明确失败**、不做跨传输回退；顺带验证「没有真实 Core 时也能安全启动与停止」 |
| 7 | **回写观察到的页面版本**（`ExecuteAsync` 拆出 `ExecuteCoreAsync`，唯一出口记录） | DesktopService | 同套件（+10）：准入按注册表版本判 `page_version_mismatch`，此前无人回写 ⇒ **第一个带版本的操作就被拒**（通道"握手成功却什么也做不了"）；已按能力逐项钉住 |
| 8 | `desktop.json` 能力通道段（§8.3 **方案 A** 已落地） | WpfArchive + 桌面测试 | `PuddingDesktop.Tests` **273/273**（+7）：缺席 ⇒ 关闭、段名可两文件复制、**文件缺省值 == 组件缺省值**、缺席段保存时不出现 |
| 9 | `IDesktopShellHostFacilities` 的 WinUI 实现 | CapabilityHost | 0 警告 0 错误（并核对产物时间戳确认真的编译了新代码） |
| 10 | **Shell 接线完成**：页面生命周期驱动注册表 + 组合根调用点 | `DesktopApplicationCoordinator(.Capability).cs`、`MainWindow`（只暴露宿主事实）、`DesktopTrayIcon.ShowBalloon`、`CoreProcessSession.CapabilityEndpoint` | Shell **exit 0 / 0 错误**，我的文件 0 警告；**启用态本身待外部窗口验收**（见 §8.2） |

合计 7 套件 **537** 用例（Contracts 96、Rpc.Protocol 20、DesktopConnection 80、**DesktopService 170**、
DesktopSurface.Browser 54、CapabilityBroker 78、CapabilityBroker.AspNetCore 39）
+ 真实端点探针 **53/53**；另 `PuddingDesktop.Tests` **279**（桌面启动器侧，不计入上面 7 套）。

### 8.2 剩余（**Desktop 侧接线已完成**；剩下的只有外部窗口验收）

Shell 内两处接线已于 2026-10-02 落地（提交 `cd2e1d9`）：

1. **页面生命周期驱动目标注册表** — `DesktopApplicationCoordinator.Capability.cs` 的
   `AttachBrowserTargets` 订阅控制器 `Tabs.CollectionChanged` 与 `PropertyChanged`，
   把创建/关闭/激活/Agent 目标变更翻译给 `BrowserWorkspaceTargetBridge`；
   页面版本**登记为"尚未观测"**，由 `DesktopService` 在唯一出口用能力结果回写。
2. **组合根调用点** — Core 就绪时 `OnRuntimeChanged` 触发
   `StartCapabilityChannelAsync(session, token)`：读 `desktop.json` 段 → 组装
   `DesktopSurfaceComposition` → `DesktopCapabilityChannelComposition.StartAsync`；
   Core 不再就绪时 `StopCapabilityChannelAsync` 释放单实例传输名额。
   端点描述来自 `CoreProcessSession.CapabilityEndpoint`（就绪信号搬运，不含凭据）。

**仍然只能由外部控制器判定**（Agent 不能验收承载自身的生命周期）：启用态真实 `DispatcherQueue`
线程访问、拨入握手与世代、Desktop 退出后 Core 侧注册表清空与管道释放、以及关闭态逐字无变化。
⇒ 验收动作仍以接线手册 §6 的 8 步为准。

**另注**：Core 侧授权器仍是 `DenyAll`（等切片 D 的调用点提供可信调用方身份），
因此启用态下**能力会被 Core 拒绝**——这是既定的 fail-closed 设计，不是缺陷。

> **并发注意（工程约束）**：`Source/PuddingDesktop/MainWindow.xaml.cs` 与 `MainWindow.xaml` 里仍有
> 另一方 AI 未提交的运行中心内存口径改动。本轮接线用 `git apply --cached` **只暂存自己的 hunk**
> 并核对暂存 blob 不含他方代码；`MainWindow.xaml` 完全未纳入。后续若再改这两个文件，同样需要这样处理。
> `Source/PuddingDesktop/code_map.md` 等索引文件当期正被他方批量迁移，索引登记待其落地后补。


### 8.3 `desktop.json` 段形态（**已裁定并落地 = 方案 A**）

`DesktopCapabilityChannelSettings.SectionName` 常量写着 `Desktop:CapabilityChannel`（与 Core 同名段对称），
而 `desktop.json` 现有段（`Window`/`ToolWorkspace`/`Debug`）都在根上、没有 `Desktop` 这一层。两种形态都说得通：

| 方案 | desktop.json 形态 | 结论 |
|---|---|---|
| **A** | `{ "desktop": { "capabilityChannel": { "enabled": true } } }` | **已采纳**：与 `SectionName`、与 Core 的 `system.json` 完全同名 ⇒ 同一段配置可在两文件间原样复制 |
| B | `{ "capabilityChannel": { "enabled": true } }` | 不采纳：与常量不一致 ⇒ 两文件段名不同，运维容易写错 |

落盘取舍也已按预判处理：`FileDesktopBootstrapSettingsStore` 整体序列化且**没有** `WhenWritingNull`，
因此该段做成**可空**（缺席 = 从未配置 = 关闭），避免用户没写过的安全相关开关因为「保存了一次设置」
就出现在文件里；`Enabled=false` 与「段缺席」语义等价但**不互相改写**。

映射成 `DesktopCapabilityChannelSettings` 的代码**只能待在 Shell**——
`Pudding.DesktopService` 的边界目标禁止引用任何含 `PuddingDesktop` 的项目，故映射不可下沉到组件；
文件形态因此是独立值对象，缺省值一致性由 `PuddingDesktop.Tests` 的跨侧断言钉住。

### 8.4 切片 D 不能靠「替换 `IBrowserRuntime` 实现」完成（实读后的结论，改变下一步做法）

七个浏览器工具（`Source/PuddingBrowser.AgentTools/`）都通过主构造函数拿 `IBrowserRuntime`
（`BrowserContextTool` / `BrowserTabsTool` / `BrowserNavigateTool` / `BrowserSnapshotTool` /
`BrowserLocateTool` / `BrowserInteractTool` / `BrowserWaitForTool`），
Core 侧实现是 `Source/PuddingHost/BrowserBridge/RemoteBrowserRuntime.cs`，
在 DI 里注册为 `services.AddSingleton<IBrowserRuntime>(sp => sp.GetRequiredService<RemoteBrowserRuntime>())`。

因此很自然会想：**换掉这个注册**就等于迁移完成。实读后确认**不可行**：

| 面 | 成员规模 | 能力通道覆盖 |
|---|---|---|
| `IBrowserRuntime` + `IBrowserContext` | 约 12 | 部分（上下文**只读**可映射；创建/关闭上下文无对应能力） |
| `IBrowserPage` | **约 35**（CDP / 截图 / PDF / DevTools / 订阅 / 拖拽 / 选择 / 勾选 / 上传文件 / cookie / 权限 / 前进后退 / 刷新 / 停止 / 置前 / 句柄求值…） | 约 12（九项浏览器能力对应的操作） |

⇒ 任何"直接实现 `IBrowserRuntime`"的适配器都有二十多个成员必须抛 `NotSupported`：
工具会在运行期被打成半残，而**编译期完全看不出来**。正确做法是**按操作迁移**——
为七个工具实际执行的操作提供窄端口（或逐能力替换调用点），
让「能力通道覆盖不到的操作」在**类型层面**就不存在，而不是留在一个宽接口里等运行期爆炸。

**下一步建议顺序**：① 先给七个工具实际用到的操作抽窄端口（`contexts`/`tabs`/`navigate`/`snapshot`/
`locate`/`interact`/`wait_for` 各自的最小面）；② 用 `DesktopSession` 的对应方法实现该端口
（它已经暴露了这 14 个类型化调用）；③ 由组合根按开关选择「窄端口走能力通道 vs 既有 Bridge」，
**不做跨传输回退**。①②③ 每一步都可独立测试，不需要一次性替换整个宽接口。

> 相关：`DesktopSession`（`Source/Pudding.CapabilityBroker/DesktopSession.cs`）已公开
> `NavigateAsync` / `SnapshotAsync` / `LocateAsync` / `InteractAsync` / `WaitForAsync` /
> `GetContextsAsync` / `TabsAsync` / `ExecuteJavascriptAsync` 等类型化调用，
> 因此第 ② 步不需要新的协议能力。

### 8.5 迁移第七步之前必须先**加宽契约**：工具面 ⊃ 能力面（实读七个工具的参数后确认）

§8.4 的 ①②③ 已完成（窄端口 + 两个 9/9 实现）。但真正开始改工具调用点之前，本轮实读七个工具
的参数记录，发现一个**会让迁移变成功能倒退**的问题：**能力契约只覆盖工具面的一个子集**。

| 工具 | 工具面（`*Args`） | 能力契约覆盖 | 缺口 |
|---|---|---|---|
| `browser_context` | `create` / `list` / `get` / `close` | 仅 `browser.contexts`（清单） | **创建 / 关闭上下文无对应能力**（`get` 可由清单过滤） |
| `browser_navigate` | `goto` / `back` / `forward` / `reload` / `stop` + `TimeoutMs` | 仅 `webview.navigate`（=goto） | **back / forward / reload / stop**；**`TimeoutMs`** |
| `browser_tabs` | `new` / `list` / `activate` / `close` + `Url` + `Activate` | `browser.tabs`（activate/close）+ `browser.contexts`（list） | **新建标签页**（含初始 URL 与是否激活） |
| `browser_interact` | `click` / `fill` / **`type`** / `press` / `hover` / `scroll` / `select` / `check` + **`DeltaX`** | `browser.interact`（8 动作，含 `Fill`） | **`type`**（与 `fill` 语义不同）；**`DeltaX`**（当前只映射了 `DeltaY`） |
| `browser_snapshot` | 上述 + `IncludeHidden` / `IncludeIframes` / `IncludeShadowDom` / `MaxDepth` | `browser.snapshot` | **四个参数**（契约 `DesktopSnapshotOptions` 没有它们） |
| `browser_locate` | `Locator` | `browser.locate` | ✓（`Ref` 定位由 Desktop 侧注册表处理） |
| `browser_wait_for` | 三个条件 + `TimeoutMs` | `browser.wait_for`（三条件 ✓） | **`TimeoutMs`** |

⇒ **七个工具里只有 `locate` 是完整可迁移的**。若照原计划直接改调用点，结果是
**能力倒退**（context 的 create/close、navigate 的四个动作、新建标签页、`type`、
快照四个参数、三处超时、`DeltaX` 全部消失）——而且这种倒退在编译期与单元测试里都看不出来，
只有在 Agent 真实用到那些动作时才暴露。

**因此修正后的下一步顺序**：

1. **加宽契约**：为缺口补能力/字段——上下文 `create`/`close`、导航 `back`/`forward`/`reload`/`stop`、
   标签页 `new`、交互 `type`、以及超时与快照/滚动参数。这一步要动 **proto（payload/outcome oneof）+
   两侧映射 + Desktop 侧实现 + 探针断言**，属于跨侧协议变更，必须**两侧同提交**并补探针；
2. 每加一项，**两侧窄端口实现同时补齐**（本轮已把流程跑通：先实读形状 → 再映射 → 再逐项测试）；
3. 工具调用点**按能力就绪度逐个迁移**（先 `locate`，它现在就能迁），并保持"开关二选一、不回退"；
4. 未迁移的工具继续走既有 Bridge —— 这与计划的分阶段迁移一致，且**任何时候都不出现功能倒退**。

> 判断依据（本轮实读）：`BrowserContextArgs` / `BrowserNavigateArgs` / `BrowserTabsArgs` /
> `BrowserInteractArgs` / `BrowserSnapshotArgs` / `BrowserLocateArgs` / `BrowserWaitForArgs`，
> 以及能力侧的 `DesktopCapabilityRequest` 联合与各 `*Options` 记录。

#### 8.5.1 加宽清单（**请求面 + 响应面**都已逐项实读，可直接照此施工）

上面只审计了**参数**；随后又审计了七个工具的**响应值**记录（`Browser*ToolValue`）与契约结果 DTO，
发现响应面同样有缺口（例如元素 `BoundingBox`）。**完整清单**如下——按此施工即可避免
"迁移 = 削功能"（前两轮各只发现了一半，第三次审计才把两面凑齐）：

| # | 缺口 | 方向 | 位置 |
|---|---|---|---|
| 1 | 上下文 `create` / `close` | **能力** | 目录 + proto payload oneof + Desktop 侧实现 |
| 2 | 上下文 `Persistent` | 结果 | `DesktopContextInfo`（`PageCount` 可由 `Pages.Count` 得到，不算缺口） |
| 3 | 导航 `back` / `forward` / `reload` / `stop` | **能力** | 目录 + proto + Desktop 侧实现 |
| 4 | 导航 `TimeoutMs` | 请求 | `NavigateRequest` |
| 5 | 导航结果 `NavigationOk` / `StatusCode` / `ErrorText` | 结果 | `NavigateResult`（当前只有 Disposition/Url/Version） |
| 6 | 标签页 `new`（含 `Url` 与是否激活） | **能力** | 目录 + proto + Desktop 侧实现 |
| 7 | 交互 `type`（与 `fill` 语义不同） | **能力** | `DesktopInteractionAction` + proto |
| 8 | ~~交互 `DeltaX`~~ | — | **已更正（2026-10-02）：不是契约缺口**——proto `InteractCommand.delta_x` 与 `Pudding.DesktopConnection.Mapping` 本来就带它，缺口只在 Bridge 适配器（只映射了 `DeltaY`，已修并加测试） |
| 9 | 快照 `IncludeHidden` / `IncludeIframes` / `IncludeShadowDom` / `MaxDepth` | 请求 | `DesktopSnapshotOptions` + proto `SnapshotBudget` |
| 10 | 定位结果 `BoundingBox` | 结果 | `DesktopElementRef` |
| 11 | ~~等待 `TimeoutMs`~~ | — | **已更正（2026-10-02）：不是契约缺口**——契约 `BrowserWaitForRequest.TimeoutMs`、线缆 `WaitForCommand.timeout_ms`、Core 与 Desktop 两侧映射本来都带它，缺口只在 Bridge 适配器（未传，已修并加测试） |
| 12 | `DesktopPageState.Title` | 结果 | **新发现（2026-10-02，迁移第二、三个工具时）**：`browser_navigate` / `browser_wait_for` / `browser_interact` / `browser_tabs` 的结果里 `BrowserTabToolValue.Title` 是**必填**，而契约 `DesktopPageState` 只有 Target/Url/Version/Readiness，**没有标题** ⇒ 迁移这四个工具前必须补（proto `PageStateOutcome.title` + 契约 + 两侧映射 + 两侧实现），否则等于削掉标题 |
| 13 | `fill`/`type` 的**空文本** | 请求 | **迁移 `browser_interact` 时发现（2026-10-02）**：契约要求 `text` 非空（`BrowserInteractRequest` 把空串归一化为 `null` 后即报错），而 proto3 的 `string text` **无法区分空串与未设** ⇒ 「用空文本清空输入框」这个用法在能力通道上表达不了。当前选择**明确拒绝**（`browser_invalid_arguments`），不静默当成清空；若要恢复该用法，需把 `text` 改成 `optional string` 或另加一个显式标志 |

> 说明：`BrowserTabToolValue.Title` 在契约里是 `string?`，而工具值为 `required string`
> ⇒ 迁移时回退空串即可，**不算缺口**（但要在实现里显式处理，不能假定非空）。
> 每项都属于跨侧协议变更 ⇒ 必须**两侧同提交**并补探针断言（本系列的门禁核心）。

#### 8.5.2 缺口清单施工结果（2026-10-02，逐项实测）

上表的 14 项**全部处理完毕**；逐项落点如下（详细证据见 `Docs/00_changelog/2026Year/10/`，此处只记"现在是什么"）。

| # | 状态 | 落点 |
|---|---|---|
| 1 | ✅ | 上下文创建/关闭用**新能力名** `browser.context.create` / `browser.context.close`（proto payload oneof `24`/`25`、outcome `25`/`26`；目录 + 策略表 + 声明集合 + 两侧实现 + 四个端口的实现/转发）——目录规则要求改变同名能力语义必须换名，故 `browser.contexts` 保持只读 |
| 2 | ✅ | `ContextInfo.persistent` 贯通两侧 |
| 3 | ✅ | 导航 `back`/`forward`/`reload`/`stop`：`NavigateCommand.action` + 冻结线名 |
| 4 | ✅ | 导航 `TimeoutMs`：`NavigateCommand.timeout_ms` |
| 5 | ✅ | 导航结果事实：`NavigateOutcome.navigation_ok` / `status_code` / `error_text`（+ 标题，见 #14）。导航被拒仍是**事实**而非失败 |
| 6 | ✅ | 标签页 `new`：`TabsCommand.context_id` / `url` / `activate`；两侧一次性建页（`PageCreateOptions.InitialUrl/Activate`），不再"先建后导航再置前" |
| 7 | ✅ | 交互 `type`：`DesktopInteractionAction` + 线名 |
| 8 | ✅ | **不是契约缺口**：proto 与 Desktop 侧本就带 `delta_x`，缺口在 Bridge 适配器（只映射 `DeltaY`）⇒ 已修并加测试 |
| 9 | ✅ | 快照四旋钮：`SnapshotBudget` +6..+9 |
| 10 | ✅ | 定位结果 `BoundingBox`：`ElementRef.bounding_box` + `ElementBox` |
| 11 | ✅ | **不是契约缺口**：契约/线缆/两侧映射本就带超时，缺口在 Bridge 适配器未传 ⇒ 已修并加测试 |
| 12 | ✅ | 页状态标题：`PageStateOutcome.title` + 两侧；工具值仍按 `string?` 防御性处理 |
| 13 | ✅ | `fill`/`type` 空文本：`InteractCommand.text` 改为 **`optional string`**（presence 区分"没给"与"给了空串"）⇒ 空文本 = 清空输入框；`press` 仍要求非空键 |
| 14 | ✅ | `NavigateResult.Title`：复用 `PageStateOutcome.title` |

**工具迁移**：七个 Browser Agent Tools **全部**走窄端口（`IDesktopBrowserCapabilitySurface` /
`IDesktopContextCapabilitySurface`）+ 上下文工厂 + 组合根路由（通道就绪走通道，否则 Bridge，二选一且**不回退**）；
旧的静态解析器已删除。

**剩余（不是缺口，且都需要外部窗口）**：

- 能力通道**默认开启**与**退役 WebSocket Bridge 的 Browser 部分**：判据是"窗口内通道调用 > 0 且旧 Bridge
  回退 = 0"，证据面已就绪（`GET /api/admin/transport/status`），执行步骤见
  `Docs/13_runbooks/能力通道外部验收运行手册-2026-10-02.md`；
- 真实 Desktop 会话端到端与生命周期（启动/重启/崩溃/退出回收）：**只能由进程外控制器判定**。

**权限证据链**（本次工作副产物的加固项）：证据类型/生产者/携带者/观察者/第二阶段闸门已落地，
第三阶段与会话层闸门**有意暂缓**（理由见 `Docs/12_features/桌面能力链路权限证据设计-2026-10-02.md`）。

**验证基线**：九个套件全绿，合计 **810** 用例（协议 20 / 契约 97 / 能力代理 90 + 43 / Desktop 连接 80 /
Desktop 服务 170 / 浏览器表面 54 / Host 239 / 工具 17）。
