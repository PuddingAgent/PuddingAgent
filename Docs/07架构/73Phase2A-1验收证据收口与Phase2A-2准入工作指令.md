# 73 Phase 2A-1 验收证据收口与 Phase 2A-2 准入工作指令

> - 状态：**completed / Phase 2A-1 accepted（2026-08-02）**
> - 日期：2026-08-02
> - 执行者：Pudding 自身 Agent
> - 前置文档：[72 最终验收修复](72Phase2A-1最终验收修复Bridge握手Surface切换与UISmoke工作指令.md)
> - 本批次性质：Phase 2A-1 最后一个收口批次，不增加 Phase 2A-2 功能

## 验收结果

Phase 2A-1 已完成并通过准入验收。本文后续“当前缺口”和执行指令保留为验收前历史快照，不能再用来判断当前源码状态。

| 证据 | 最终结果 |
|---|---|
| Host Bridge 本地集成测试 | 43/43 passed |
| Desktop Browser/Client/配置测试 | 92/92 passed |
| Release publish | `.tmp-build/phase2a1-final-preview` 成功，包含 Desktop、`core/PuddingAgent.exe`、`core/wwwroot/admin/index.html` |
| 可见 Desktop | PID 40588，来自验收发布目录；退出码 0 |
| Core 启动/重启 | PID 46272 → 45380；动态端口 8959 → 10398 |
| Bridge | Browser 页面启动 Core 后进入 Connected；重启后自动回到 Connected |
| Browser | Core 未启动时创建真实 Page；双标签分别加载 `example.com` / `example.org`，切换只有 active Surface 可见 |
| Agent target | 指定第二标签后切回第一标签，target 不变化 |
| Workbench | 只在用户进入 Workbench 时初始化，随后进入 Workbench Ready；不再阻塞 Core Ready |
| Stop / Exit | Stop 后 Browser Context/Page 保留；退出后 Desktop/Core 子进程全部回收 |
| 隔离目录 | `%TEMP%\PuddingAgent\phase2a1-browser-09d488f8542949d5ab86472806122411`，Workbench 与 Agent Browser UDF 分离，未触碰 `D:\data` |

Activity 的开始/完成、错误码和 100 条上限由确定性 Dispatcher/Controller 测试覆盖。Phase 2A-1 明确不包含 Remote Browser/Agent Tools，因此本轮可见 UI 没有伪造一个产品外“Agent 发命令”入口；真实 Agent Tool 驱动 Activity 的窗口验收进入 Phase 2A-2 首批工作。

验收期间额外关闭了三个仅在真实发布窗口暴露的问题：Brush 属性误绑定 Color 资源、`desktop.json` 的字符串关闭行为无法反序列化、Collapsed WebView2 初始化挂起；同时把 Workbench 改为可见时按需初始化，并为退出阶段 Browser 释放增加有界等待。

## 0. 可直接发送给 Pudding Agent 的指令

```text
请完整执行 Docs/07架构/73Phase2A-1验收证据收口与Phase2A-2准入工作指令.md。

当前源码已经修复 Desktop HelloAck 接收顺序、独立 heartbeat watchdog、连接 generation 隔离、Host 可取消 Receive 以及 Controller 的 Surface/AgentTarget 主体逻辑；不要重写这些已完成部分。当前重新验证结果为：Protocol、WebView2、Host、Desktop build 全部 0 error；Host 29/29、Desktop 74/74 passed；git diff --check passed。

但 Phase 2A-1 仍未达到 accepted：BrowserWorkspaceView 仍以 DataContext=this 绑定不存在的 Tabs，ActivityList 没有数据源，已有 BrowserWorkspaceViewModel 未接入；Browser Workspace 仍只在 CoreReady 初始化；没有 DesktopBrowserBridgeClientTests，也没有 Host 认证/握手/心跳/断线 Endpoint 测试；没有 TestScripts/start-phase2a1-browser-smoke.ps1、Release publish 或新版可见 UI smoke。当前运行的仍是旧 .tmp-build/phase1b-runtime-preview，不是验收包。

先冻结 dirty worktree，只完成本文列出的剩余项。不得进入 BrowserWindow、RemoteBrowserRuntime、Agent Tools、DOM/Input/CDP/Network、Screenshot/PDF、Douyin Adapter；不得触碰 D:\data、回滚无关改动或静默结束用户正在运行的旧 Desktop。完成全部自动化验证和可见 UI smoke 后，再更新文档为 Phase 2A-1 accepted，并报告是否满足 Phase 2A-2 准入条件。
```

