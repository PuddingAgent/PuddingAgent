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
| `Hosting/DesktopApplicationCoordinator.cs` | 🔑 Launcher↔Core 状态机，协调 Runtime、Bridge、Workbench、调试组件；`DeployFrontendAsync` 独立锁执行前端构建部署（目标=当前 Core 可执行目录，调试模式取源码构建输出目录） |
| `Core/CoreExecutableResolver.cs` | Core 路径确定性解析（配置→发布包→同源→兜底） |
| `Core/CoreProcessSupervisor.cs` | Core 子进程固定端口 `0.0.0.0` 启动、本机健康检查、环形 stdout/stderr、进程树回收；启动超时采用有效进度租约（静默超时 + 有界绝对上限）；`EnvironmentName` 可覆盖 Production |
| `Core/CoreStartupProgressMessage*.cs` | `PUDDING_DESKTOP_STARTING` 协议、严格解析与单调租约；校验协议版本/PID/序号，租约不替代 Ready/health 门禁 |
| `Runtime/DesktopRuntimeOrchestrator.cs` | 异常退出恢复、退避熔断（2s/4s/8s，60s 3 次） |
| `Runtime/CoreRestartPolicy.cs` | 重启策略与取消语义 |
| `Bootstrap/DesktopBootstrapSignalService.cs` | 🔑 Core 点火部署主管：`desktop-build` / `prebuilt-artifact` / `restart-only`，停 Core 后把产物事务部署到实际 `CoreExecutablePath` 目录，重启前后校验 `PuddingAgent.dll` SHA-256 |
| `Bootstrap/DesktopBootstrapHttpEndpoint.cs` | 仅回环 + ControlToken 的点火遥控 API；接收部署模式/预构建目录/期望哈希并返回结构化结果路径 |
| `Bootstrap/DesktopBootstrapSignal*.cs` | 信号协议、部署模式归一化与结果证据模型（产物/加载路径、复制计数、哈希、`assembliesReloaded`） |

## 调试模式（Debug）

| 文件 | 用途 |
|------|------|
| `Debug/DesktopReverseProxy.cs` | HttpListener 本机反向代理（默认 127.0.0.1:80）：后端前缀→Core、其余→前端 dev server；SSE 逐块 Flush、WebSocket 全双工中继（HMR）、上游 502 |
| `Debug/ProxyRoutePlanner.cs` | 纯路由决策：8 个后端前缀 + /admin SPA fallback（与 dev-up.py 语义逐条对齐） |
| `Debug/DebugBackendLauncher.cs` | `dotnet build` 源码后端并解析 `bin/Debug/net10.0/PuddingAgent.exe`（desktop-child 协议不变，Development 环境）；`ResolveOutputDirectory` 无需先构建即可给出输出目录 |
| `Debug/FrontendDevSupervisor.cs` | `pnpm run start:dev`（缺 node_modules 自动 install）、/admin/ 就绪探测、进程树回收、环形日志 |
| `Debug/FrontendBuildDeployService.cs` | 运行中心「构建并部署前端」：`pnpm run build` 出 dist 后清空并复制到 Core 可执行目录 `wwwroot\admin`（仅动静态文件，Core 运行中可安全执行）；目标目录防御性限定 `wwwroot\admin` 后缀 |
| `Debug/DebugRepositoryResolver.cs` | 仓库根向上自动解析 + 显式覆盖（repositoryRoot/frontendWorkingDirectory/backendProjectPath） |
| `Configuration/DesktopBootstrapSettings.cs` | `DesktopDebugSettings`（desktop.json `debug` 节：Enabled/端口/超时） |
| `Hosting/DesktopStateChangedEventArgs.cs` | `WorkbenchAddress`（调试=代理源）与 `CoreAddress`（控制面）分离 |

## Browser 工作区

| 文件 | 用途 |
|------|------|
| `Browser/DesktopBrowserBridgeClient.cs` | 认证 WebSocket Client，HelloAck、heartbeat、重连 |
| `Browser/BrowserBridgeCommandDispatcher.cs` | 命令分发，安全摘要投影到 UI Activity |
| `Browser/BrowserWorkspaceController.cs` | Tab/Activity/active/target 唯一 UI 状态源 |
| `Views/BrowserWorkspaceView.xaml(.cs)` | 双标签、地址栏、Surface、Agent Activity Pane |

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
| `Storage/CoreStorageManagementClient.cs` | 携带 Desktop ControlToken 调用 Core `/api/admin/storage/databases` 分析/预览/执行 API，不直接写 SQLite |
| `Storage/DataRootSafetyValidator.cs` | 安全校验（拒绝越界、链接） |
| `ViewModels/StorageViewModel.cs` | 文件分类与 Core 数据库明细合并展示；7/14/30/90 天保留期、预览确认、清理后重扫 |
| `Configuration/SystemConfigurationService.cs` | system.json 原子写入 |
| `ViewModels/SettingsViewModel.cs` | Desktop Core 固定监听端口（默认 8080）、恢复与关闭策略、调试模式（仓库根/前端端口/代理端口/超时）配置 |

## 测试

`../../Tests/PuddingDesktop.Tests/` — Desktop 进程/配置、Browser、Core Storage Client 与 Debug（路由/代理集成/SSE/WS、前端监督器、源码构建器）测试
