# Pudding.DesktopSurface.Browser — 代码地图

> 项目定位与边界见根 [`code_map.md`](../../code_map.md) §2.2；施工规格见
> [`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md`](../../Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md)。

## 1. 这个项目是什么

把 Desktop 侧能力通道需要的**桌面表面**映射到**既有浏览器抽象**（`PuddingBrowser.Abstractions`），
并负责「页面生命周期 → 目标注册表」的翻译。**不含任何 UI 代码**：不引用 WinUI / WebView2 /
浏览器实现项目，因此整条映射链路都能在无 UI 环境下测试。

编译期边界：`EnforceDesktopSurfaceBrowserBoundary` 禁止引用
`PuddingHost` / `PuddingRuntime` / `PuddingDesktop` / `PuddingBrowser.WebView2` /
`PuddingBrowser.AgentTools` / `PuddingAgent`，以及 `Microsoft.AspNetCore` / `Microsoft.UI` / `WebView2` 包。

参考：[映射规格](../../Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md)（九个操作的映射表与语义）。

## 2. 关键文件

| 文件 | 用途 / 不变量 |
|------|---------------|
| `BrowserRuntimeDesktopSurface.cs` | 浏览器能力映射；多匹配交互返回脱敏歧义原因与 nth 提示（见下「关键语义」） |
| `DesktopSurfaceComposition.cs` | 把协作方组装成 `DesktopService` 需要的**单一** `IDesktopUiSurface`：浏览器 9 项 → `BrowserRuntimeDesktopSurface`，Shell 5 项 → `DesktopShellSurface`。组装层无逻辑，唯一可能的缺陷是**接错协作者**（编译期查不出、表现是"能调用但做错事"），故由测试逐个钉住 |
| `BrowserWorkspaceTargetBridge.cs` | 页面生命周期翻译；`ObserveAgentTarget` 同步 UI 快照，`OnAgentTargetChanged` 表达显式选页/撤权，空快照不作撤权 |
| （同上）切换目标即撤销 | （见下「关键语义」） | （见下「关键语义」） |

### 2.1 关键语义（长单元格原文）

下表两条目的「用途 / 不变量」原文逐字下沉如下。

**`BrowserRuntimeDesktopSurface.cs`**

产品组合必传 `IBrowserWorkspaceLifecycle`：上下文创建/关闭与页签新建/激活/关闭走工作区所有者，内容操作仍走 driver。工作区失败不回退重试原始 runtime。独立驱动映射测试可直接构造表面；Shell 友好的 `DesktopSurfaceComposition` 构造函数不接受缺失所有者。

截图统一读取 layout metrics 与真实 DPR，按 `ScreenshotPlan.BuildForObservation` 采集并核对 PNG/JPEG 字节的尺寸和格式；只为有效且仍在原页面版本的图片登记 observation。裁剪原点保存文档 CSS 坐标，点击/移动/拖拽两端的截图坐标转换为视口 CSS 后传给驱动。模型 detail 的传递归 AgentTools，不在表面决定供应商参数。

九项浏览器能力 → `IBrowserRuntime` 调用。语义：页面版本来自身为一等成员的 `IBrowserPage.PageVersion`；缺失即 `Unknown`（**不伪造**）；超预算结果**如实标注** `Truncated`；`locate` 遇版本不符**跳过**陈旧引用查询，并实现**游标分页**（校验版本+查询指纹 ⇒ 不符即 `CursorExpired`，格式坏即 `InvalidRequest`；还有下一页才发 `NextCursor`，
取完为 null）；元素无活版本即 `internal_error`（不静默丢弃）；`interact` 的 `focus` 无运行时 API ⇒ `unsupported_capability`；滚动映射**目标模型**（`DesktopScrollTarget` → `ScrollTarget`，显式优先、否则按 locator/位移推导）并把驱动报告的**滚动事实**放进 `DesktopInteractionResult.Scroll`（无事实即 `null`，
**不编造**）；`wait_for` 超时是结果不是失败；`page_state` 只报 `IsLoading` 能支持的 `Loading`/`Unknown`（**从不**声称 Interactive/Complete）

**（同上）切换目标即撤销**

`OnAgentTargetChanged` 在目标**真的改变**后调用 `BrowserTaskBindingService.RevokeForTarget(旧目标)`；以注册表实际状态为准（切换失败不误撤）。未提供绑定服务时不撤销 —— 因此**闸门也不应被打开**

**页面生命周期 → 生命周期端口**的唯一翻译点。版本推进（`OnPageActivated`/`OnPageVersionAdvanced`）仍直接写页面注册表；**创建、目标切换与关闭全部交给 `BrowserPageLifecycle`**，因此"登记原子性/关闭清理（授权+租约）"只有一个所有者。不撒谎语义：未登记上下文 `Untrusted`（fail closed）；先页面后上下文**直接抛**（不静默补登记）；版本只前进；
版本事件对未知页面**不凭空造目标**；关闭/清空不留悬空引用

## 3. 与其它组件的边界

- 上游（被谁用）：`PuddingDesktop` 组合根把它组装进 `DesktopService`（`IDesktopUiSurface` 接缝）。
- 依赖方向：`Pudding.Contracts`（契约叶）→ `Pudding.DesktopService`（服务与注册表）→
  `PuddingBrowser.Abstractions`（既有浏览器抽象）；**不得**反向引用调用方。
- 版本回写不在本层：能力结果里的页面版本由 `DesktopService` 在唯一出口回写注册表
  （见 `Source/Pudding.DesktopService/code_map.md`），本层只负责映射。

## 4. 测试

`Source/Pudding.DesktopSurface.BrowserTests/`（同目录同级）：

- `BrowserTargetObservationTests.cs` — 初始/重复/关页后快照不撤权，显式撤权仍阻断；使用真实生命周期与会话权威。
- `ContextsMappingTests.cs` / `InteractMappingTests.cs` / `SnapshotMappingTests.cs` /
  `WaitForMappingTests.cs` / `WebViewMappingTests.cs` — 各能力的映射语义与 fail-closed 分支；
- `DesktopSurfaceCompositionTests.cs` — 用「Shell 侧哨兵」把"接错协作者"变成可判定；
- `BrowserWorkspaceTargetBridgeTests.cs` — 注册表驱动的不变式（撤销 Agent 目标、版本只前进、
  不凭空造目标、关闭不留悬空引用）。
