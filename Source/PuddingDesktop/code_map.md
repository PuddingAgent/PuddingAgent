# PuddingDesktop — WinUI 3 骨架

2026-09-27 按用户裁定在原工程重建。产物仍为 `PuddingDesktop.exe`；旧 WPF 在 `../PuddingDesktop.WpfArchive`，只保留迁移测试基线。

| 文件 | 职责 |
|---|---|
| `App.xaml(.cs)` | Windows 11 控件/主题，独立骨架单实例和激活转发，隔离配置、异常日志、组合入口 |
| `MainWindow.Kernel.cs` | 内核配置、工作台 WebView2、生命周期按钮、退出协调与真实进程 smoke |
| `MainWindow.xaml(.cs)` | 原生菜单、角色导航、草稿、五类文档标签、设置与运行中心、窄窗口折叠 |
| `Kernel/WinUiDesktopServices.cs` | Core 通过 DI 调用展示端口，DispatcherQueue 调度、取消及关闭处理 |
| `HostingProbeWindow.cs` | 手动/自检双 WebView2，独立 UDF 与离线页面，Cookie 隔离，原生浮层验证 |
| `PuddingDesktop.csproj` | WinUI 3/.NET 10/x64，非打包、AppSDK self-contained，禁止视图直接引用业务工程 |
| `../PuddingDesktop.Foundation` | 独立角色/文档/布局/配置状态与 `IDesktopKernel` 合同 |

默认无示例角色、发送禁用；“体验角色布局示例”或 `--demo` 明确启用静态样例。默认加载进程内 Core DLL，并通过隔离 WebView2 打开真实工作台。原生角色列表/Browser 工具适配仍待迁移。默认数据目录 `%LOCALAPPDATA%/Pudding/DesktopData`；`--demo` 跳过自动启动。运行中心支持启停/重启，关闭等待 Host 停止和释放；托盘与硬崩溃恢复待验收。

构建：`dotnet build Source/PuddingDesktop/PuddingDesktop.csproj -c Release --artifacts-path temp/build/desktop-kernel`。便捷入口：`TestScripts/start-pudding-winui-preview.ps1`；自检：`TestScripts/test-pudding-winui-skeleton.ps1`。结果及未验收项见 `Docs/Reports/Desktop-WinUI3-Skeleton-2026-09-27.md`。

Core 接入验证：`TestScripts/test-pudding-desktop-kernel.ps1`。Composition 是唯一引用 Host 的桌面装配层；Shell 禁止传递项目引用，View 不引用 Host/Runtime。