## 1. 本轮复核事实

2026-08-02 已重新执行：

| 验证项 | 当前结果 |
|---|---|
| `PuddingBrowser.Protocol` build | 0 warning / 0 error |
| `PuddingBrowser.WebView2` build | 0 warning / 0 error |
| `PuddingHost` build | 0 error；保留既有 NU1903/NU1904 |
| `PuddingDesktop` build | 0 error；保留既有 NU1903 |
| `PuddingHost.Tests` | 29/29 passed |
| `PuddingDesktop.Tests` | 74/74 passed |
| `git diff --check` | passed；只有行尾转换提示 |

已确认源码存在：

- Desktop 在发送 Hello 前已经启动同一个 Receive Loop；
- Desktop/Host 均有独立 watchdog，并能取消阻塞的 Receive；
- Desktop 使用 per-connection session 与 generation；
- Host 的 `NextGeneration()` 已进入 Registry 接口；
- Controller 已有 `ActivateAsync`、`AssignAgentTargetAsync` 和 UI dispatcher 主体逻辑。

不要为这些已完成项再做大规模重构。本批次只补剩余闭环和证明。

## 2. 当前仍阻断验收的事实

### 2.1 UI 数据源仍未接通

当前 `BrowserWorkspaceView` 构造函数仍执行：

```csharp
DataContext = this;
```

但 XAML 使用 `{Binding Tabs}`，View 本身没有 `Tabs`；`ActivityList` 也没有 `ItemsSource`。仓库中的 `BrowserWorkspaceViewModel` 未被 View 或 Controller 使用，同时 Controller 又维护另一套 `Tabs`/`Activities`。这会造成可编译但真实 UI 不显示数据。

### 2.2 Browser 初始化仍依赖 CoreReady

`MainWindow.OnCoordinatorStateChanged` 仍只在 `CoreReady` 分支调用 `InitializeBrowserWorkspaceAsync()`。当 DataRoot 已配置而 Core AutoStart=false、Core 启动失败或 Core 停止时，本地 Agent Browser 不能按设计独立初始化。

### 2.3 新 Bridge 可靠性代码没有对应测试

当前 Browser 定向测试仍只有：

```text
Tests/PuddingHost.Tests/BrowserBridge/DesktopBrowserCommandBrokerTests.cs
Tests/PuddingDesktop.Tests/Browser/BrowserWorkspaceControllerTests.cs
```

没有直接测试 `DesktopBrowserBridgeClient` 的 Hello/同一 Receive Loop/watchdog/generation/reconnect，也没有通过 TestServer 测试 Host Endpoint 的 Token、Loopback、Hello、heartbeat 和断线。

### 2.4 没有新版产品运行证据

当前运行进程来自：

```text
.tmp-build/phase1b-runtime-preview/PuddingDesktop.exe
.tmp-build/phase1b-runtime-preview/core/PuddingAgent.exe
```

仓库中不存在 `TestScripts/start-phase2a1-browser-smoke.ps1` 和 `.tmp-build/phase2a1-final-preview`。旧窗口不能作为 Phase 2A-1 验收证据。

## 3. 修改边界

允许修改：

```text
Source/PuddingDesktop/Browser/**
Source/PuddingDesktop/Views/BrowserWorkspaceView.xaml
Source/PuddingDesktop/Views/BrowserWorkspaceView.xaml.cs
Source/PuddingDesktop/MainWindow.xaml.cs
Source/PuddingDesktop/Hosting/DesktopApplicationCoordinator.cs
Source/PuddingHost/BrowserBridge/**
Tests/PuddingDesktop.Tests/Browser/**
Tests/PuddingHost.Tests/BrowserBridge/**
Tests/PuddingDesktop.Tests/PuddingDesktop.Tests.csproj（仅测试依赖确有需要时）
Tests/PuddingHost.Tests/PuddingHost.Tests.csproj（仅 TestHost 依赖确有需要时）
TestScripts/start-phase2a1-browser-smoke.ps1
Agents.md
How-Debuge.md
Source/code_map.md
Docs/README.md
Docs/07架构/README.md
Docs/07架构/68、69、70、71、72、73
```

