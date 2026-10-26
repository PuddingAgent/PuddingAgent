# PuddingBrowser.Abstractions CodeMAP
> 源指纹: *.cs=11-c3a972e069b4, *.csproj=1-eb0b706aca65 · 条目数: 10 · 最近整理: 2026-10-26

> Browser 抽象契约 | 接口 · 类型 · 策略

## 核心接口

| 文件 | 用途 |
|------|------|
| `BrowserInterfaces.cs` | 核心接口：`IBrowserRuntime`、`IBrowserPage`；另有工作区生命周期与页面呈现端口 |

## 模型与类型

| 文件 | 用途 |
|------|------|
| `BrowserTypes.cs` | 浏览器类型定义（标签、Cookie、视口等） |
| `BrowserIds.cs` | 浏览器 ID 常量 |
| `BrowserOperationOrigin.cs` | 操作来源标记 |
| `CapabilityPolicy.cs` | 能力策略 |
| `Locator.cs` | 元素定位器模型 |
| `Models.cs` | 通用模型 |
| `ScriptAndSnapshot.cs` | 脚本与快照模型 |
| `DriverPolicy.cs` | `IBrowserDriverPolicy` 驱动策略**接缝**：计划/事件日志/停止原因由实现方决定（见下「关键语义」） |
| `PageVersionPolicy.cs` | pageVersion 递增判据：同文档导航（SPA pushState）必须让版本递增，否则旧 ref 被判仍然有效 |

## 关键语义

`IBrowserDriverPolicy` **接缝**：驱动把「计划/事件日志/停止原因」交给实现方决定（计划点击/悬停/滚动/填充/键入/按键/拖拽、到达步进、截图参数**与质量阶梯 `ScreenshotQualitySteps`**、文件上传三步、事件日志工厂）；`NullBrowserDriverPolicy` = **fail-closed**（一切返回 null/unsupported，阶梯返回空 ⇒ 只拍一次）

`PageVersionPolicy.BumpsOnSourceChange`：**真实导航**（`NavigationStarting` 之后、`NavigationCompleted` 之前）由 `NavigationCompleted` 递增，这里返回 false 以免重复计；**同文档导航**（URL 变了但没有导航在飞）必须递增 —— SPA 的 `pushState/replaceState` 换掉整页内容却不触发 `NavigationCompleted`，漏掉它等于让旧元素引用在新路由上继续有效

## 测试

—（由 AgentTools / Desktop.Tests 间接覆盖）

`PageVersionPolicy` 有直接用例：`PuddingBrowser.AgentToolsTests/PageVersionPolicyTests.cs`（6 例，含「不该递增」的反例）

**`IBrowserWorkspaceLifecycle`**：产品管理操作的工作区所有者端口，成功即包含页签/生命周期登记（不是"已排入队列"）。**`IBrowserPagePresenter`**：把这一页**真正呈现出来**的端口——截图/原生输入遇到表面不可见时先展开工作区再重试，不许静默返回空。
