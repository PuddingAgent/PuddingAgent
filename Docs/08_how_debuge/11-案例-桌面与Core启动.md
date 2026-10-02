# 案例：桌面与 Core 启动

> 本文档是 [How-Debuge 调试与诊断手册](README.md)（主索引）的主题分册，由原根目录 `How-Debuge.md` 于 2026-10-02 按主题拆分而来。
> 新增本主题的经验请直接追加到本文件；跨主题内容请回到主索引选择分册。

## 11.9 Agent Benchmark 诊断

先 dry-run 检查服务端当前识别出的 deterministic cases；该操作不会调用付费模型：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\run_benchmarks.py --dry-run
```

单题 smoke：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\run_benchmarks.py `
  --case workspace-markdown-summary --label local-smoke
```

run 元数据和评价快照位于 `D:\data\runtime\benchmark-runs\`。出现 `unscored` 时先检查该 case 是否有 artifact oracle；不要把 Session diagnostics 的启发式分数当作任务完成。Token 为 0 时检查 `TokenUsageEvents` 的 `SessionId/ParentSessionId`，角色为空时检查 `sub_agent_runs.task_planning_metadata_json` 是否包含 `role_in_plan/profile_id`。

Benchmark Turn 必须在 ChatMessage/Command metadata 中保留 `excludeFromLearning=true`。经验→SKILL 意外收录基准轨迹时，先过滤该字段；不要通过按模型名猜测角色来修正统计。

## 11.10 PuddingDesktop 发布与 Workbench 静态资源诊断

Desktop 发布前先生成 Workbench：

```powershell
pnpm --dir .\Source\PuddingPlatformAdmin run build
dotnet publish .\Source\PuddingDesktop\PuddingDesktop.csproj -c Release --no-restore
```

Desktop 发布产物必须同时包含：

- `PuddingDesktop.exe`；
- `Microsoft.Windows.SDK.NET.dll`（`WebView2CompositionControl` 需要）；
- `core/PuddingAgent.exe`；
- `core/wwwroot/admin/index.html`。

缺失静态资源时先检查
`Source/PuddingPlatformAdmin/dist/index.html`，再检查可执行宿主是否导入
`PuddingHost/Build/PuddingHostContent.props`。`PuddingHost` 类库自身不导入该 props，
否则 Web SDK 可能在 `DiscoverPrecompressedAssets` 报同一 `dist` 文件 key 重复。

使用隔离的系统 Temp 配置启动真实 Desktop smoke，禁止复用 `D:\data`：

```powershell
.\TestScripts\start-phase1a-desktop-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase1a-win11-preview
```

窗口底部显示 `0.0.0.0:<configured-port>` 监听端点；运行中心悬停可查看同端口 Loopback 控制地址。确认以下路径均有非零响应体：

```powershell
$base = 'http://127.0.0.1:<configured-port>'
Invoke-WebRequest "$base/health/ready" -UseBasicParsing
Invoke-WebRequest "$base/admin/" -UseBasicParsing
Invoke-WebRequest "$base/admin/index.html" -UseBasicParsing
```

如果 `/admin/index.html`、CSS 或 JS 返回 `200` 但 `RawContentLength=0`，检查
`PuddingWebApplicationExtensions.MapPuddingApplication` 是否重新启用了
`MapStaticAssets()`。Desktop 的嵌套 `core/` 发布布局必须通过
`AppContext.BaseDirectory/wwwroot` 的 `PhysicalFileProvider` 提供静态文件，并用物理
`admin/index.html` 处理 SPA fallback。

如果 WebView2 报 `RedirectFailed`，检查是否显式映射了 `/admin` 到 `/admin/`。
ASP.NET Core 路由默认忽略末尾斜杠，这种映射会同时匹配 `/admin/` 并形成重定向循环；
让 `UseDefaultFiles` 单独处理即可。

如果标准 WebView2 覆盖标题栏或导航栏，确认 Workbench 使用
`WebView2CompositionControl`，项目 TFM 为
`net10.0-windows10.0.17763.0`。如果启动异常包含缺失
`Microsoft.Windows.SDK.NET, Version=10.0.17763.10`，检查发布目录是否包含
`Microsoft.Windows.SDK.NET.dll`。CompositionControl 创建后必须在
`EnsureCoreWebView2Async` 前设为 `Visible`，否则可能一直停留在初始化遮罩。

Desktop 在 Core 和 Serilog 之前发生的启动/XAML/WebView2 异常写入：

```text
%LOCALAPPDATA%\Pudding\logs\desktop.log
```

smoke 脚本通过 `PUDDING_DESKTOP_HOME` 将该日志重定向到
`<smokeRoot>/desktop-home/logs/desktop.log`。该环境变量只控制 Desktop 自身配置目录，
Core Token、端口和 DataRoot 仍必须通过配置文件与启动参数传递。

如果 Desktop 中出现“系统初始化”，但日常开发环境已经初始化，先看窗口底部的
`数据` 路径。`start-phase1a-desktop-smoke.ps1` 会故意创建新的系统 Temp DataRoot，
该目录没有 `runtime/bootstrap-state.json`，因此显示初始化向导是正确行为；它不能
代表 `D:\data` 的初始化状态。使用真实数据验证时必须先停止 `dev-up.py`，避免两个
Core 同时访问 `D:\data`，再让 Desktop 的 `desktop.json` 指向 `D:\data`。已初始化
环境的正确首屏是登录页，认证完成后进入 Workbench `/` 产品首页；如果仍进入
`/bootstrap`，依次检查窗口底部 DataRoot、`/api/bootstrap/status` 和浏览器 UDF。

Desktop 项目切换到带 Windows 版本的 TFM 后，如果构建提示
`project.assets.json` 缺少 `net10.0-windows10.0.17763.0`，先执行：

```powershell
dotnet restore .\Source\PuddingDesktop\PuddingDesktop.csproj
dotnet build .\Source\PuddingDesktop\PuddingDesktop.csproj --no-restore
```

这是旧 NuGet assets 的目标框架缓存，不应通过降低 TFM 或移除
`WebView2CompositionControl` 解决。导航 XAML 改名后若 IDE 仍报告 `navLogs`，先用
`rg -n "navLogs" Source/PuddingDesktop` 核对磁盘源码；当前导航字段是
`navWorkbench`、`navCore`、`navStorage`、`navSettings`，磁盘已无旧引用时重新加载项目或重建即可。

如果 Desktop Workbench 的 `GET /api/workspaces/{workspaceId}/agents/status` 返回 500，
先用响应中的 `errorId` 搜索 `D:\data\logs\error` 和 `D:\data\logs\system`。若堆栈落在
`PlatformApiClient.GetSessionsAsync`，并显示连接 `localhost:5000` 被拒绝，说明 Core 已经
按 Desktop 配置监听，但内部控制面请求仍在使用错误的默认地址。检查
`PuddingControllerAddressRewriteHandler` 是否已注册到 `PlatformApiClient`，以及
`PuddingApplicationHost.CaptureBoundAddresses` 是否已把实际地址写入
`IPuddingServerAddressAccessor`。

HttpClient 的起始日志可能在 DelegatingHandler 执行前显示原始
`http://localhost:5000/...`，不能仅凭这一行判定重写失败。有效验收证据是后续
`Sending HTTP request` 指向配置端口的 `127.0.0.1:<port>` 本机控制地址，下游请求返回 200，
并且原始 Agent 状态接口也返回 200。