禁止修改：

- `dev-up.py` 及其产品化边界；
- BrowserWindow、RemoteBrowserRuntime/Context/Page、Agent Tool；
- DOM/Input/CDP/Network、下载上传、Screenshot/PDF；
- Douyin URL、选择器、登录、评论或回复实现；
- `D:\data` 中任何用户数据；
- 无关 Feishu、Chat、Memory、Runtime、Storage、Workbench 和外部子模块改动。

## 4. Task 1：收敛唯一 Browser Workspace UI 状态

不要继续保留“未使用的 `BrowserWorkspaceViewModel` + Controller 自己持有集合 + View code-behind 手工控件状态”三套来源。以一个对象作为 UI 唯一事实源。

推荐做法：保留 `BrowserWorkspaceViewModel`，由 Controller 构造注入并负责更新；View 初始化完成后只将它设为 `DataContext`。

```csharp
public sealed class BrowserWorkspaceViewModel : INotifyPropertyChanged
{
    public ObservableCollection<BrowserTabViewModel> Tabs { get; }
    public ObservableCollection<AgentBrowserActivityItem> Activities { get; }
    public PageId? ActivePageId { get; internal set; }
    public PageId? AgentTargetPageId { get; internal set; }
    public BrowserTabViewModel? ActiveTab { get; }
    public BrowserBridgeConnectionState BridgeState { get; internal set; }
    public AgentBrowserControlState ControlState { get; internal set; }
    public bool HasTabs { get; }
    public bool CanGoBack { get; }
    public bool CanGoForward { get; }
    public bool IsLoading { get; }
}

public BrowserWorkspaceController(
    IBrowserRuntime runtime,
    IBrowserSurfaceHost surfaceHost,
    IWebView2UiDispatcher uiDispatcher,
    BrowserWorkspaceViewModel viewModel);
```

必须完成：

1. XAML 的 TabStrip 绑定 `Tabs`，ActivityList 绑定 `Activities`；
2. active tab、agent target、loading、back/forward、bridge/control 状态都从同一 ViewModel 读取；
3. `BrowserBridgeCommandDispatcher` 增加 `ActivityChanged`，只投影安全摘要，不显示 Token、Cookie、表单值、完整参数或脚本；
4. Activity 最多保留 100 条，开始和完成状态都能通知 UI；
5. 所有集合写入仍通过 `IWebView2UiDispatcher`；
6. 删除未使用或重复的状态类型，不增加同步复制计时器。

如果选择让 Controller 本身成为唯一 ViewModel，也必须删除未使用的 `BrowserWorkspaceViewModel`，并满足同样的数据绑定和线程边界。不能继续保留两个事实源。

## 5. Task 2：让 Browser 在 DataRoot Ready 后独立初始化

增加一个可等待、可失败、可重试的明确入口：

```csharp
internal async Task InitializeBrowserWorkspaceAsync(
    string dataRoot,
    CancellationToken cancellationToken);
```

要求：

1. Coordinator 成功读取 `desktop.json`、验证 DataRoot 并读取 `system.json` 后，通过 WPF Dispatcher 调用该入口；
2. 初始化发生在 Core AutoStart 判断和 CoreReady 之前；
3. Core Failed/Stopped/Restarting 只断开 Bridge，不销毁 Browser Context、Page、Tab 或 UDF；
4. View 的 `InitializeAsync` 不得捕获异常后假装成功。失败时完整释放半初始化 Runtime、Surface、事件订阅和 handler，再把失败返回 MainWindow；
5. `_browserInitialized` 只能在成功后设为 true；
6. 页面提供可见的“重试初始化”入口；重试不得创建重复 Context、重复订阅或重叠 WebView2 Surface；
7. Desktop lifetime token 负责初始化、连接与释放，不新增无所有权的 fire-and-forget 生命周期。

## 6. Task 3：补齐阻断性自动化测试

### 6.1 Desktop Client

新增：

```text
Tests/PuddingDesktop.Tests/Browser/DesktopBrowserBridgeClientTests.cs
```

使用现有 fake transport/clock seam，禁止真实公网和真实等待 45 秒。至少覆盖：

