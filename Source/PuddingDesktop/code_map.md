# PuddingDesktop — WinUI 3 骨架

2026-09-27 按用户裁定在原工程重建。产物仍为 `PuddingDesktop.exe`；旧 WPF 在 `../PuddingDesktop.WpfArchive`，只保留迁移测试基线。

| 文件 | 职责 |
|---|---|
| `App.xaml(.cs)` | Windows 11 控件/主题，独立骨架单实例和激活转发，隔离配置、异常日志、组合入口 |
| `MainWindow.Kernel.cs` | 内核配置、原生 ChatWorkspace 装配、生命周期按钮、退出协调与真实进程 smoke |
| `MainWindow.xaml(.cs)` | 原生菜单、角色导航、草稿、五类文档标签、设置与运行中心、窄窗口折叠 |
| `MainWindow.Settings.cs` | 居中设置层、分类/页签/占位卡片、字段搜索、背景输入隔离、Esc/焦点恢复；保留外观和运行中心入口；DS-00 绑定所选工作区/角色到设置选择代次 |
| `Kernel/WinUiDesktopServices.cs` | Core 通过 DI 调用展示端口，DispatcherQueue 调度、取消及关闭处理 |
| `HostingProbeWindow.cs` | 手动/自检双 WebView2，独立 UDF 与离线页面，Cookie 隔离，原生浮层验证 |
| `PuddingDesktop.csproj` | WinUI 3/.NET 10/x64，非打包、AppSDK self-contained，禁止视图直接引用业务工程 |
| `../PuddingDesktop.Foundation` | 独立角色/文档/布局/配置状态与 `IDesktopKernel` 合同 |

默认加载进程内 Core DLL，使用 `PuddingChat.WinUI` 原生角色卡、消息卡、执行过程和输入框；通过 Composition 的 `InProcessChatClient` 直接调用 Core 服务。聊天不再加载 WebView2。“初始化与配置”由用户点击后打开独立管理页面。默认数据目录 `%LOCALAPPDATA%/Pudding/DesktopData`；`--demo` 仍为旧骨架静态布局样例并跳过自动启动。运行中心支持启停/重启，关闭先取消并排空聊天调用，再释放 Host；托盘与硬崩溃恢复待验收。

构建：`dotnet build Source/PuddingDesktop/PuddingDesktop.csproj -c Release --artifacts-path temp/build/desktop-kernel`。便捷入口：`TestScripts/start-pudding-winui-preview.ps1`；自检：`TestScripts/test-pudding-winui-skeleton.ps1`。结果及未验收项见 `Docs/Reports/Desktop-WinUI3-Skeleton-2026-09-27.md`。

Core 接入验证：`TestScripts/test-pudding-desktop-kernel.ps1`。Composition 是唯一引用 Host 的桌面装配层；Shell 禁止传递项目引用，View 不引用 Host/Runtime。

设置中心骨架及 DeepSeek 后续任务：[交接任务书](../../Docs/Tasks/Desktop-Admin-Settings-DeepSeek-2026-09-27.md)。本轮无新增业务 Client/API 包装器，后续直接绑定 Core 既有方法。默认数据目录已由 2026-09-27 裁定改为 `D:\data`（已保存值优先）；上文旧默认路径不再适用。DS-00 已交付：设置操作经 `IDesktopKernel.RunSettingsAsync` 进入 Core 作用域，未就绪/停止中拒绝并给出原因，切换角色作废迟到结果；见 [DS-00 接入说明](../../Docs/Features/Desktop-Settings-Operation-Boundary-2026-09-27.md)。
