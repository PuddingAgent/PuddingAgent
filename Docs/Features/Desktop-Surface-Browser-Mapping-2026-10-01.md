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