发布报 `NETSDK1152` 且路径同时出现两个
`Microsoft.CodeAnalysis.Workspaces.MSBuild` 版本时，查各被引用项目的传递依赖：

```powershell
dotnet list .\Source\PuddingMemoryEngine\PuddingMemoryEngine.csproj package --include-transitive
dotnet list .\Source\PuddingPlatform\PuddingPlatform.csproj package --include-transitive
dotnet list .\Source\PuddingCodeIntelligence\PuddingCodeIntelligence.csproj package --include-transitive
```

EF Design 和运行时 Code Intelligence 必须解析到同一 Roslyn Workspace 版本；
不要通过禁用 `ErrorOnDuplicatePublishOutputFiles` 隐藏冲突。

## 11.11 PuddingDesktop Storage 统计与旧日志清理诊断

Storage 页面显示的是 DataRoot 中文件的**逻辑大小**，不是 NTFS 精确物理占用；顶部磁盘条来自 `DriveInfo`，表示整个卷的已用/可用空间。两者口径不同，不能用分类大小之和反推磁盘已用空间。

Storage 定向验证必须使用系统 Temp 下的隔离 DataRoot，禁止让自动化测试或清理 smoke 指向 `D:\data`：

```powershell
dotnet test .\Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo
dotnet publish .\Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release --no-restore `
  -o .\.tmp-build\phase1b-storage-preview
