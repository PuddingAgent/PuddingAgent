# PuddingBrowser.WebView2 CodeMAP
> 源指纹: *.cs=11-d76a40df0f5f, *.csproj=1-d784f732327e · 条目数: 9 · 最近整理: 2026-10-22

> WebView2 Driver | 真实浏览器页面操作

## 运行时

| 文件 | 用途 |
|------|------|
| `WebView2BrowserRuntime.cs` | 浏览器运行时实现 |
| `WebView2BrowserContext.cs` | 浏览器上下文管理 |
| `WebView2BrowserPage.cs` | 页面实现（核心，16KB）。CDP 传输/截图已实现，PDF/事件订阅 unsupported。（见下「关键语义」） |
| `WebView2BrowserSurface.cs` | 浏览器表面与截图有效可见性；共享页面驱动在 UI 线程检查后采集，CDP 等待支持取消 |

## DOM 操作

| 文件 | 用途 |
|------|------|
| `WebView2DomClient.cs` | DOM 客户端（核心，22KB）。ref 线格式与定位/交互/快照/滚动脚本语义。（见下「关键语义」） |
| `WebView2ElementHandle.cs` | 元素句柄 |

## WPF 集成

| 文件 | 用途 |
|------|------|
| `WpfBrowserSurfaceHost.cs` | WPF 表面宿主 |
| `IBrowserSurfaceHost.cs` | 表面宿主接口 |
| `IWebView2UiDispatcher.cs` | UI 线程调度接口 |

## 测试

- [WebView2.Smoke](../../Tests/PuddingBrowser.WebView2.Smoke/Program.cs)：真实 WebView2 空白页版本、首次导航、表单交互与导航后旧 ref 失效；使用独立 TestSite/DataRoot。
- `Tests/PuddingDesktop.Tests/Browser/`：驱动与 Desktop 组合边界测试。

## 关键语义

> 上表「用途」列的长单元格已缩为语义摘要并附 `（见下「关键语义」）` 指针；以下为该单元格原始全文，仅插入换行、逐字保留。

**`WebView2BrowserPage.cs`**：
真实 surface 初始化完成时已存在 `about:blank` 文档，驱动从版本 1 开始，`Info.PageVersion` 同步初始化；无 surface 仍为 0。首次导航继续递增，传输适配器不得补造有效版本。
页面实现。**CDP 传输**：`SendCdpAsync` 走 WebView2 自带的 `CallDevToolsProtocolMethodAsync`（不必另开调试端口）；`SerializeCdpParameters` 将 nullable null、JSON null、Undefined 转成 `{}`，其余参数透传，返回 JSON 由调用方解释；**截图**：`ScreenshotAsync` 走 `Page.captureScreenshot`（
整页用 `Page.getLayoutMetrics` 的内容尺寸做 clip，参数语义在 `ScreenshotPlan` 里单独测试；**预算内降档重拍**：JPEG 超 `MaxBytes` 时按 `ScreenshotPlan.QualitySteps` 依次降质重拍（第一个装进预算的胜出；PNG 无 quality 可降，直接如实报预算），胜出档位经 `ScreenshotResult.Quality` 回给调用方）；
`PrintToPdfAsync` 与 `SubscribeCdpAsync`（事件订阅）**仍明确 unsupported**——它需要另外的事件泵，不假装支持。原生输入的动作序列由 `PuddingBrowser.Automation.NativeInputSequence` 规划，这里只负责按序发 CDP。**非 ASCII 输入走真实剪贴板 + Ctrl+V**（`BrowserClipboard` 按 `WINUI_BROWSER` 分派到 WinUI/WPF 剪贴板 API，经 `IWebView2UiDispatcher` 落到 UI 线程；判据在 `Abstractions.BrowserTextInput.RequiresClipboard`）；宿主痕迹由 `WebView2FingerprintScript` 在文档创建时清理（只抹 `chrome.webview`，UA/webdriver/插件实测本就与真实 Edge 一致）；指针轨迹点之间由 `PointerMoveSettleMilliseconds`（12ms）打出时间间隔——分步但零时长比瞬移更可疑：2026-10-26 实测「只带 text 的 keyDown」会让 Draft.js/React 的 EditorState 为空（DOM 有字、发送按钮不出现）。**引用身份事实**：ref 以 `data-pudding-ref` 属性绑在**节点自身**上（节点被换 ⇒ 查不到；节点脱离文档 ⇒ 仍查得到），因此解析时必须判 `isConnected`，
脱离文档一律按 `stale_element_reference` 拒绝（否则就是静默操作一个不在文档里的元素）。**原生滚动**（§7）：改 `scrollTop` 换成在正确落点发真实滚轮事件（CDP `Input.dispatchMouseEvent(mouseWheel)`），落点=元素中心或视口中心（视口尺寸取自 CDP `Page.getLayoutMetrics`，不猜窗口大小）；滚轮本身不带结果，**事实由之后的一次只读 DOM 观察取回**（输入原生、观察 DOM，这个分工如实反映在返回的 ScrollOutcome 里）。
**页面脚本对话框已接管**：`ScriptDialogOpening` 按 `ScriptDialogPolicy` 确定性作答（本版本 API 没有 `Handled` 属性，语义是「调用 Accept 即接受、不调用即取消」），页面不会被模态框卡住，摘要留在 `LastScriptDialog`/`ScriptDialogCount`。**权限请求/新窗口/下载也已接管**：分别一律拒绝、抑制弹窗、取消下载（`PageEventPolicy`），摘要进 `PageEventLog`（有界、连续重复合并）
并随 `PageSnapshot.PageEvents` 回给调用方——页面不会因为「等人点权限/等人选保存位置」而卡住，上层也能知道刚才发生了什么。**WinUI 面**（`PuddingBrowser.WinUI` 链接同一驱动，只引用 Abstractions）：输入/截图计划通过 `IBrowserDriverPolicy` 注入；
`BrowserWorkspaceController` 为其创建的 context 注入 `AutomationDriverPolicy`。缺策略时才由 `NullBrowserDriverPolicy` 明确拒绝；不能再笼统写成 WinUI 全部不支持。默认 Bridge 的截图执行器依赖与输入模式透传限制见[现有架构](../../Docs/07_architecture/浏览器工具与浏览器控制能力-架构与施工进度-2026-10-18.md)。

