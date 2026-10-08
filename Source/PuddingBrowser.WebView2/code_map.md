# PuddingBrowser.WebView2 CodeMAP

> WebView2 Driver | 真实浏览器页面操作

## 运行时

| 文件 | 用途 |
|------|------|
| `WebView2BrowserRuntime.cs` | 浏览器运行时实现 |
| `WebView2BrowserContext.cs` | 浏览器上下文管理 |
| `WebView2BrowserPage.cs` | 页面实现（核心，16KB）。**CDP 传输已实现**：`SendCdpAsync` 走 WebView2 自带的 `CallDevToolsProtocolMethodAsync`（不必另开调试端口），参数原样透传、返回 JSON 由调用方解释；`SubscribeCdpAsync`（事件订阅）**仍明确 unsupported**——它需要另外的事件泵，不假装支持。原生输入的动作序列由 `PuddingBrowser.Automation.NativeInputSequence` 规划，这里只负责按序发 CDP。**引用身份事实**：ref 以 `data-pudding-ref` 属性绑在**节点自身**上（节点被换 ⇒ 查不到；节点脱离文档 ⇒ 仍查得到），因此解析时必须判 `isConnected`，脱离文档一律按 `stale_element_reference` 拒绝（否则就是静默操作一个不在文档里的元素）。**原生滚动**（§7）：改 `scrollTop` 换成在正确落点发真实滚轮事件（CDP `Input.dispatchMouseEvent(mouseWheel)`），落点=元素中心或视口中心（视口尺寸取自 CDP `Page.getLayoutMetrics`，不猜窗口大小）；滚轮本身不带结果，**事实由之后的一次只读 DOM 观察取回**（输入原生、观察 DOM，这个分工如实反映在返回的 ScrollOutcome 里） |
| `WebView2BrowserSurface.cs` | 浏览器表面 |

## DOM 操作

| 文件 | 用途 |
|------|------|
| `WebView2DomClient.cs` | DOM 客户端（核心，22KB）。ref 线格式 **`p{页面token}-v{文档世代}-n{序号}`**：token 由 `WebView2BrowserPage` 生成（页面写不出来），因此**跨页/跨 frame 的引用即使版本号相同也被拒**（`stale_element_reference` + 只说「重新观察」的提示）；序号是**每页独立**生成的，只比版本号会让 A 页的 `v7-n3` 命中 B 页元素。定位/交互脚本还回读**表单当前值**（`value`/`selectedValues`）——这是「fill 是否写入成功」的唯一可靠回执（设计方案 §4.2/§6.2）。两个脚本默认**排除隐藏元素**（`!locator.includeHidden` ⇒ 过滤 `visible`；屏外但有尺寸的仍保留），并按 `inViewportOnly` 正交过滤；两个脚本都报告 **`visible` 与 `inViewport` 两个独立事实**。交互脚本另有歧义拒绝（`matches.length>1`）、不可见/禁用拒绝，且**写操作先 `scrollIntoView` 再复检视口**，仍不满足就返回 `browser_element_not_in_viewport`（不静默点一个看不见的节点）；写操作前还做**遮挡命中测试**（`root.elementFromPoint`，用元素所在根以正确穿越 shadow DOM），被别的节点挡住即 `browser_element_obstructed`——**遮挡目标不按成功处理**（§6.2）。**text 定位取最小匹配节点**（剔除包含其它匹配的祖先，否则"祖先也含文本"会返回整页），两份脚本共用同一套语义；零命中再分清"真的没有"（`browser_element_not_found`）与**"命中了但全被隐藏/视口筛掉"**（`browser_locator_matches_filtered`）。**快照脚本支持作用域**（`scopeRef`）：只从该 ref 的节点（含自己）开始遍历；进入脚本前先用与普通引用**同一条** `ValidateLocator` 规则校验它属于这一页这一版，找不到即 `stale_element_reference`——**绝不退化成正页扫描**。**滚动走独立脚本** `ScrollScript`：实现 `page/element/to_ref/to_top/to_bottom`（线名取自 Abstractions 的 `ScrollTargetWire`，**驱动与 Core 共用**，不再各写一份 switch），`element` 先看元素自身滚动盒、必要时上溯**最近可滚动祖先**（不盲目滚另一个区域），并返回**结果事实**（容器/前后位置/`consumedDelta`/`atBoundary`/`contentChanged`）；未登记目标 fail closed；`DescribeScroll` 要求元素类目标必须是 **ref** 定位。**内容没变不是失败** |
| `WebView2ElementHandle.cs` | 元素句柄 |

## WPF 集成

| 文件 | 用途 |
|------|------|
| `WpfBrowserSurfaceHost.cs` | WPF 表面宿主 |
| `IBrowserSurfaceHost.cs` | 表面宿主接口 |
| `IWebView2UiDispatcher.cs` | UI 线程调度接口 |

## 测试

—（由 Desktop.Tests 间接覆盖）