.\TestScripts\start-phase1a-desktop-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase1b-storage-preview
```

扫描出现 Warning 时按页面给出的路径检查访问权限、扫描中消失的文件和 Junction/Reparse Point。扫描器不会跟随链接，也不会因为单个目录无权访问而让窗口崩溃。分类采用 first-match：`browser/downloads`、`screenshots`、`traces` 必须先于 Browser UDF；各分类文件数和逻辑大小之和应等于总计。

V1 日志清理边界固定为：

- 只允许真实 `<DataRoot>/logs`，DataRoot 不能是空路径、相对路径、盘符根目录或 Reparse Point；
- 只处理 `.log`、`.jsonl`、`.txt`、`.gz`、`.zip`，且 `LastWriteTimeUtc` 早于 24 小时 cutoff；
- UI 必须先 Preview，再内联确认；执行前重新检查路径、长度、创建/修改时间和 cutoff；
- 已变化、已消失、正在占用、越界或扩展名不允许的文件跳过/失败，不能扩大为递归通用删除；
- 只移除 logs 下已经为空的真实子目录，不删除 logs 根目录；完成后立即重扫。

启动即出现 `XamlParseException` 且提示只读属性不能 `TwoWay` 绑定时，检查 `ProgressBar.Value` 等控件是否显式使用 `Mode=OneWay`。该问题可以通过编译但会在真实窗口加载时失败，因此 Storage 改动必须保留发布包视觉 smoke。

WPF 会生成 `PuddingDesktop_*_wpftmp.csproj`。若自定义 `BaseOutputPath` 后出现临时项目找不到 `PuddingCore.dll`，先回到普通项目输出或只用 `dotnet publish -o <仓库临时目录>` 隔离发布产物；不要把 `BaseOutputPath`、`OutDir` 或测试输出指向运行时 DataRoot。

## 11.12 PuddingDesktop 运行中心、单实例与自动恢复诊断

运行中心的进程职责分为两层：`CoreProcessSupervisor` 只管理一次 Core 启停和进程树，`DesktopRuntimeOrchestrator` 管理异常恢复、退避、熔断和用户意图。默认策略是 2s/4s/8s 退避，60 秒窗口内允许 3 次恢复，继续失败进入 `CoreCircuitOpen`。用户点击“停止”、配置无效或 DataRoot 缺失都不得自动拉起。

DesktopChild 的监听端口来自 `<DataRoot>/config/system.json` 的 `desktop.core.port`，必须为 `1–65535`，默认 `8080`。进程命令行应包含 `--urls http://0.0.0.0:<port>`，`PUDDING_DESKTOP_READY` 则报告 `http://127.0.0.1:<port>` 给 Desktop 控制链路。若启动失败，先用 `Get-NetTCPConnection -State Listen -LocalPort <port>` 判断端口占用，再看运行中心最近 stderr 中的 Kestrel bind 错误；系统不会静默回退到随机端口。局域网访问还需确认 Windows 防火墙允许该入站端口。

定向验证：

```powershell
dotnet build .\Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo
dotnet test .\Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo
dotnet publish .\Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release --no-restore `
  -o .\.tmp-build\phase1b-runtime-preview
.\TestScripts\start-phase1a-desktop-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase1b-runtime-preview
```

上述 Desktop build/test/publish 必须串行执行。`dotnet build PuddingDesktop` 与引用同一 WPF 项目的 `dotnet test PuddingDesktop.Tests` 若并行共享默认 `obj`，可能在 `Microsoft.WinFX.targets` 报 `RG1000` 和重复 `mainwindow.baml`；串行重跑即可，不要因此修改 XAML 资源名或清理 DataRoot。

真实故障恢复 smoke 必须使用脚本创建的系统 Temp `DesktopHome` 和 DataRoot。终止进程前同时核对 Core 的 `ParentProcessId`、`ExecutablePath`、`--data-root` 参数与隔离目录，禁止对真实 `D:\data` Core 或名称匹配的一组进程执行批量 Kill。正常结果是：旧 Core 退出后运行中心先进入“等待自动恢复”，随后出现新的 PID，监听端点仍为同一个配置端口；点击“停止”后至少等待一个最大退避周期，Desktop 仍存活但 Core 子进程数保持为 0。

Desktop 使用本地命名 `Semaphore` 保证单实例，并通过仅当前 Windows 用户可访问的 Named Pipe 发送激活信号。发现第二个窗口或第二个 Core 时，检查两个启动进程是否使用同一版本的 Desktop 和同一 `PUDDING_DESKTOP_HOME`；开发中的旧版本不参与新版单实例协议。实例发现文件位于：

```text
<DesktopHome>/desktop.instance.<instance-key>
```

默认关闭按钮只隐藏主窗口，Core 和外部 HTTP API 会继续运行；这不是退出失败。使用托盘菜单“退出 Pudding”才会执行明确退出并停止 Core/WebView2。托盘图标在 Explorer 重启后由 `TaskbarCreated` 消息重建。若托盘初始化失败，检查 `%LOCALAPPDATA%\Pudding\logs\desktop.log` 中的 `[Desktop] Tray initialization failed`，窗口仍应保持可用。若菜单文字在浅色弹出层上不可见，检查 `DesktopTrayIconService` 的独立 `Background` / `Foreground`，并确认 `MenuItem.Header` 使用显式前景色的 `TextBlock`：字符串 Header 会创建隐式 `TextBlock`，其全局深色主题样式会覆盖 MenuItem 继承色，造成白底白字。原生 `ComboBox` 也应通过显式 `ItemTemplate` 使用 `NativeLightPopupTextBrush`。若标题栏最小化、最大化、关闭图标过小或不可见，检查 `TitleBarButtonStyle` 的 Fluent 图标字号，以及其 `ContentPresenter` 是否显式传递 `Foreground`、`FontFamily` 与 `FontSize`。

从 Visual Studio 启动 Desktop 后，若 Core 在 Ready 前连续退出并报告 `DirectoryNotFoundException: Source\PuddingAgent\wwwroot`，说明 WPF 启动环境错误地把 ASP.NET Core 的 Development 静态资源清单行为传给了子进程。`CoreProcessSupervisor` 必须对 DesktopChild 显式设置 `ASPNETCORE_ENVIRONMENT=Production` 和 `DOTNET_ENVIRONMENT=Production`，并把工作目录设为 Core 可执行文件目录；Workbench 由输出或发布目录中的物理 `wwwroot` 提供。`PuddingDesktop/Properties/launchSettings.json` 不应包含 ASP.NET Core URL 或环境变量。

从 Visual Studio 直接启动 `PuddingDesktop.csproj` 时，Desktop 不再通过 ProjectReference 隐式构建 Core；WPF 与 ASP.NET Core 保持独立进程/项目边界。开发态解析顺序仍是显式配置、发布包 `core/PuddingAgent.exe`、Desktop 同目录产物、`Source/PuddingAgent` 旧布局兜底，因此只重新构建 Desktop 可能继续启动旧 Core。若运行中心反复退出，且异常文案与当前源码不一致，先核对日志中的实际 `ExecutablePath`、文件时间和完整启动参数；自动恢复只会重启同一产物，不会编译源码。通过 `/desktop/bootstrap/start` 点火时默认使用 `deploymentMode=desktop-build`：Core 全停后由 Desktop 编译，把完整产物先暂存再提交到实际 `CoreExecutablePath` 所在目录，并在重启前后校验入口 DLL 与全部托管 DLL/EXE/deps/runtimeconfig 的确定性清单指纹。验收必须同时满足 `/desktop/bootstrap/status` 的 `coreState=Ready`、`lastResult.success=true`、`assembliesReloaded=true`、`preparedArtifactManifestSha256 == loadedArtifactManifestSha256`、新 Core PID，以及 `http://127.0.0.1:<port>/health` 返回 `healthy`；不能只看 build exit code，也不能只校验 `PuddingAgent.dll` 而漏掉实际承载点火工具的 `PuddingRuntime.dll`。`prebuilt-artifact` 仅接受仓库根内的绝对产物目录，可附期望哈希；`restart-only` 是显式降级，不代表源码已加载。DesktopChild 不是 `dotnet watch`，源码修改不会热替换到已运行进程。