**`WebView2DomClient.cs`**：
DOM 客户端（核心，22KB）。ref 线格式 **`p{页面token}-v{文档世代}-n{序号}`**：token 由 `WebView2BrowserPage` 生成（页面写不出来），因此**跨页/跨 frame 的引用即使版本号相同也被拒**（`stale_element_reference` + 只说「重新观察」的提示）；序号是**每页独立**生成的，只比版本号会让 A 页的 `v7-n3` 命中 B 页元素。定位/交互脚本还回读**表单当前值**（`value`/`selectedValues`）
——这是「fill 是否写入成功」的唯一可靠回执（设计方案 §4.2/§6.2）。两个脚本默认**排除隐藏元素**（`!locator.includeHidden` ⇒ 过滤 `visible`；屏外但有尺寸的仍保留），并按 `inViewportOnly` 正交过滤；两个脚本都报告 **`visible` 与 `inViewport` 两个独立事实**。交互脚本另有歧义拒绝（`matches.length>1`）、不可见/禁用拒绝，且**写操作先 `scrollIntoView` 再复检视口**，
仍不满足就返回 `browser_element_not_in_viewport`（不静默点一个看不见的节点）；写操作前还做**遮挡命中测试**（`root.elementFromPoint`，用元素所在根以正确穿越 shadow DOM），被别的节点挡住即 `browser_element_obstructed`——**遮挡目标不按成功处理**（§6.2）。**text 定位取最小匹配节点**（剔除包含其它匹配的祖先，否则"祖先也含文本"会返回整页），两份脚本共用同一套语义；
零命中再分清"真的没有"（`browser_element_not_found`）与**"命中了但全被隐藏/视口筛掉"**（`browser_locator_matches_filtered`）。**快照脚本支持作用域**（`scopeRef`）：只从该 ref 的节点（含自己）开始遍历；进入脚本前先用与普通引用**同一条** `ValidateLocator` 规则校验它属于这一页这一版，找不到即 `stale_element_reference`——**绝不退化成正页扫描**。
**滚动走独立脚本** `ScrollScript`：实现 `page/element/to_ref/to_top/to_bottom`（线名取自 Abstractions 的 `ScrollTargetWire`，**驱动与 Core 共用**，不再各写一份 switch），`element` 先看元素自身滚动盒、必要时上溯**最近可滚动祖先**（不盲目滚另一个区域），并返回**结果事实**（容器/前后位置/`consumedDelta`/`atBoundary`/`contentChanged`）；未登记目标 fail closed；
`DescribeScroll` 要求元素类目标必须是 **ref** 定位。**内容没变不是失败**
