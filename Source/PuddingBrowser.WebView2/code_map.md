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
| `WebView2DomClient.cs` | DOM 客户端（核心，22KB）。ref 线格式 **`p{页面token}-v{文档世代}-n{序号}`**：token 由 `WebView2BrowserPage` 生成（页面写不出来），因此**跨页/跨 frame 的引用即使版本号相同也被拒**（`stale_element_reference` + 只说「重新观察」的提示）；序号是**每页独立**生成的，只比版本号会让 A 页的 `v7-n3` 命中 B 页元素 |
| `WebView2ElementHandle.cs` | 元素句柄 |

## WPF 集成

| 文件 | 用途 |
|------|------|
| `WpfBrowserSurfaceHost.cs` | WPF 表面宿主 |
| `IBrowserSurfaceHost.cs` | 表面宿主接口 |
| `IWebView2UiDispatcher.cs` | UI 线程调度接口 |

## 测试

—（由 Desktop.Tests 间接覆盖）
