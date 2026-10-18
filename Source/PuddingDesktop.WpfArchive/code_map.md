# PuddingDesktop CodeMAP

> Windows WPF 产品入口 | 单实例 · Core 子进程管理 · Browser 工作区 · 运行中心

## 入口

| 文件 | 用途 |
|------|------|
| `App.xaml` | Windows 11 Light/Dark 样式 Token；原生浅色 Popup 文本对比度资源 |
| `App.xaml.cs` | WPF 产品入口、单实例所有权、异常日志 |
| `MainWindow.xaml(.cs)` | 48px 标题栏、Navigation、Workbench/Browser/Runtime/Storage 页 |

## 进程管理

| 文件 | 用途 |
|------|------|
| `Hosting/DesktopApplicationCoordinator.cs` | 🔑 Launcher↔Core 状态机（协调 Runtime/Bridge/Workbench 等）见下「关键语义」 |
| `Core/CoreExecutableResolver.cs` | Core 路径确定性解析（配置→发布包→同源→兜底） |
| `Core/CoreProcessSupervisor.cs` | Core 子进程启动/健康检查/日志/进程树回收；超时用进度租约见下「关键语义」 |
| `Core/CoreStartupProgressMessage*.cs` | `PUDDING_DESKTOP_STARTING` 协议、严格解析与单调租约见下「关键语义」 |
| `Runtime/DesktopRuntimeOrchestrator.cs` | 异常退出恢复、退避熔断（2s/4s/8s，60s 3 次） |
| `Runtime/CoreRestartPolicy.cs` | 重启策略与取消语义 |
| `Bootstrap/DesktopBootstrapSignalService.cs` | 🔑 Core 点火部署主管（desktop-build 等三模式事务部署）见下「关键语义」 |
| `Bootstrap/DesktopBootstrapHttpEndpoint.cs` | 仅回环 + ControlToken 的 Desktop 控制面（Core/前端部署与诊断）见下「关键语义」 |
| `Bootstrap/DesktopBootstrapSignal*.cs` | 信号协议、部署模式归一化与结果证据模型（产物/加载路径、复制计数、哈希、`assembliesReloaded`） |

**关键语义**

**`Hosting/DesktopApplicationCoordinator.cs`**：🔑 Launcher↔Core 状态机，协调 Runtime、Bridge、Workbench、调试组件；独立前端锁支持源码构建部署与仓库内预构建 `dist` 加载，并投影受控诊断快照

**`Core/CoreProcessSupervisor.cs`**：Core 子进程固定端口 `0.0.0.0` 启动、本机健康检查、环形 stdout/stderr、进程树回收；启动超时采用有效进度租约（静默超时 + 有界绝对上限）；`EnvironmentName` 可覆盖 Production

**`Core/CoreStartupProgressMessage*.cs`**：`PUDDING_DESKTOP_STARTING` 协议、严格解析与单调租约；校验协议版本/PID/序号，默认 60 秒静默门禁下允许最高 10 分钟冷升级，租约不替代 Ready/health 门禁

**`Bootstrap/DesktopBootstrapSignalService.cs`**：🔑 Core 点火部署主管：`desktop-build` / `prebuilt-artifact` / `restart-only`，停 Core 后把产物事务部署到实际 `CoreExecutablePath` 目录，重启前后校验 `PuddingAgent.dll` SHA-256

**`Bootstrap/DesktopBootstrapHttpEndpoint.cs`**：仅回环 + ControlToken 的 Desktop 控制面：Core stop/build/start/restart/预构建制品部署，前端 build-deploy/预构建 dist 加载，以及有界诊断；返回稳定 JSON/HTTP 状态


## 调试模式（Debug）