Desktop 默认关闭到托盘且是单实例：窗口消失不代表进程退出。若旧实例仍在托盘，VS 启动的新进程只会激活旧实例，旧 Core 也会继续运行，看起来就像新后端没有生效。调试新构建前先从托盘选择“退出 Pudding”，再确认 `PuddingDesktop.exe` / 其子 `PuddingAgent.exe` 已退出；不要同时用 `dev-up.py` 和 Desktop 访问同一个 DataRoot。

运行中心“生成诊断包”只在用户点击时写入 `<DataRoot>/diagnostics/`。ZIP 应只包含运行快照、最近 Core 输出和配置键名；出现 ControlToken、Authorization、Cookie、API Key 或 Secret 值即视为缺陷。不要把完整 `system.json` 或 WebView2 UDF 直接加入诊断包。

“登录 Windows 后启动”只在用户保存 Desktop 设置时更新 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，不应在普通启动或测试构造期间隐式修改注册表。后台启动使用 `--background`；窗口创建和配置错误仍然完成，随后隐藏到托盘，不能因 Core 启动失败终止 Desktop。

运行中心的多行日志框若只显示成一行，先检查 `App.xaml` 的全局 `TextBox` 样式：普通输入框默认 `Height=34`，日志控件必须显式使用 `Height=Auto`、足够的 `MinHeight` 和顶部内容对齐。`PART_ContentHost` 应保持 Stretch，并通过 `HorizontalContentAlignment` / `VerticalContentAlignment` 承接控件设置；不要把 ScrollViewer 自身设为 Top，否则它仍可能按单行内容高度测量。该问题能通过编译，必须在重启 Desktop 后做实际窗口检查。

## 11.13 Phase 2A Agent Browser 与 Desktop Bridge 诊断

先区分三个互不等价的状态：窗口底部 `Core` 表示子进程/健康状态，Agent Browser 右栏 `Bridge Status` 表示认证 WebSocket 状态，Workbench Ready 表示 `/admin/` 的 WebView2 导航完成。Core 可以运行而 Workbench 尚未打开；Browser 在 Core 停止时也应保持可用。

标准验证命令：

```powershell
dotnet test .\Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet test .\Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo
dotnet publish .\Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release -o .\.tmp-build\phase2a1-final-preview --nologo
.\TestScripts\start-phase2a1-browser-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase2a1-final-preview -KeepArtifacts
```

smoke 只使用 `%TEMP%\PuddingAgent\phase2a1-browser-<guid>`，不得改指向 `D:\data`。脚本拒绝在另一个 Desktop 运行时启动，报告 Desktop/Core PID、两个 UDF 和退出后的残留子进程。Workbench UDF 是 `<DataRoot>/browser/workbench/user-data`，Agent Browser UDF 是 `<DataRoot>/browser/agent-browser/user-data`；两者相同即为缺陷。

Bridge 排查顺序：

