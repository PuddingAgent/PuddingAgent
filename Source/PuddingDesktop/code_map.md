# PuddingDesktop

WinUI 3 外壳与独立 Core 启动器：负责桌面窗口、Core 进程监督、托盘与 Guardian 交接，自己是产品组合根但不装配 Host。

> 源指纹: Source/PuddingDesktop/**=pending · 条目数: 18 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `app.manifest` | 声明 PerMonitorV2 DPI 感知、长路径支持与 Windows 10 兼容性 | — | `PuddingDesktop.csproj` | — |
| `App.xaml` | 定义外壳主题画刷与 QuietButton 样式，含三套主题字典 | `ShellBackground`、`CardBackground`、`QuietButton` | `MainWindow.xaml` | 新增颜色必须同时补 Light / Default / HighContrast 三套，否则高对比度下丢对比 |
| `App.xaml.cs` | 负责单实例激活与协调器启动，并驱动诊断日志心跳 | `App`、`OnLaunched`、`UiThread` | `DesktopApplicationCoordinator`、`DesktopRunJournal` | `UiThread.Queue` 只在 `OnLaunched` 内赋值，UI 投递必须经它而非直接拿 DispatcherQueue |
| `Assets/Pudding.ico` | 多尺寸品牌图标，同时作 EXE 图标与托盘图标源 | — | `DesktopIcon`、`PuddingDesktop.csproj` | 图标须含多尺寸；重新生成走 `TestScripts/update-pudding-desktop-icon.ps1` |
| `DesktopIcon.cs` | 提供图标路径并按系统小图标尺寸加载私有 HICON | `DesktopIcon`、`LoadTrayIcon` | `DesktopTrayIcon` | HICON 生命周期归调用方，用完必须 `DestroyIcon`，否则句柄泄漏 |
| `DesktopTrayIcon.cs` | 托盘外壳：气泡通知、打开与退出菜单及会话结束处理 | `DesktopTrayIcon`、`ShowBalloon` | `MainWindow`、`DesktopIcon` | 气泡弹不出来时必须返回 false 而不是抛异常，调用方据此刻意区分「没送达」与「能力不可用」 |
| `GuardianTestAssembly.cs` | 只给 Guardian 集成测试开放 internal 可见性 | `InternalsVisibleTo` | `../PuddingDesktop.Guardian/code_map.md` | 不得扩大可见性名单 |
| `Hosting/DesktopApplicationCoordinator.cs` | Core 进程监督与启动状态机的组合根 | `DesktopApplicationCoordinator`、`StartAsync`、`StartCoreAsync`、`StopCoreAsync` | `CoreProcessLogBuffer`、`DesktopRuntimeSnapshot` | 进程内不得装配 Host / Runtime / Platform（`EnforceShellBoundary` 构建期拦截）；重启不等于部署 |
| `Hosting/DesktopApplicationCoordinator.Capability.cs` | 把工作区目标快照接入浏览器能力通道与页面生命周期 | `DesktopApplicationCoordinator` | `BrowserWorkspaceTargetBridge`、`IBrowserWorkspaceLifecycle` | 空或重复的 UI 状态不构成用户撤销指令，不得据此注销目标 |
| `Hosting/DesktopApplicationCoordinator.Guardian.cs` | Guardian 交接：生成无令牌设置、校验安装身份并转发控制命令 | `GuardianManaged`、`RequestGuardianExitAsync` | `GuardianAdoptionPlan`、`GuardianLaunchPlan` | 初始 Desktop 必须先交接所有权再启动 Core；受管子进程跳过交接 |
| `Hosting/GuardianAdoptionPlan.cs` | 纯函数：判定已在监听的 Guardian 是否可接管本进程 | `GuardianAdoptionPlan.Decide`、`GuardianAdoption` | `DesktopApplicationCoordinator.Guardian.cs` | 身份相同优先于存活探测；`AdoptsExitedDesktop` 必须报错，不能当健康接受 |
| `Hosting/GuardianLaunchPlan.cs` | 纯函数：判定新拉起 Guardian 的每次探测结果 | `GuardianLaunchPlan.Decide`、`GuardianLaunchDecision` | `GuardianAdoptionPlan` | 子进程 exit 0 表示另一进程持有安装租约，不得折叠成探测超时 |
| `MainWindow.xaml` | 外壳布局：Web 工作台与 Agent 浏览器、运行时中心及启动状态面板 | `MainWindow` | `MainWindow.xaml.cs`、`App.xaml` | 启动状态面板只切预声明元素的 Visibility，不要在代码里改颜色，否则主题切换不会重解析 |
| `MainWindow.xaml.cs` | 外壳交互：面板画布与工具卡片、运行时中心及启动流程编排 | `MainWindow` | `PanelImageBridge`、`ToolTabItem` | 工具卡片的标签与可点击性只能取自 `PuddingDesktop.Foundation.ToolAvailabilityCatalog`，不得就地判断 |
| `PanelImageBridge.cs` | 为画布 WebView2 提供 `pudding-image://` 本地图片通道 | `PanelImageBridge.Attach` | `PanelImageSource` | 空白名单等于全部拒绝，不得退化成不限；拒绝时只回状态码，不回显路径 |
| `PuddingDesktop.csproj` | 外壳工程定义：构建期边界检查与 Core、observer 的串行打包 | — | `../PuddingAgent/PuddingAgent.csproj`、`../PuddingDesktop.ExitObserver` | `DisableFastUpToDateCheck` 必须保留，否则只改 Core 不会进当前构建；Core 与 observer 由 Target 串行 Publish 到 `core/` 与 `observer/` |
| `SplitterHandle.cs` | 聊天区与工具区之间的拖拽分隔条，兼键盘焦点目标 | `SplitterHandle` | `MainWindow.xaml` | 必须继承 `Grid` 而不是 Border，因为调整光标挂在 `UIElement.ProtectedCursor` 上 |
| `ToolTabItem.cs` | 把 `ToolTab` 投影成标签条可绑定的视图模型 | `ToolTabItem`、`Refresh` | `ToolTab`、`ToolWorkspaceTabs` | 注册表保持无 UI 依赖；每次注册表变更后必须调 `Refresh`，否则标签不刷新 |
