# PuddingDesktop.CapabilityHost CodeMAP

> **WinUI 侧平台适配**：只承载「必须依赖 Windows App SDK」的包装。
> 边界（编译期 Target `EnforceCapabilityHostBoundary`）：只允许引用 `Pudding.Contracts`；
> 所有业务/准入/竞态逻辑在 `Pudding.DesktopService`（可用假调度器独立测试）。
> 目标框架：`net10.0-windows10.0.19041.0` + `Microsoft.WindowsAppSDK 1.8.260921001`（与 WinUI Shell 同版本）。

| 文件 | 用途 |
|---|---|
| `WinUiDesktopUiDispatcher.cs` | `IDesktopUiDispatcher` 的 `DispatcherQueue` 实现：已在 UI 线程则同步执行；跨线程经 `TryEnqueue` 排队并把取消/异常传播到返回的 Task；**入队失败（窗口销毁）以异常结束，绝不悬挂** |

## 为什么没有独立测试工程

本类需要真实 `DispatcherQueue`（WinUI 消息循环）才能构造，无独立可测逻辑。它的契约由
`Pudding.DesktopServiceTests` 用假调度器验证（拒绝入队 ⇒ `ui_unavailable`、窗口退出 ⇒ 任务完成而不悬挂、
已取消调用 ⇒ 不触碰 UI 表面），本工程只保证**编译期**符合 `IDesktopUiDispatcher` 接缝。

## 后续（切片 C-3 / D）

- 在 `PuddingDesktop` 组合根构造本调度器与 `IDesktopUiSurface`（WebView2 动作表面），
  经 `DesktopCapabilityHost.CreateGrpcSupervisorFactory` 装配宿主；
- 线程访问与真实队列拒绝行为需在 WinUI 应用内定向验证（外部控制器重启到新构建后执行）。
