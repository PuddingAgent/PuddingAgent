# PuddingDesktop.CapabilityHost CodeMAP

> **WinUI 侧平台适配**：只承载「必须依赖 Windows App SDK」的包装。
> 边界（编译期 Target `EnforceCapabilityHostBoundary`）：只允许引用 `Pudding.Contracts`；
> 所有业务/准入/竞态逻辑在 `Pudding.DesktopService`（可用假调度器独立测试）。
> 目标框架：`net10.0-windows10.0.19041.0` + `Microsoft.WindowsAppSDK 1.8.260921001`（与 WinUI Shell 同版本）。

| 文件 | 用途 |
|---|---|
| `WinUiDesktopUiDispatcher.cs` | `IDesktopUiDispatcher` 的 `DispatcherQueue` 实现：已在 UI 线程则同步执行；跨线程经 `TryEnqueue` 排队并把取消/异常传播到返回的 Task；**入队失败（窗口销毁）以异常结束，绝不悬挂** |
| `WinUiShellFacilities.cs` | `IDesktopShellFacilities`（对话框 / 文件选择器 / 剪贴板）的 WinUI 实现。必守要点：`ContentDialog` **必须设 `XamlRoot`**；`FileOpenPicker` 必须 `InitializeWithWindow`（否则 `COMException`）；剪贴板非文本 ⇒ `HasText=false` 的**成功**结果；取消（ESC/遮罩/选择器返回 null）是**结果**不是失败 |
| `WinUiShellHostFacilities.cs` | `IDesktopShellHostFacilities`（窗口形态 / 托盘可见性 / 系统通知）的实现：**委托注入**（窗口与托盘归 Shell 所有，通知走托盘气泡需要托盘图标的 HWND 与 uID）；通知**没弹出来也是成功结果**（`Shown=false`）；只读状态只报「只有它知道」的部分，自动化状态与页面数由 `DesktopService` 覆盖 |

## 为什么没有独立测试工程

这些类需要真实 `DispatcherQueue`（WinUI 消息循环）或真实窗口/托盘才能构造，无独立可测逻辑。
它们的契约由 `Pudding.DesktopServiceTests` 用假调度器与假设施验证（拒绝入队 ⇒ `ui_unavailable`、
窗口退出 ⇒ 任务完成而不悬挂、已取消调用 ⇒ 不触碰 UI 表面、通知未弹出 ⇒ 成功结果、异常 ⇒ `internal_error`
且不透出消息），本工程只保证**编译期**符合契约并 0 警告。

## 后续（切片 C-3 / D）

- 在 `PuddingDesktop` 组合根构造调度器、两个设施实现与 `IDesktopUiSurface`，
  经 `DesktopCapabilityChannelComposition.StartAsync`（`Source/Pudding.DesktopService`）装配并启动；
  退出路径 `DisposeAsync`。Shell 内仍待接线：页面生命周期驱动目标注册表、组合根调用点。
- 线程访问与真实队列拒绝行为需在 WinUI 应用内定向验证（外部控制器重启到新构建后执行）。