1. `Core 运行中` 但 Bridge 为 `Disconnected`：先核对 Core 是 `--desktop-child`、监听地址是配置的 `0.0.0.0:<port>`、Ready 控制地址是同端口 Loopback，并检查 `system.json` 的 ControlToken 是否存在；Token 只允许进入 `X-Pudding-Desktop-Token` Header，禁止写入 UI、异常或诊断包。
2. 一直 `Connecting`：检查 Desktop 是否先启动 Receive Loop 再发送 Hello；HelloAck 前不能进入 Connected，也不能有第二个 Receive Loop。
3. 立即 `Failed`：Host 只接受 Loopback WebSocket、DesktopChild 模式、正确 Token 和匹配协议版本。用 `DesktopBrowserBridgeAuthenticationTests` 与 `DesktopBrowserBridgeHandshakeTests` 区分 401/403、非 WebSocket、首消息错误和协议拒绝。
4. 静默连接 45 秒后仍不掉线：检查 heartbeat watchdog 是否可取消阻塞 Receive。测试必须使用 fake clock，不等待真实 45 秒。
5. Restart 后旧错误覆盖新连接：检查 connection generation。generation N 的 Receive/finally/pending 完成不得改变 N+1；旧命令断线后不得重放。
6. pending 命令在断线后悬挂：应返回 `browser_bridge_disconnected`；Dispatcher handler 被移除时应返回 `browser_not_available`。

Tab/Surface 排查顺序：

- `BrowserWorkspaceController` 是 `Tabs`、`Activities`、`ActivePageId`、`AgentTargetPageId` 和导航状态的唯一事实源；View 不得再次使用 `DataContext=this` 或维护复制集合。
- active tab 只决定可见 Surface，Agent target 只决定无显式 PageId 的命令目标。切换 active tab 不得改 target；关闭 target 后命令返回 `page_not_found`，不能偷偷回退 active tab。
- 每次创建 Page 必须创建一个 Surface；`WpfBrowserSurfaceHost.ActivateAsync` 只让目标 Surface `Visible/IsHitTestVisible`。若页面创建永久等待，检查新 `WebView2CompositionControl` 是否在 `EnsureCoreWebView2Async` 前被设为 `Collapsed`；初始化阶段应使用 `Hidden` 保持在 WPF layout，完成后再由 Controller 激活。
- Workbench 只在其页面可见时初始化。若从 Agent Browser 启动 Core 后长期停在 `Workbench 加载中`，检查是否又对父级为 Collapsed 的 Workbench 调用了 `EnsureCoreWebView2Async`。
- Activity 只保存动作名、Page 摘要、时间、结果和错误码，最多 100 条；不得显示完整 Arguments、脚本、Token、Cookie 或表单值。

发布窗口在 `InitializeComponent` 前后退出时读取：

```text
<DesktopHome>/logs/desktop.log
```

`XamlParseException` 提示 `#AARRGGBB` 不是 `Foreground` 有效值，通常是把 `*Color` 资源直接绑定到了 Brush 属性，应改用对应 `*Brush`。`desktop.json` 的 `closeBehavior` 使用 `MinimizeToTray` 或 `ExitAndStopCore` 字符串；无法反序列化时检查 `DesktopCloseBehavior` 的 `JsonStringEnumConverter`。退出阶段 Browser/WebView2 释放必须有界，正常明确退出的脚本结果应为 Desktop exitCode 0 且 `remainingChildProcessIds=[]`。

## 11.14 Phase 2A-2 Remote Browser 与 Agent Tools 诊断

先确认工具是否应该存在。`browser_context`、`browser_tabs`、`browser_navigate` 只在以下两个条件同时满足时注册：

```text
PuddingHostOptions.Mode == DesktopChild
PuddingHostOptions.BrowserAutomationEnabled == true
```

普通 Console/dev Host 看不到 Browser Tools 是正确隔离，不应通过注册空实现或全局打开能力“修复”。DesktopChild 仍看不到工具时检查 `AddPuddingApplicationServices(hostOptions)` 是否把同一个 `PuddingHostOptions` 传到 `AddDesktopBrowserAutomation()`，并运行：

```powershell
dotnet test .\Tests\PuddingHost.Tests\PuddingHost.Tests.csproj `
  --no-restore --nologo `
  --filter "FullyQualifiedName~BrowserBridgeServiceCollectionExtensionsTests"
```

工具错误按稳定 `error.code` 排查：

1. `browser_not_available`：Desktop 尚未为 Browser Workspace 安装 Dispatcher，通常是 DataRoot 未 Ready、Browser 初始化失败或 Desktop 正在退出。
2. `browser_bridge_disconnected`：Core Broker 没有已完成 HelloAck 的当前 Desktop 连接。先看 Agent Browser 的 Bridge Status，再检查 generation、watchdog 和 Core Restart 日志。
3. `browser_context_not_found` / `browser_page_not_found`：重新调用 `browser_context list` 或 `browser_tabs list`，不要从旧 Core proxy 缓存推测 Desktop 状态。
4. `browser_operation_not_supported`：调用了 Phase 2A-2 尚未开放的 Snapshot、Locator、输入、Evaluate、CDP、Cookie 或文件能力；这是明确边界，不是 Bridge 失败。
5. `browser_invalid_arguments`：检查 action 枚举以及 ContextId、PageId、Url 必填组合。
6. `browser_deadline_exceeded` / `browser_cancelled`：检查 Tool cancellation、Bridge command `DeadlineUtc` 和 Desktop WebView2 是否仍在 UI 线程响应；调用方取消应继续表现为 `OperationCanceledException`。