| 文件 | 用途 |
|------|------|
| `Debug/DesktopReverseProxy.cs` | HttpListener 本机反向代理（后端→Core、其余→前端）见下「关键语义」 |
| `Debug/ProxyRoutePlanner.cs` | 纯路由决策：8 个后端前缀 + /admin SPA fallback（与 dev-up.py 语义逐条对齐） |
| `Debug/DebugBackendLauncher.cs` | `dotnet build` 源码后端并解析可执行产物见下「关键语义」 |
| `Debug/FrontendDevSupervisor.cs` | `pnpm run start:dev`（缺 node_modules 自动 install）、/admin/ 就绪探测、进程树回收、环形日志 |
| `Debug/FrontendBuildDeployService.cs` | 运行中心/API 前端制品服务（build 或加载预构建 dist）见下「关键语义」 |
| `Debug/DebugRepositoryResolver.cs` | 仓库根向上自动解析 + 显式覆盖（repositoryRoot/frontendWorkingDirectory/backendProjectPath） |
| `Configuration/DesktopBootstrapSettings.cs` | `DesktopDebugSettings`（desktop.json `debug` 节：Enabled/端口/超时） |
| `Hosting/DesktopStateChangedEventArgs.cs` | `WorkbenchAddress`（调试=代理源）与 `CoreAddress`（控制面）分离 |

**关键语义**

**`Debug/DesktopReverseProxy.cs`**：HttpListener 本机反向代理（默认 127.0.0.1:80）：后端前缀→Core、其余→前端 dev server；SSE 逐块 Flush、WebSocket 全双工中继（HMR）、上游 502

**`Debug/DebugBackendLauncher.cs`**：`dotnet build` 源码后端并解析 `bin/Debug/net10.0/PuddingAgent.exe`（desktop-child 协议不变，Development 环境）；`ResolveOutputDirectory` 无需先构建即可给出输出目录

**`Debug/FrontendBuildDeployService.cs`**：运行中心/API 前端制品服务：可 `pnpm run build` 或直接加载预构建 dist，部署前后校验 `index.html` SHA-256；只替换 Core 可执行目录 `wwwroot\admin`，目标后缀强校验


## Browser 工作区

| 文件 | 用途 |
|------|------|
| `Browser/DesktopBrowserBridgeClient.cs` | 认证 WebSocket Client，HelloAck、heartbeat、重连 |
| `Browser/BrowserBridgeCommandDispatcher.cs` | 命令分发，安全摘要投影到 UI Activity；实现 `IBrowserAutomationGate` 见下「关键语义」 |
| `Browser/BrowserWorkspaceController.cs` | Tab/Activity/active/target 唯一 UI 状态源；接管/暂停唯一写入点见下「关键语义」 |
| `Browser/BrowserAutomationGateSync.cs` | `IBrowserAutomationGate` 端口/闸门集合/`DelegateAutomationGate` 见下「关键语义」 |
| `Views/BrowserWorkspaceView.xaml(.cs)` | 双标签、地址栏、Surface、Agent Activity Pane |

**关键语义**

**`Browser/BrowserBridgeCommandDispatcher.cs`**：命令分发，安全摘要投影到 UI Activity；实现 `IBrowserAutomationGate`（它的 pause/takeover 就是**执行闸门本身**）。构造时只接受 `BrowserPageLifecycle`：**控制状态不再自己持有**，读写的是进程内唯一权威实例（与能力通道、
生命周期清理点同一个对象）—— 只读快照、只写同一事实源，Bridge 是默认路径，因此这里读错就等于人工接管无效。**会话准入（ADR-097 D3，2026-10-18 替换旧的 grant 闸门）**：owner 来自**宿主注入的 Origin**
（`ConversationId ?? SessionId`，不由模型参数决定）；第一条可信命令经 `TryClaimControlOwner` 确认交页归属，随后 `AuthorizePageCommand` → 触碰 handler 前 `RevalidatePageCommand` → `finally` `CompletePageCommand`
（唯一票据、写世代、取消信号都在权威内；释放后不可复用）。无 PageId 的引导/发现命令（context.list/getInfo、page.list、context.create）不要求先交页，其余管理命令必须绑定已交出上下文。
（本文件被链接编译进 WinUI Shell，Shell 不引用 `PuddingBrowser.Automation`/`Authorization` ⇒ 门面签名只出现 Contracts/标量类型，否则 `CS0012` 打断 Shell 构建）。**幂等**：以 `BrowserOperationLedger` 为**唯一**来源（`TryBegin` 四态驱动；指纹 = 调用者身份 + 能力名 + 目标 + 完整载荷，deadline 等非语义元数据不入）⇒ 同指纹在途等同一完成源、已完成复用终态、
**换调用者/换载荷即冲突**，不再另造按 Guid 的 in-flight/调用者缓存。**暂停/接管只阻断副作用**：先按命令名判定只读类别（`ReadOnlyPageCommands`），只读命令继续走授权闸门并执行，写命令返回 `browser_paused`/`browser_user_takeover`（ADR-097 D4；修复前在识别命令前就拒绝全部命令，导致"接管后可读"只在文档里成立）

