# Desktop / Core DLL 接入（2026-09-27）

## 结果与边界

PuddingDesktop.exe 已在同一进程加载 PuddingHost.dll。产品启动不再调用 PuddingAgent.exe，不使用 PID 监督、stdout Ready 或 HTTP shutdown。Console/历史 DesktopChild 留给源码开发和迁移基线，不打进新 Desktop 产品链。

依赖：WinUI → Foundation + Composition → PuddingHost。Foundation 是 BCL 叶组件；Shell 禁止传递项目引用，Composition 才能看见 Host。Core 不依赖 WinUI/WPF。

`IDesktopServices.ShowAsync(ShellPage)` 与 `OpenDocumentAsync(WorkspaceDocument)` 是直接 DI 调用，后者只展示调用方提供的只读内容，不读取任意文件或执行命令。WinUI 实现调度到 DispatcherQueue，支持取消、拒绝排队和关闭。Core 的 `DesktopPresentationController` 演示真实消费：已认证 admin 的 `POST /api/desktop/show/Settings` 直接调用桌面端口；匿名请求为 401。此 HTTP 路由供 Workbench 使用，Core 服务本身调用接口无需 HTTP。

## 生命周期与数据

- `InProcessKernel` 串行启动/停止，停止取消初始化；不同数据根不能覆盖运行中的 Host。失败时保留未释放会话，禁止重复装配。Host 自行请求停止时释放内核，窗口保留。
- `DesktopKernelFactory` 获取进程内唯一 Host 门禁和 DataRoot 文件句柄租约，完成 Build → Initialize → Start → bound address；停止后 Dispose。不是 DLL 热卸载。
- 默认 `%LOCALAPPDATA%/Pudding/DesktopData`，`StateRoot/desktop.kernel.json` 保存数据根。StateRoot 默认 `%LOCALAPPDATA%/Pudding/WinUiSkeleton`；`--state-root` 用于隔离验证。`--demo` 不自动启动内核。
- 运行中心提供启动、停止、重启、PID、地址和诊断入口。停止后更换数据根会保存配置并提示重开，避免沿用旧 WebView 登录环境。关闭窗口/退出菜单等待内核释放；启动失败仍可进入运行中心修复。
- Workbench 使用动态 Loopback HTTP 和原有业务鉴权；WebView2 profile 按数据根隔离，仅允许当前内核源导航，关闭通用 WebMessage 和弹窗。窗口材质保留 Mica / Mica Alt / Acrylic。
- Console 与 Desktop 新入口共同持有 `.pudding-host.lock`；文件存在不表示占用，文件句柄才是租约。旧构建不认识该租约，因此沿用旧 DataRoot 必须先停止旧 Core。

## 验证证据

1. Foundation 18 项独立测试：角色/草稿/文档、布局、配置、重复启动、取消初始化、停止失败防双开、Host 自行停止与释放。
2. Composition 真实集成测试：完整 ValidateOnBuild、初始化、ready、SPA HTML、相同数据根互斥、同进程禁止双 Host、401/已认证 UI 回调、停止后端口不可达、再次启动。
3. 实际 WinUI Release smoke：构建目录 PID 25684、发布目录 PID 49772，同进程加载发布目录的 PuddingHost.dll；真实工作台导航、后台到 UI 回调、重启、数据根切换配置保存、退出、数据目录租约释放均通过。脚本 `TestScripts/test-pudding-desktop-kernel.ps1`，每次使用新 temp/test-out 数据根。
4. 编译期反证：通过临时 MSBuild 编译输入向 WinUI 添加 PuddingHost 类型引用，得到 CS0246；无探针的全解决方案构建 0 错误。探针不进入产品源码。构建保留现有依赖/分析器警告。
5. 发布包验证包含 PuddingHost.dll、default-data、SPA 和 WinUI PRI/XBF，无 PuddingAgent.exe。曾发现发布遗漏 PRI/XBF 导致 XamlParseException，补齐资源后发布目录真实 smoke 通过；不是仅验证构建目录。
6. 材质与骨架回归使用 `TestScripts/test-pudding-winui-skeleton.ps1`；不启动真实 Core。

未读取或复制 D:/data 凭据，未重启已有产品，也不宣称生产数据切换或干净机器验收。

## 尚未迁移

原生左栏的真实角色摘要同步、专用嵌入布局与 ShellWebBridge 协议、Agent Browser 的 WinUI 本地适配、托盘、硬崩溃恢复与升级控制仍未完成。当前真实角色与会话通过原有 Web Workbench 使用。BrowserAutomationEnabled 为 false，不虚报浏览器工具可用；右栏示例终端/文件也不冒充真实工具执行。

## 复现

```powershell
dotnet test Source/PuddingDesktop.FoundationTests/PuddingDesktop.FoundationTests.csproj -p:CollectCoverage=false
dotnet test Source/PuddingDesktop.CompositionTests/PuddingDesktop.CompositionTests.csproj --artifacts-path temp/build/kernel-composition -p:CollectCoverage=false
powershell -ExecutionPolicy Bypass -File TestScripts/test-pudding-desktop-kernel.ps1
powershell -ExecutionPolicy Bypass -File TestScripts/start-pudding-winui-preview.ps1
```
