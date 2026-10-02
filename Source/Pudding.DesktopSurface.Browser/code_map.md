# Pudding.DesktopSurface.Browser — 代码地图

> 项目定位与边界见根 [`code_map.md`](../../code_map.md) §2.2；施工规格见
> [`Docs/Features/Desktop-Surface-Browser-Mapping-2026-10-01.md`](../../Docs/Features/Desktop-Surface-Browser-Mapping-2026-10-01.md)。

## 1. 这个项目是什么

把 Desktop 侧能力通道需要的**桌面表面**映射到**既有浏览器抽象**（`PuddingBrowser.Abstractions`），
并负责「页面生命周期 → 目标注册表」的翻译。**不含任何 UI 代码**：不引用 WinUI / WebView2 /
浏览器实现项目，因此整条映射链路都能在无 UI 环境下测试。

编译期边界：`EnforceDesktopSurfaceBrowserBoundary` 禁止引用
`PuddingHost` / `PuddingRuntime` / `PuddingDesktop` / `PuddingBrowser.WebView2` /
`PuddingBrowser.AgentTools` / `PuddingAgent`，以及 `Microsoft.AspNetCore` / `Microsoft.UI` / `WebView2` 包。

参考：[映射规格](../../Docs/Features/Desktop-Surface-Browser-Mapping-2026-10-01.md)（九个操作的映射表与语义）。

## 2. 关键文件

| 文件 | 用途 / 不变量 |
|------|---------------|
| `BrowserRuntimeDesktopSurface.cs` | 九项浏览器能力 → `IBrowserRuntime` 调用。语义：页面版本来自身为一等成员的 `IBrowserPage.PageVersion`；缺失即 `Unknown`（**不伪造**）；超预算结果**如实标注** `Truncated`；`locate` 遇版本不符**跳过**陈旧引用查询；元素无活版本即 `internal_error`（不静默丢弃）；`interact` 的 `focus` 无运行时 API ⇒ `unsupported_capability`；`wait_for` 超时是结果不是失败；`page_state` 只报 `IsLoading` 能支持的 `Loading`/`Unknown`（**从不**声称 Interactive/Complete） |
| `DesktopSurfaceComposition.cs` | 把协作方组装成 `DesktopService` 需要的**单一** `IDesktopUiSurface`：浏览器 9 项 → `BrowserRuntimeDesktopSurface`，Shell 5 项 → `DesktopShellSurface`。组装层无逻辑，唯一可能的缺陷是**接错协作者**（编译期查不出、表现是"能调用但做错事"），故由测试逐个钉住 |
| `BrowserWorkspaceTargetBridge.cs` | **页面生命周期 → 两个目标注册表**的唯一翻译点。`DesktopTargetRegistry` 管页面版本/就绪度（准入与「引用随版本失效」的依据），`BrowserTargetRegistry` 管可信级别 / Agent 目标 / 活动页。不撒谎语义：未登记上下文 `Untrusted`（fail closed）；先页面后上下文**直接抛**（不静默补登记）；版本只前进；版本事件对未知页面**不凭空造目标**；关闭/清空不留悬空引用 |

## 3. 与其它组件的边界

- 上游（被谁用）：`PuddingDesktop` 组合根把它组装进 `DesktopService`（`IDesktopUiSurface` 接缝）。
- 依赖方向：`Pudding.Contracts`（契约叶）→ `Pudding.DesktopService`（服务与注册表）→
  `PuddingBrowser.Abstractions`（既有浏览器抽象）；**不得**反向引用调用方。
- 版本回写不在本层：能力结果里的页面版本由 `DesktopService` 在唯一出口回写注册表
  （见 `Source/Pudding.DesktopService/code_map.md`），本层只负责映射。

## 4. 测试

`Source/Pudding.DesktopSurface.BrowserTests/`（同目录同级）：

- `ContextsMappingTests.cs` / `InteractMappingTests.cs` / `SnapshotMappingTests.cs` /
  `WaitForMappingTests.cs` / `WebViewMappingTests.cs` — 各能力的映射语义与 fail-closed 分支；
- `DesktopSurfaceCompositionTests.cs` — 用「Shell 侧哨兵」把"接错协作者"变成可判定；
- `BrowserWorkspaceTargetBridgeTests.cs` — 注册表驱动的不变式（撤销 Agent 目标、版本只前进、
  不凭空造目标、关闭不留悬空引用）。
