# PuddingDesktop CodeMAP

> Windows WPF 产品入口 | 单实例 · Core 子进程管理 · Browser 工作区 · 运行中心

## 入口

| 文件 | 用途 |
|------|------|
| `App.xaml` | Windows 11 Light/Dark 样式 Token |
| `App.xaml.cs` | WPF 产品入口、单实例所有权、异常日志 |
| `MainWindow.xaml(.cs)` | 48px 标题栏、Navigation、Workbench/Browser/Runtime/Storage 页 |

## 进程管理

| 文件 | 用途 |
|------|------|
| `Hosting/DesktopApplicationCoordinator.cs` | 🔑 Launcher↔Core 状态机，协调 Runtime、Bridge、Workbench |
| `Core/CoreExecutableResolver.cs` | Core 路径确定性解析（配置→发布包→同源→兜底） |
| `Core/CoreProcessSupervisor.cs` | Core 子进程启动、健康检查、环形 stdout/stderr、进程树回收 |
| `Runtime/DesktopRuntimeOrchestrator.cs` | 异常退出恢复、退避熔断（2s/4s/8s，60s 3 次） |
| `Runtime/CoreRestartPolicy.cs` | 重启策略与取消语义 |

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
| `ViewModels/RuntimeCenterViewModel.cs` | Core 状态、PID、健康、启停重启 |
| `Views/RuntimeCenterView.xaml(.cs)` | 运行中心 UI，500 行日志视口 |
| `Runtime/DiagnosticBundleService.cs` | 诊断 ZIP，过滤敏感信息 |

## 系统集成

| 文件 | 用途 |
|------|------|
| `Runtime/DesktopSingleInstanceService.cs` | Semaphore + Named Pipe 单实例 |
| `Runtime/DesktopTrayIconService.cs` | WPF/Win32 托盘菜单 |
| `Runtime/DesktopBackgroundModeService.cs` | 关闭到托盘策略 |
| `Runtime/AutoStartRegistrationService.cs` | HKCU Run 登录启动 |
| `Theming/WindowsThemeService.cs` | Windows Light/Dark + DWM Accent |
| `Theming/WindowsBackdropService.cs` | Mica、沉浸式深色、系统圆角 |

## 存储 & 配置

| 文件 | 用途 |
|------|------|
| `Storage/StorageAnalysisService.cs` | DataRoot 分类与大小扫描 |
| `Storage/LogRetentionService.cs` | 24h 日志清理 |
| `Storage/DataRootSafetyValidator.cs` | 安全校验（拒绝越界、链接） |
| `Configuration/SystemConfigurationService.cs` | system.json 原子写入 |

## 测试

`../Tests/PuddingDesktop.Tests/` — Browser Controller/Client 阻断性测试（102/102 ✅）