**`Browser/BrowserWorkspaceController.cs`**：Tab/Activity/active/target 唯一 UI 状态源；**接管/暂停的唯一写入点**：`SetUserTakeoverAsync`/`SetPausedAsync` 把状态推给所有已注册的执行闸门（不推 ⇒ 「人工接管」按钮对 Agent 无效）。桥接命令的宿主侧执行者：
`page.interact` 的 `scroll` 会解析 `ScrollTarget`（显式线名优先，否则按 locator/位移推导；**未登记线名拒绝而不猜测**）并把驱动的滚动事实回传到响应。另暴露 `TryGetPageVersion(PageId)`（页面真实版本，未知返回 false）供 Shell 目标登记/版本喂入使用——**不伪造版本 0**

**`Browser/BrowserAutomationGateSync.cs`**：`IBrowserAutomationGate` 端口 + 闸门集合 + `DelegateAutomationGate`：注册/撤销、逐一推送、单个闸门抛异常不影响其余（接管是安全动作，不得半途失效）


## 运行中心 & 诊断

| 文件 | 用途 |
|------|------|
| `ViewModels/RuntimeCenterViewModel.cs` | Core 状态、PID、健康、启停重启；`DeployFrontendAsync` 防重入门控 |
| `Views/RuntimeCenterView.xaml(.cs)` | 运行中心 UI，500 行日志视口；「构建并部署前端」按钮（成功反馈目标目录与文件数） |
| `Runtime/DiagnosticBundleService.cs` | 诊断 ZIP，过滤敏感信息 |

## 系统集成

| 文件 | 用途 |
|------|------|
| `Runtime/DesktopSingleInstanceService.cs` | Semaphore + Named Pipe 单实例 |
| `Runtime/DesktopTrayIconService.cs` | WPF/Win32 托盘菜单；浅色 Popup Header 显式前景色，避免继承深色主题白字 |
| `Runtime/DesktopBackgroundModeService.cs` | 关闭到托盘策略 |
| `Runtime/AutoStartRegistrationService.cs` | HKCU Run 登录启动 |
| `Theming/WindowsThemeService.cs` | Windows Light/Dark + DWM Accent |
| `Theming/WindowsBackdropService.cs` | Mica、沉浸式深色、系统圆角 |

## 客户端素材

| 目录 | 用途 |
|------|------|
| `Assets/AgentSprites/` | 从 Web Workspace Studio 迁出的角色精灵源素材；当前不进入 Desktop 发布包，待未来原生客户端体验显式接入 |

## 存储 & 配置

| 文件 | 用途 |
|------|------|
| `Storage/StorageAnalysisService.cs` | DataRoot 分类与大小扫描 |
| `Storage/LogRetentionService.cs` | 24h 日志清理 |
| `Storage/CoreStorageManagementClient.cs` | 携带 Desktop ControlToken 调用 Core 存储 API 见下「关键语义」 |
| `Storage/DataRootSafetyValidator.cs` | 安全校验（拒绝越界、链接） |
| `ViewModels/StorageViewModel.cs` | 文件分类与 Core 数据库明细合并展示；7/14/30/90 天保留期、预览确认、清理后重扫 |
| `Configuration/SystemConfigurationService.cs` | system.json 原子写入 |
| `ViewModels/SettingsViewModel.cs` | Desktop Core 固定监听端口（默认 8080）、恢复与关闭策略、调试模式（仓库根/前端端口/代理端口/超时）配置 |

**关键语义**

**`Storage/CoreStorageManagementClient.cs`**：携带 Desktop ControlToken 调用 Core `/api/admin/storage/databases` 分析/预览/执行 API，不直接写 SQLite


## 测试

`../../Tests/PuddingDesktop.Tests/` — Desktop 进程/配置、Browser、Core Storage Client 与 Debug（路由/代理集成/SSE/WS、前端监督器、源码构建器）测试