- 等待 HelloAck 时 Receive Loop 已经运行；
- accepted 后才进入 Connected，rejected/timeout 不得进入 Connected；
- HelloAck 前后始终只有一个 Receive Loop；
- Command -> Dispatcher -> CommandResult correlation；
- Heartbeat 与 HeartbeatAck；
- fake clock 推进超时后取消静默且阻塞的 Receive；
- Send/Receive 同时失败只完成一次且只产生一个 reconnect task；
- generation N 的晚到事件不能改变 N+1；
- Disconnect 取消 reconnect，下一次 Connect 建立全新 session；
- control token 只进入 Header，不进入状态、异常文本或测试日志。

### 6.2 Host Endpoint

新增：

```text
Tests/PuddingHost.Tests/BrowserBridge/DesktopBrowserBridgeAuthenticationTests.cs
Tests/PuddingHost.Tests/BrowserBridge/DesktopBrowserBridgeHandshakeTests.cs
Tests/PuddingHost.Tests/BrowserBridge/DesktopBrowserBridgeHeartbeatTests.cs
Tests/PuddingHost.Tests/BrowserBridge/DesktopBrowserBridgeDisconnectTests.cs
```

使用本地 `TestServer`/TestHost 和 fake clock。至少覆盖：

- Console 模式不映射，DesktopChild 模式才映射；
- 无 Token/错误 Token、非 Loopback、非 WebSocket；
- 首消息非 Hello、协议不匹配、Hello 超时；
- accepted 前 Broker 不可用，accepted 后可用；
- 第二连接不能替换 AwaitingHello 并留下僵尸 Receive；
- fake clock 推进 timeout 后可取消阻塞 Receive 并 Detach；
- 旧 generation finally 不得失败新 generation pending；
- 断线后的旧命令不得在新连接重放。

### 6.3 Workspace 状态

扩充 `BrowserWorkspaceControllerTests.cs`，至少新增：

- 创建两个 Page 时 Surface 创建两次、只激活目标 Surface；
- 切换可见 Tab 不改变 AgentTargetPageId；
- Assign target 后无 PageId 命令使用 target；
- 关闭 target 后返回 page_not_found，不回退 active tab；
- UI 集合只经 fake UI dispatcher 写入；
- Back/Forward/Reload/Stop 后 ViewModel 同步；
- Activity start/complete 投影且最多 100 条；
- ClearHandler 后返回 browser_not_available；
- 初始化失败清理后可重试。

测试必须先证明旧行为会失败，再由实现修复使其通过；不得只断言注释、私有字段或固定延迟。

## 7. Task 4：Release publish 与新版 UI smoke

先串行执行 restore/build/test。不得使用并行 Desktop build，不得将输出写入 DataRoot：

```powershell
dotnet restore Tests\PuddingHost.Tests\PuddingHost.Tests.csproj
dotnet restore Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj

dotnet build Source\PuddingBrowser.Protocol\PuddingBrowser.Protocol.csproj --no-restore --nologo
dotnet build Source\PuddingBrowser.WebView2\PuddingBrowser.WebView2.csproj --no-restore --nologo
dotnet build Source\PuddingHost\PuddingHost.csproj --no-restore --nologo
dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo

dotnet test Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo

dotnet publish Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release `
  -o .tmp-build\phase2a1-final-preview `
  --nologo

git diff --check
```

新增 `TestScripts/start-phase2a1-browser-smoke.ps1`：

- 默认使用 `$env:TEMP\PuddingAgent\phase2a1-browser-<guid>` 作为 DesktopHome/DataRoot；
- 接受 publish 目录参数；
- 不默认访问、复制或清理 `D:\data`；
- 输出 Desktop PID、Core PID、动态 Loopback 地址、DataRoot、Workbench UDF、Agent Browser UDF 和日志位置；
- 脚本退出清理自己创建的进程和临时目录；可通过显式 `-KeepArtifacts` 保留证据。

当前旧 Desktop 仍在运行，单实例会阻止新版 UI smoke。执行 Agent 不得 `Stop-Process` 或静默强杀；必须先报告旧 PID/路径，请用户从托盘执行“退出 Pudding”，确认旧 Desktop/Core 已退出后再启动新 publish 包。

可见 smoke 必须逐项记录：

