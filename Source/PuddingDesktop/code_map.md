# PuddingDesktop — WinUI 3 骨架

2026-09-27 按用户裁定在原工程重建。产物仍为 `PuddingDesktop.exe`；旧 WPF 在 `../PuddingDesktop.WpfArchive`，只保留迁移测试基线。

| 文件 | 职责 |
|---|---|
| `App.xaml(.cs)` | Windows 11 控件/主题，独立骨架单实例和激活转发，隔离配置、异常日志、组合入口 |
| `MainWindow.xaml(.cs)` | 原生菜单、角色导航、草稿、五类文档标签、设置与运行中心、窄窗口折叠 |
| `Kernel/UnconfiguredDesktopKernel.cs` | 明确 NotConfigured，不假报启动成功；未来换为真实 DLL Host adapter |
| `HostingProbeWindow.cs` | 手动/自检双 WebView2，独立 UDF 与离线页面，Cookie 隔离，原生浮层验证 |
| `PuddingDesktop.csproj` | WinUI 3/.NET 10/x64，非打包、AppSDK self-contained，禁止视图直接引用业务工程 |
| `../PuddingDesktop.Foundation` | 独立角色/文档/布局/配置状态与 `IDesktopKernel` 合同 |

默认无示例角色、发送禁用；“体验角色布局示例”或 `--demo` 明确启用静态样例。当前不加载 Core DLL、不接真实会话、不执行工具、不读生产 DataRoot。预览关闭即退出；托盘及生产单实例/升级生命周期留内核接入阶段。

构建：`dotnet build Source/PuddingDesktop/PuddingDesktop.csproj`。便捷入口：`TestScripts/start-pudding-winui-preview.ps1`；自检：`TestScripts/test-pudding-winui-skeleton.ps1`。结果及未验收项见 `Docs/Reports/Desktop-WinUI3-Skeleton-2026-09-27.md`。