最短自动化闭环：

```powershell
dotnet test .\Tests\PuddingBrowser.AgentTools.Tests\PuddingBrowser.AgentTools.Tests.csproj `
  --no-restore --nologo
dotnet test .\Tests\PuddingHost.Tests\PuddingHost.Tests.csproj `
  --no-restore --nologo `
  --filter "FullyQualifiedName~BrowserAgentToolBridgeIntegrationTests|FullyQualifiedName~RemoteBrowserRuntimeTests"
```

第二条测试证明 Tool → `IBrowserRuntime` → Remote proxy → Broker → 认证 WebSocket → Desktop result，不需要真实模型或外网。若它通过而真实 Agent 看不到工具，检查 Agent capability：新通用助手默认包含 `cap-browser-context`、`cap-browser-tabs`、`cap-browser-navigate`，既有 Agent 不会被升级过程静默扩权，必须在配置界面选择对应能力。

Core Restart 后 Tab 消失通常不是 Remote proxy 的预期行为。`RemoteBrowserRuntime.DisposeAsync()` 不发送 close；Desktop 仍拥有 Context/Page。新 Core 应先执行 `context.list` 并恢复代理。若 Tab 真被关闭，搜索显式 `context.close`/`page.close` Activity，而不是给 proxy Dispose 添加兼容性恢复。

Agent Activity 只允许记录动作名、Context/Page 摘要、时间、结果和错误码。不得为了诊断写入完整 tool arguments、脚本、URL query secret、Cookie、Token 或表单值。

Phase 2A-2 发布 smoke 继续使用：

```powershell
.\TestScripts\start-phase2a1-browser-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase2a2-minimal-preview `
  -KeepArtifacts
```

正常明确退出必须同时满足 Desktop exitCode 0 和 `remainingChildProcessIds=[]`。本 smoke 只证明 Desktop/WebView2/Bridge/退出表现；真实模型是否正确选择 Browser Tool 必须另做 DeepSeek 可见 smoke，不能由集成测试替代。

## 11.15 Phase 2A-3 Snapshot、Locator、Interact 与 Wait 诊断

DesktopChild 启用 Browser Automation 后应有七项 Browser Tool。若只有三项导航工具，先检查运行的 Core 是否来自最新 Release，以及 Agent 是否显式拥有以下新增能力；既有 Agent 不会被升级过程静默扩权：

```text
cap-browser-snapshot
cap-browser-locate
cap-browser-interact
cap-browser-wait-for
```

典型调用顺序是 `browser_snapshot` → 使用返回 ref 或 `browser_locate` → `browser_interact` → `browser_wait_for`/新 Snapshot。ref 格式为 `v{PageVersion}-n{sequence}`。导航后出现 `stale_element_reference` 是正确保护，必须重新 Snapshot；禁止去掉版本校验或自动回退到文本/CSS Locator。

稳定错误按 code 排查：

1. `browser_element_not_found`：Locator 为 0 个匹配；重新 Snapshot，检查动态 DOM 和 PageId；
2. `browser_locator_ambiguous`：多个匹配但没有 `Nth`；缩小 role/name/text 或明确索引；
3. `browser_element_not_visible` / `browser_element_disabled`：页面状态尚未就绪；先 Wait，不能强制 JS click；
4. `stale_element_reference`：PageVersion 已变化；重新 Snapshot，不重复已经提交的动作；
5. `browser_operation_not_supported`：Frame/复合 Has、drag、upload、Evaluate、CDP、Cookie 等不在 Phase 2A-3；
6. `browser_invalid_arguments`：检查 interaction action 的 Locator、text、values、checked 组合。

click、press 或表单动作可能已经提交并触发导航。Desktop 和 Agent Tool 在交互成功后不会重新查询旧 Locator，返回的 `Element` 可以为空；调用方应 Wait 或新 Snapshot。若 Activity/Tool result 中看到 fill/type 的原始值、完整 Locator、页面正文、ControlToken 或 Cookie，视为敏感信息泄漏缺陷。

确定性与真实 WebView2 验收：

```powershell
dotnet test .\Tests\PuddingBrowser.AgentTools.Tests\PuddingBrowser.AgentTools.Tests.csproj --no-restore --nologo
dotnet test .\Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet test .\Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo
dotnet build .\Tests\PuddingBrowser.TestSite\PuddingBrowser.TestSite.csproj --no-restore --nologo
dotnet build .\Tests\PuddingBrowser.WebView2.Smoke\PuddingBrowser.WebView2.Smoke.csproj --no-restore --nologo
.\TestScripts\start-phase2a3-webview2-smoke.ps1 -HoldSeconds 0
```