1. 新版窗口确实来自 `.tmp-build/phase2a1-final-preview`；
2. DataRoot Ready 且 Core 未启动时，Agent Browser 页面可以初始化并创建 Page；
3. 创建两个 Tab，分别导航不同 URL，切换时只有对应 Surface 可见；
4. 将 Tab A 指定为 Agent target 后，用户切到 Tab B，target 仍是 A；
5. Bridge 状态从 Connecting 到 Connected；
6. 通过 Core Broker 发出至少一个 `page.goto`，Activity Pane 显示开始和成功/失败终态；
7. Restart Core 后 Bridge 自动重连，Tab/Context 不丢失，旧 pending 不重放；
8. Stop Core 后本地 Browser 仍可操作；
9. 明确退出 Desktop 后 Core 与 WebView2 子进程退出，动态端口释放；
10. Workbench UDF 与 Agent Browser UDF 完全隔离。

如果当前执行环境不能完成可见窗口操作，只能报告“代码与自动化测试通过，Phase 2A-1 仍未验收”，不得修改 completed 状态。

## 8. Task 5：只在验收通过后更新状态

全部 DoD 满足后才更新：

```text
Agents.md
How-Debuge.md
Source/code_map.md
Docs/README.md
Docs/07架构/README.md
Docs/07架构/68、69、70、71、72、73
```

统一状态应为：

```text
Phase 2A-1 accepted（2026-08-02）
下一批次允许规划 Phase 2A-2，但尚未实现。
```

`How-Debuge.md` 至少记录：Bridge 状态、握手失败、认证失败、watchdog timeout、generation、Core Restart、Surface/Tab 不一致时的日志位置与排查顺序。

## 9. Definition of Done

- [x] UI 只有一个 Browser Workspace 状态源，Tab 和 Activity 绑定到同一 Controller。
- [x] active tab 与 agent target 是两个独立状态，切换 Tab 不改变 target。
- [x] 创建/切换/关闭 Page 会真实切换 Surface，只有 active Surface 可见。
- [x] Browser 在 DataRoot Ready 后初始化，不依赖 CoreReady；失败可清理并重试。
- [x] Desktop Client 的 Hello、watchdog、generation、reconnect 有确定性测试。
- [x] Host Endpoint 的认证、握手、heartbeat、断线有本地集成测试。
- [x] Workspace 的 Surface、target、UI dispatcher、activity 和重试测试通过。
- [x] Protocol/WebView2/Host/Desktop build 全部 0 error。
- [x] Host/Desktop 定向测试全部通过：43/43、92/92。
- [x] Release publish 包含 Desktop、Core 与 Workbench 静态资源。
- [x] 新版可见 UI smoke 覆盖双 Tab、真实 Bridge、Restart、Stop 与 Exit；Activity 的命令投影由自动化覆盖，真实 Agent Tool 驱动进入 Phase 2A-2。
- [x] smoke 全程使用系统 Temp 隔离数据，未触碰 `D:\data`。
- [x] 旧 Desktop 由用户明确退出；未修改或回滚无关 dirty files。
- [x] 文档、Agents 和 code_map 状态一致。

只有上述全部满足，才能创建 Phase 2A-2 工作指令。Phase 2A-2 的首批范围应另行评审，不能偷偷混入本批次。

## 10. 完成报告格式

最终报告必须包含：

1. 初始 dirty worktree 与实际修改文件边界；
2. UI 单一状态源和 DataRoot Ready 初始化的最终数据流；
3. 新增测试文件、测试名称、用例数与结果；
4. build/test/publish 每条命令的结果；
5. smoke 的 PID、动态端口、临时 DataRoot/UDF 和十项结果；
6. 既有 NU1903/NU1904 等保留警告；
7. 未执行项或真实阻塞；
8. 明确结论：`Phase 2A-1 accepted` 或 `Phase 2A-1 not accepted`。

只报告“代码完成”“29/29、74/74 通过”或“UI 留待手工验证”不算验收完成。

## 11. 后续状态（2026-08-02）

Phase 2A-2 最小 Remote Browser Runtime/Context/Page 与 `browser_context`、`browser_tabs`、`browser_navigate` 已实现并完成自动化、发布和可见 Desktop smoke 验收。实现边界、类签名、测试证据和下一批 Phase 2A-3 指令见 [74](74Phase2A-2最小RemoteBrowser与AgentTools实施验收报告.md)。
