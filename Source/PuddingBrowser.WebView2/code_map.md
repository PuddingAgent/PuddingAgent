# PuddingBrowser.WebView2 CodeMAP

> WebView2 Driver | 真实浏览器页面操作

## 运行时

| 文件 | 用途 |
|------|------|
| `WebView2BrowserRuntime.cs` | 浏览器运行时实现 |
| `WebView2BrowserContext.cs` | 浏览器上下文管理 |
| `WebView2BrowserPage.cs` | 页面实现（核心，16KB） |
| `WebView2BrowserSurface.cs` | 浏览器表面 |

## DOM 操作

| 文件 | 用途 |
|------|------|
| `WebView2DomClient.cs` | DOM 客户端（核心，22KB）。ref 线格式 **`p{页面token}-v{文档世代}-n{序号}`**：token 由 `WebView2BrowserPage` 生成（页面写不出来），因此**跨页/跨 frame 的引用即使版本号相同也被拒**（`stale_element_reference` + 只说「重新观察」的提示）；序号是**每页独立**生成的，只比版本号会让 A 页的 `v7-n3` 命中 B 页元素。定位/交互脚本还回读**表单当前值**（`value`/`selectedValues`）——这是「fill 是否写入成功」的唯一可靠回执（设计方案 §4.2/§6.2）。两个脚本默认**排除隐藏元素**（`!locator.includeHidden` ⇒ 过滤 `visible`；屏外但有尺寸的仍保留），并按 `inViewportOnly` 正交过滤；两个脚本都报告 **`visible` 与 `inViewport` 两个独立事实**。交互脚本另有歧义拒绝（`matches.length>1`）、不可见/禁用拒绝，且**写操作先 `scrollIntoView` 再复检视口**，仍不满足就返回 `browser_element_not_in_viewport`（不静默点一个看不见的节点）。**text 定位取最小匹配节点**（剔除包含其它匹配的祖先，否则"祖先也含文本"会返回整页），两份脚本共用同一套语义；零命中再分清"真的没有"（`browser_element_not_found`）与**"命中了但全被隐藏/视口筛掉"**（`browser_locator_matches_filtered`） |
| `WebView2ElementHandle.cs` | 元素句柄 |

## WPF 集成

| 文件 | 用途 |
|------|------|
| `WpfBrowserSurfaceHost.cs` | WPF 表面宿主 |
| `IBrowserSurfaceHost.cs` | 表面宿主接口 |
| `IWebView2UiDispatcher.cs` | UI 线程调度接口 |

## 测试

—（由 Desktop.Tests 间接覆盖）