正常 smoke 输出包含 `phase2a3-webview2-smoke-passed`、`pageVersion=2`、`finalContainsSaved=true` 和 `staleCode=stale_element_reference`。脚本只使用 `%TEMP%\PuddingAgent\phase2a3-webview2-*`；WebView2 子进程可能在 WPF 退出后短暂锁定 UDF，因此清理有界重试，最终锁定只报告 Warning，不能掩盖页面断言结果。测试进程退出后若仍有 Pudding/TestSite 进程，按脚本输出的精确 PID、ExecutablePath 和 Temp DataRoot 核对所有权，禁止按名称批量终止用户的 WebView2/浏览器进程。

真实 DeepSeek smoke 不能由上述集成测试代替。只有用户明确选择测试 Agent/DataRoot 后才能执行；不得读取、复制或回显 `D:\data` 中的 LLM Secret。需要保留的证据是脱敏 Tool 顺序、provider/model/role、Bridge Activity、最终页面和退出结果，不包含表单值、Token、Cookie 或 API Key。

## 11.16 用户消息无输出、后台投递抢占与过期心跳执行诊断

聊天页出现用户消息、Core `/health` 仍为 200，但长期没有 `thinking/delta/terminal` 时，不要先归因前端或 Provider。按同一 `conversationId` 对齐四组事实：

1. `chat_execution_commands` / Turn / Run：确认消息已受理，区分 `pending` 与 `running`，记录 `commandId/turnId/runId`；
2. `session_event_log`：确认该 run 是否只有 `turn.accepted/turn.started`，以及同 Agent 另一会话最近执行的首条 thinking 是否来自 `system:heartbeat`；
3. `message_deliveries`：只检查 claim 前投递与受理审计。先按 `handling_mode` 分流：`execute` 的 Agent/获准 heartbeat 在 canonical Turn 受理后应很快变为 `delivered`；`notify` 在被动消息事件落盘后 ACK。两类受理失败才进入 `retrying/dead_letter`；
4. `ChatMessages`、`session_event_log` 与 Conversation projection：确认 Message Fabric 输入卡已经出现，随后是否有 `turn.started/thinking/tool/delta/terminal`。输入卡存在而无处理卡属于 canonical Turn 消费问题，不再属于消息队列问题；
5. `runtime_activity` 与 `AgentExecutionStateRegistry`：若 command 已是 `running`、Agent 实际持续跑工具而 registry 仍显示 idle，再检查是否绕过 `RuntimeAgentDispatcher`。

若用户消息停在 `turn.accepted`，而同 Agent 的 `source=subagent` / `intent=subagent_result` 专用 continuation 正在执行，可能是该专用后台路径占用了 Agent 单写执行槽。可执行 Agent-to-Agent 消息与获准 heartbeat 不应再长时间停在 `message_deliveries=delivering`：它们在 claim 后受理 canonical Turn 并立即 ACK，执行状态迁入 `chat_execution_commands` 与 Conversation 事件；被动通知则直接进入 Conversation 消息事件。当前不变量是：

- 用户 Turn、`handling_mode=execute` Agent 消息与获准 heartbeat 最终都由 canonical `ChatExecutionWorker -> RuntimeAgentDispatcher` 执行，并产生一致的消息卡、处理卡、思考和工具轨迹；`notify` 只有消息卡，不应伪造处理卡或思考轨迹；
- `MessageDelivery` 队列所有权在 claim 时结束：默认队列只展示 `queued/retrying` delivery 与 `pending` command；`delivering/leased/running/cancel_requested` 仅在显式诊断查询出现，不能作为 Composer 的“处理中”队列项；
- 可执行 Agent/heartbeat delivery 使用 delivery 派生的稳定 `clientRequestId/clientMessageId` 受理 Turn；只有 durable acceptance 成功才能 ACK。通知则只有在消息/事件原子落盘后才能 ACK。任一路径受理失败都必须 retry/dead-letter，不能 ACK 后丢失；
- 受理前必须剥离 sender metadata 中伪造的 `message_fabric_*`，再从已 claim 的 delivery 重建 source/reply/room/correlation 路由；若终态回复投向错误 Agent，先对比原 delivery 与 command metadata，不能信任发送方自带保留键；
- `AgentExecutionAdmissionCoordinator` 以 workspace/agent 为键；foreground demand 存续期间 recovery/idle drain 不得抢先 claim。heartbeat 在 claim 后遇 Busy、foreground demand 或 firewall denial 时 ACK/drop，不创建 Turn、不重试；
- `message.deliver` wake event 必须把 `deliveryId` 传给 `MessageClaimRequest` 做精确领取；否则用户 delivery 的事件可能领取更旧的队首后台 delivery，再次形成饥饿；
- Agent 的 canonical reply 由 `ConversationReplyProjectionWorker` 在 committed terminal event 后按原 `replyTo/room/conversation/priority` 投递；`ReplyProjectedAt` 保证一次，MessageDelivery dispatcher 不等待回复；
- Agent 消息不得再默认形成“收到即执行并回复”。`inform/report_result/agent_reply + requires_response!=true` 必须是 `handling_mode=notify`，不创建 Turn、不调用模型；`ask/request_review/delegate` 或显式 `requires_response=true` 才是 `execute`。未知旧 intent 可以执行但不得自动回复；
- `notify` drain 按 workspace/Agent 跨 room 一次最多原子领取 20 条，并逐条写 `ChatMessage + message.created`、逐条 ACK。目标 Agent Busy 时仍应排空；如果这些通知还停在 `queued`，优先查 Hosted Service/DI/schema，而不是 availability；
- 合并领取不等于合并 Prompt。可执行 delivery 仍是一条一个 Turn；通知也必须保留独立 messageId、correlation/causation、卡片与失败状态；
- SubAgent continuation 等仍保留的长执行路径必须每 2 分钟续 5 分钟租约；`RenewLeaseAsync=false` 后旧执行必须取消且不得 ACK/retry/dead-letter/reply，非空 `executionId` 必须与 `ClaimedByExecutionId` 完全相等。

关键日志：

```text
[TurnExecutorAdapter] Waiting for foreground admission
[MessageDeliveryDispatcher] Message Fabric delivery handed off to canonical Turn
[MessageFabric] Canonical Turn reply projected
[MessageFabric] Passive notification accepted
[MessageDeliveryDispatcher] Passive notification batch drained
[MessageDeliveryDispatcher] Heartbeat skipped because foreground work is pending
[MessageDeliveryDispatcher] Delivery lease ownership lost; cancelling stale runtime execution
[MessageDeliveryDispatcher] Discarded stale execution before delivery mutation
```

若出现“B 每完成一条就新增 2~3 条”或 A/B 乒乓，先做只读聚合（不要直接更新运行库）：

```sql
SELECT d.handling_mode,
       json_extract(m.metadata_json, '$.intent') AS intent,
       json_extract(m.metadata_json, '$.requires_response') AS requires_response,
       d.status,
       COUNT(*) AS n
FROM message_deliveries d
JOIN room_messages m ON m.message_id = d.message_id
WHERE d.target_kind = 'agent' AND d.target_id = $agentId
GROUP BY d.handling_mode, intent, requires_response, d.status
ORDER BY n DESC;
```

异常特征是 `intent=inform/agent_reply` 却为 `handling_mode=execute`，或 command 的
`message_fabric_reply_expected=true` 与消息合同不符。新版本启动时 schema bootstrap 会把历史普通
`inform/report_result/agent_reply` 回填为 `notify`；只有外部重启到新构建后才能验证该回填和 drain，编译通过不等于线上队列已恢复。

若前端仍显示“处理中”但时间线没有卡片，先调用队列 API 的默认查询和 `includeTerminal=true` 诊断查询做差集：默认结果中出现 `message_deliveries=delivering` 或 `chat_execution_commands=running` 是投影回归；只在诊断结果出现则队列正确。随后核对 `message_fabric_delivery_id` 对应 command 是否存在、`ChatMessages.MessageId` 是否与稳定 clientMessageId 一致，以及 SSE 是否回放该 command 的 canonical events。不要通过前端本地状态伪造消息卡或处理卡。

定向回归：

```powershell
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore --nologo `
  --filter "FullyQualifiedName~MessageDeliveryDispatcherTests|FullyQualifiedName~TurnExecutorAdapterTests"
dotnet test .\Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --nologo `
  --filter "FullyQualifiedName~MessageFabricStoreTests|FullyQualifiedName~MessageQueueProjectionServiceTests|FullyQualifiedName~ConversationReplyProjectionWorkerTests|FullyQualifiedName~SubmitTurnHandlerTests"
```

## 11.17 Desktop 重启 Core 固定 60 秒失败，SessionChunk 回填反复从头开始

若 `desktop/bootstrap/start` 的结果显示 `buildExitCode=0`，但约 60 秒后仍为
`coreRestarted=false`，同时每次手工启动都能看到 `[SessionChunkBackfill] start`、若干
`progress`，却永远没有 `completed`，先检查 Hosted Service 是否阻塞宿主启动：

- DesktopChild 只有在 `await app.StartAsync()` 返回后才输出 `PUDDING_DESKTOP_READY`；
- `IHostedService.StartAsync` 若直接等待完整历史扫描，Ready 信号会被回填时长阻塞；
- 旧版 Desktop 的 Core Supervisor 到达固定 `startupTimeoutSeconds` 后会终止该子进程，所以下次启动
  又从 `lastId=0` 扫描，看起来像回填卡死或反复重启；新版启动租约只保护 Ready 前必须完成的
  有限初始化，后台历史回填仍不得依赖租约占用启动窗口。

修复边界是让一次性长回填继承 `BackgroundService`，在 `ExecuteAsync` 首先异步让出执行，
并用 stopping token 支持宿主退出；不能靠单纯增大 Desktop 启动超时掩盖。验收必须同时满足：

1. `/desktop/bootstrap/status` 在超时前进入 `coreState=Ready`；
2. Core PID 跨过原来的启动超时仍存活；
3. 日志最终出现 `[SessionChunkBackfill] completed`；
4. `SessionChunkVectors` 计数持续增长且重启幂等跳过已有 `MessageId`。

定向回归：

```powershell
dotnet test .\Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore --nologo `
  --filter "FullyQualifiedName~SessionChunkBackfillServiceTests"
```

