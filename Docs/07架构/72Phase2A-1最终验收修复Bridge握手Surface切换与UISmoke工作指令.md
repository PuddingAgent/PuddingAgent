# 72 Phase 2A-1 最终验收修复：Bridge 握手、Surface 切换与 UI Smoke 工作指令

> - 状态：**completed / accepted（2026-08-02，经 73 最终验收）**
> - 日期：2026-08-02
> - 执行者：Pudding 自身 Agent
> - 前置文档：[70 初始工作包](70Phase2A-1通用BrowserBridge与双标签工作区开发工作指令.md)、[71 Acceptance Patch](71Phase2A-1验收补丁真实BrowserWorkspace与Bridge可靠性工作指令.md)
> - 本批次目标：只关闭 Phase 2A-1 已确认的最终阻断项；验收通过前不得进入 Phase 2A-2

> 2026-08-02 最终说明：本文列出的阻断项已关闭。最终验收为 Host 43/43、Desktop 92/92、Release publish、双标签/Agent target/Core restart/Bridge reconnect/Workbench 按需初始化/Stop/Exit 可见 smoke 通过，详见 73。

## 0. 可直接发送给 Pudding Agent 的指令

```text
请完整执行 Docs/07架构/72Phase2A-1最终验收修复Bridge握手Surface切换与UISmoke工作指令.md。

这是 Phase 2A-1 最终验收修复，不是新功能批次。当前四个定向项目可以编译，Host 29/29、Desktop 74/74 测试通过，但产品仍存在会阻止真实运行的缺陷：Desktop 在启动 Receive Loop 之前等待 HelloAck，Bridge 必然超时；Core/Desktop 的 45 秒超时都会被阻塞的 ReceiveAsync 绕过；Tab/Activity 绑定没有真实数据源；切换 Tab 没有调用 SurfaceHost.ActivateAsync；AgentTargetPageId 从未赋值；Controller 可能从 WebSocket 后台线程修改 ObservableCollection；Browser Workspace 只在 CoreReady 初始化；计划中的认证、握手、心跳、断线和 Desktop Client 测试仍缺失；当前运行窗口还是旧 phase1b-runtime-preview，不是新版验收包。

请先冻结 dirty worktree，然后按文档修复这些根因，补齐测试，串行 build/test/publish，并在系统 Temp 隔离 DataRoot 下完成真实双标签、Bridge 重连、Core Stop 和退出 smoke。不要只增加绕过测试，不要把等待时间缩短后宣称成功，不要进入 BrowserWindow、RemoteBrowserRuntime、Agent Tools、DOM/CDP 或 Douyin Adapter。

不得 reset/checkout 无关改动，不得触碰 D:\data，不得静默终止用户当前运行的 PuddingDesktop。若单实例阻止新版 smoke，先报告旧进程并请求用户正常退出，再运行隔离 smoke。所有 Definition of Done 满足后才更新 68/69/70/71/72、Agents.md、How-Debuge.md、README 和 code_map，并声明 Phase 2A-1 accepted。
```

## 1. 当前验证事实

2026-08-02 已重新执行：

| 项目 | 结果 |
|---|---|
| `PuddingBrowser.Protocol` | build 0 warning / 0 error |
| `PuddingBrowser.WebView2` | build 0 warning / 0 error |
| `PuddingHost` | build 0 error，保留既有 NU1903/NU1904 |
| `PuddingDesktop` | build 0 error，保留既有 NU1903 |
| `PuddingHost.Tests` | 29/29 passed |
| `PuddingDesktop.Tests` | 74/74 passed |
| `git diff --check` | passed |

新增代码比 71 执行前明显完整，但上述测试没有覆盖实际 Bridge 连接和真实 WPF Surface 数据流。因此不得根据数量把 Phase 2A-1 标记为完成。

当前机器仍运行旧包：

```text
.tmp-build/phase1b-runtime-preview/PuddingDesktop.exe
.tmp-build/phase1b-runtime-preview/core/PuddingAgent.exe
```

它不是 Phase 2A-1 smoke 证据，也不得由开发 Agent 静默强杀。

## 2. 已确认的最终阻断项

### 2.1 Desktop HelloAck 必然超时

当前 `DesktopBrowserBridgeClient.ConnectInternalAsync` 顺序是：

```text
start SendLoop
enqueue Hello
await HelloAck TCS
收到 accepted 后才 start ReceiveLoop
```

但只有 Receive Loop 会读取 HelloAck，所以 TCS 没有完成者，连接会在 5 秒后失败并不断重连。

### 2.2 45 秒失联判断不会唤醒

Core 和 Desktop 都在进入 `ReceiveAsync` 前检查 `LastReceivedAt`。如果对端静默而 Socket 没有关闭，`ReceiveAsync` 可以无限阻塞，循环无法再次检查时间。因此当前 45 秒 timeout 只是注释，不是可运行语义。

### 2.3 WebSocket test seam 不可替换

`IDesktopBrowserWebSocketFactory.Create()` 返回具体 `ClientWebSocket`。测试无法提供确定性的 fake Connect/Receive/Send/Close 行为，导致 `DesktopBrowserBridgeClientTests` 至今不存在。

### 2.4 Host connection 生命周期仍有空洞

- Endpoint 通过 `registry as DesktopBrowserConnectionRegistry` 获取 generation；接口替身会得到 generation 0。
- Registry 允许新连接替换“尚未 Hello”的旧连接，但 `DesktopBrowserConnection.Complete()` 没有取消旧 Endpoint 使用的 linked CTS，旧 Receive 仍可能存活。
- Host heartbeat 使用真实 `Task.Delay`，没有可推进的 Clock/TimeProvider，因此 45 秒行为无法快速测试。

### 2.5 Tab Strip 和 Activity Pane 没有数据源

`BrowserWorkspaceView` 设置 `DataContext=this`，XAML 却绑定 `{Binding Tabs}`；View 没有 `Tabs` 属性。`ActivityList` 也没有 `ItemsSource`，Dispatcher 没有 ActivityChanged 通知。真实创建 Page 后 Tab 和活动记录不会可靠显示。

### 2.6 Page 切换没有切换 Surface

`BrowserWorkspaceController.ActivateAsync` 只修改 `ActivePageId`，没有调用 `IBrowserSurfaceHost.ActivateAsync`。新建 Surface 默认都是 Visible，多个 WebView2CompositionControl 会叠加在同一容器中。

### 2.7 Agent 目标页语义未实现

`_agentTargetPageId` 只在关闭 Page 时被清空，从未赋值。UI 的“将此页交给 Agent”只退出 UserTakeover；Bridge 命令缺少 PageId 时仍回退到当前可见页。用户切换 Tab 会隐式改变 Agent 操作目标。

### 2.8 Controller 的 UI 线程和串行边界不完整

- Bridge Dispatcher 在 WebSocket Receive 后台线程调用 Controller。
- Controller 会直接修改 `ObservableCollection` 和 `BrowserTabViewModel`。
- `NavigateAsync`、Back、Forward、Reload、Stop 没有使用 Controller gate。
- Back/Forward/Reload 完成后没有同步 Tab Url、Title、Loading、CanGoBack/CanGoForward。

这可能造成 WPF 跨线程异常、关闭与导航竞态或 UI 状态长期不更新。

### 2.9 初始化与重试时机错误

Browser Workspace 只在 `CoreReady` 时初始化。DataRoot 已配置但 Core AutoStart=false 时，用户进入 Agent Browser 得到未初始化页面，与“Core 失败不阻塞本地 Browser”决策冲突。View 捕获初始化异常但不抛出，MainWindow 随后仍把 `_browserInitialized=true`，导致无法重试。

### 2.10 测试、发布和 smoke 未完成

当前只新增：

```text
Tests/PuddingHost.Tests/BrowserBridge/DesktopBrowserCommandBrokerTests.cs
Tests/PuddingDesktop.Tests/Browser/BrowserWorkspaceControllerTests.cs
```

缺少认证、握手、心跳、断线、Client 状态机和真实 UI 数据流测试；没有 Phase 2A-1 Release publish 和新版可见 UI smoke。

## 3. 文件边界

### 3.1 允许修改

```text
Source/PuddingBrowser.Protocol/**（仅必要协议/测试 seam）
Source/PuddingBrowser.WebView2/**
Source/PuddingDesktop/Browser/**
Source/PuddingDesktop/Views/BrowserWorkspaceView.xaml(.cs)
Source/PuddingDesktop/Hosting/DesktopApplicationCoordinator.cs
Source/PuddingDesktop/MainWindow.xaml.cs
Source/PuddingHost/BrowserBridge/**
Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.BrowserBridge.cs
Tests/PuddingHost.Tests/BrowserBridge/**
Tests/PuddingHost.Tests/PuddingHost.Tests.csproj
Tests/PuddingDesktop.Tests/Browser/**
TestScripts/start-phase2a1-browser-smoke.ps1（需要时新增）
Agents.md
How-Debuge.md
Source/code_map.md
Docs/README.md
Docs/07架构/README.md
Docs/07架构/68、69、70、71、72
```

### 3.2 禁止扩大范围

- 不实现独立 `BrowserWindow` 或 Surface 跨窗口转移。
- 不实现 Core `RemoteBrowserRuntime/Context/Page` 或 Agent Browser Tools。
- 不实现 DOM/Input/CDP/Network/Screenshot/PDF/Download。
- 不增加 Douyin URL、选择器、登录、评论或回复代码。
- 不修改 `dev-up.py`，不删除开发脚本。
- 不清理或写入 `D:\data`。
- 不修改、回滚或格式化无关 Feishu、RuntimeTests、Storage、Workbench 和外部子模块文件。

## 4. Task 1：重构 Desktop Bridge 为可测试的 Connection Session

### 4.1 先修复 Hello 顺序

同一个 Receive Loop 必须在 Hello 发出前后持续存在：

```text
create connection session
start SendLoop
start ReceiveLoop
enqueue Hello
await HelloAck TCS (5s)
accepted -> Connected -> start Heartbeat/Watchdog
rejected/timeout -> complete this session once
```

禁止在 HelloAck 后再创建第二个 Receive Loop。Hello 阶段收到 Command/Cancel 等非 HelloAck 消息应作为协议错误关闭。

### 4.2 替换不可测试的 factory

不要让 factory 返回具体 `ClientWebSocket`。新增可 fake 的 transport：

```csharp
public interface IDesktopBrowserWebSocket : IAsyncDisposable
{
    WebSocketState State { get; }
    void SetRequestHeader(string name, string value);

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);

    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);

    Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken);
}

public interface IDesktopBrowserWebSocketFactory
{
    IDesktopBrowserWebSocket Create();
}
```

生产实现只包装 `ClientWebSocket`。测试 fake 可脚本化 HelloAck、Command、Close、静默和 Send 记录。Token 只能传给 `SetRequestHeader`，fake 日志也不得回显。

### 4.3 使用每连接对象，避免共享字段覆盖

将 Socket、CTS、Channel、HelloAck TCS、Send/Receive/Heartbeat/Watchdog tasks、generation、lastReceived 封装为一个 `DesktopBrowserClientConnection`。旧 generation 完成时只能清理自身，不能把新 session 的字段置空或改变新状态。

```csharp
private sealed class DesktopBrowserClientConnection : IAsyncDisposable
{
    public long Generation { get; }
    public IDesktopBrowserWebSocket Socket { get; }
    public CancellationTokenSource Lifetime { get; }
    public Channel<BrowserBridgeEnvelope> Outbound { get; }
    public TaskCompletionSource<BrowserBridgeHelloAck> HelloAck { get; }
    public DateTimeOffset LastReceivedAt { get; set; }
}
```

所有退出路径走一个原子 `CompleteConnectionOnceAsync(session, reason)`；完整 await 自身任务并 dispose Socket。一个实例最多一个 reconnect task。

### 4.4 独立 Watchdog

Receive Loop 只收消息，Watchdog 独立检查时间：

```csharp
private Task WatchdogLoopAsync(
    DesktopBrowserClientConnection connection,
    CancellationToken cancellationToken);
```

每次收到任何合法消息更新 `LastReceivedAt`。Watchdog 通过注入 Clock 每 1~5 秒检查一次，超过 `DefaultHeartbeatTimeout` 时取消 session，从而唤醒阻塞的 ReceiveAsync。测试使用 FakeClock 立即推进 45 秒，不真实等待。

## 5. Task 2：关闭 Core Endpoint 生命周期缺口

### 5.1 generation 属于 Registry 接口

在 `IDesktopBrowserConnectionRegistry` 增加：

```csharp
long NextGeneration();
```

Endpoint 不得向具体 Registry 强制转换。

### 5.2 未认证连接不能被静默替换

同一时刻最多存在一个 attached Socket，包括 AwaitingHello 状态。建议 `TryAttach` 在 `_current != null` 时直接拒绝，旧 Endpoint finally Detach 后才允许新连接；不要让第二连接留下第一个僵尸 Receive。

如果仍选择 replacement，必须让 `DesktopBrowserConnection.Complete()` 取消 Endpoint 实际使用的 token，并有测试证明旧 Endpoint 在 replacement 后立即结束。不得只 complete outbound Channel。

### 5.3 Host Watchdog 必须能取消 Receive

将 Endpoint linked CTS 同时链接 `context.RequestAborted` 和 `connection.ConnectionToken`。Heartbeat/Watchdog 检测超时时调用 `connection.Complete()`，使阻塞的 ReceiveAsync 收到 cancellation。

Host 使用注入的 `TimeProvider` 或 `IBrowserBridgeClock`，不要把真实 15/45 秒写死在无法测试的静态方法里。Hello、HeartbeatAck、Command 和 Cancel 仍保持单一发送者不变量。

## 6. Task 3：修复 Browser Workspace 的真实 UI 数据流

### 6.1 使用一个明确 ViewModel

不要 `DataContext=this` 后绑定不存在的 `Tabs`。推荐让 `BrowserWorkspaceViewModel` 成为唯一 UI 状态：

```csharp
public sealed class BrowserWorkspaceViewModel : INotifyPropertyChanged
{
    public ObservableCollection<BrowserTabViewModel> Tabs { get; }
    public ObservableCollection<AgentBrowserActivityItem> Activities { get; }
    public PageId? ActivePageId { get; }
    public PageId? AgentTargetPageId { get; }
    public BrowserTabViewModel? ActiveTab { get; }
    public BrowserBridgeConnectionState BridgeState { get; }
    public AgentBrowserControlState ControlState { get; }
    public bool HasTabs { get; }
    public bool CanGoBack { get; }
    public bool CanGoForward { get; }
    public bool IsLoading { get; }
}
```

View 初始化时设置一次 `DataContext=viewModel`。`TabStrip.ItemsSource` 和 `ActivityList.ItemsSource` 都通过 XAML 绑定对应 ObservableCollection，不在 code-behind 定期复制快照。

Dispatcher 增加安全的 handler 生命周期和 Activity 通知：

```csharp
public void SetHandler(IBrowserCommandHandler handler);
public void ClearHandler(IBrowserCommandHandler expectedHandler);
public event EventHandler<AgentBrowserActivityChangedEventArgs>? ActivityChanged;
```

禁止 `_dispatcher.SetHandler(null!)`。

### 6.2 Controller 注入 SurfaceHost 和 UI Dispatcher

建议构造函数：

```csharp
public BrowserWorkspaceController(
    IBrowserRuntime runtime,
    IBrowserSurfaceHost surfaceHost,
    IWebView2UiDispatcher uiDispatcher,
    BrowserWorkspaceViewModel viewModel);
```

删除 `new BrowserWorkspaceController()` + `SetRuntime(...)` 的半初始化状态。构造完成即满足依赖。

### 6.3 所有 Page 操作统一串行

Create/Activate/Close/Navigate/Back/Forward/Reload/Stop、Agent command 和用户按钮操作都经过同一个 Controller gate。任何 ObservableCollection/ViewModel 修改必须通过 UI Dispatcher；不得从 WebSocket 后台线程直接写 WPF 绑定集合。

### 6.4 Surface 激活必须真实发生

```csharp
public async Task ActivateAsync(PageId pageId, CancellationToken ct)
{
    // validate page
    await _surfaceHost.ActivateAsync(pageId, ct);
    await page.BringToFrontAsync(ct);
    // update view model on UI thread
}
```

创建第一个 Page 后立即激活。创建非 active Page 时必须保持 Collapsed。关闭 active Page 后选择相邻 Page 并调用 SurfaceHost.ActivateAsync；关闭最后一个 Page 显示空状态。

### 6.5 实现 Agent target

增加：

```csharp
public Task AssignAgentTargetAsync(PageId pageId, CancellationToken ct);
public PageId? AgentTargetPageId { get; }
```

“将此页交给 Agent”调用该函数，并在 Tab 上显示 target 标记。Bridge command 解析目标顺序固定为：

1. command 显式 PageId；
2. `AgentTargetPageId`；
3. 无目标时返回 `browser_page_not_found`。

不得回退到用户刚切换的可见 Tab。关闭 target Page 后 target 置空，Agent 后续命令明确失败，直到用户或 Core 再指定。

### 6.6 同步导航状态

Page 导航事件或每次操作完成后更新：Title、Url、IsLoading、CanGoBack、CanGoForward、PageVersion。按钮 IsEnabled 和 Reload/Stop 图标绑定这些属性。Back/Forward/Reload 不得只调用 CoreWebView2 而不刷新 UI。

Activity Pane 实时显示 Dispatcher 的最近 100 条动作、目标 Page、开始/完成、成功/失败、错误码和耗时；不得显示完整 Arguments、表单值、Token、Cookie 或脚本文本。

## 7. Task 4：修复初始化和 Coordinator 生命周期

Browser Workspace 应在“DataRoot 已成功加载且 Token 可用”后初始化，而不是等待 CoreReady。增加明确通知或方法：

```csharp
internal Task InitializeBrowserWorkspaceAsync(
    string dataRoot,
    CancellationToken cancellationToken);
```

Coordinator 完成 DataRoot/system.json 验证后在 WPF Dispatcher 调用它；Core AutoStart=false、Core Failed 和 Core Stopped 都不影响本地 Browser Context/Page。

初始化失败必须返回失败或抛给 MainWindow，使 `_browserInitialized` 保持 false；页面显示“重试初始化”按钮。重试前完整清理半初始化 Runtime/Surface/订阅。

Coordinator Bridge 调用必须使用 Desktop lifetime token，并串行化 Ready/Stopping/RestartScheduled 事件；不要使用无所有权的 `CancellationToken.None` fire-and-forget Connect/Disconnect。Core Stop 后 desiredConnected=false；新 Core Ready 后才连接新地址。

## 8. Task 5：补齐阻断性测试

### 8.1 Host

新增：

```text
Tests/PuddingHost.Tests/BrowserBridge/
  DesktopBrowserBridgeAuthenticationTests.cs
  DesktopBrowserBridgeHandshakeTests.cs
  DesktopBrowserBridgeHeartbeatTests.cs
  DesktopBrowserBridgeDisconnectTests.cs
```

必要时增加 `Microsoft.AspNetCore.TestHost` 10.0.0。覆盖：

- Console 模式不映射；DesktopChild 模式映射。
- 无 Token/错误 Token=401，非 Loopback=403，非 WebSocket=400。
- 首消息不是 Hello、协议不匹配、Hello 超时。
- Hello accepted 前 Broker 不可用，accepted 后可用。
- 第二连接不能让 AwaitingHello 的旧 Endpoint 变成僵尸。
- FakeTimeProvider 推进 45 秒可取消阻塞 Receive 并 Detach。
- 旧 generation finally 不失败新 generation pending。
- 断线后旧 outbound command 不在新连接重放。

### 8.2 Desktop Client

新增 `DesktopBrowserBridgeClientTests.cs`，必须使用 fake transport 而不是公网或真实等待：

- Receive Loop 在等待 HelloAck 时已经运行。
- accepted 后才 Connected；rejected/5 秒超时永不 Connected。
- HelloAck 后仍是同一个 Receive Loop。
- Command -> Dispatcher -> CommandResult correlation。
- Heartbeat/HeartbeatAck。
- FakeClock 推进 45 秒，静默 Socket 的阻塞 Receive 被取消。
- Send/Receive 同时失败只 complete 一次、只创建一个 reconnect task。
- generation N 晚到事件不改变 N+1 状态。
- Disconnect 取消 reconnect；下一次 Connect 可以建立全新 session。
- Token 只写 Header，不进入状态 reason、exception message 或日志。

### 8.3 Workspace/UI 状态

扩充 `BrowserWorkspaceControllerTests`：

- 构造函数依赖完整，不存在未 SetRuntime 的静默成功。
- 创建两个 Page 后实际调用 SurfaceHost.Create 两次，只激活目标 Surface。
- Tab ObservableCollection 在 fake UI Dispatcher 上更新。
- 切换可见 Tab 不改变 AgentTargetPageId。
- Assign target 后无 PageId 的 Bridge command 使用 target。
- 关闭 target 后命令返回 page_not_found，不回退 active。
- Back/Forward/Reload/Stop 后 ViewModel 状态刷新。
- ActivityChanged 更新 Activities 且最多 100 条。
- ClearHandler 后新命令返回 browser_not_available。
- 初始化失败可清理并重试。

## 9. Task 6：发布和真实 UI Smoke

串行执行：

```powershell
dotnet restore Tests\PuddingHost.Tests\PuddingHost.Tests.csproj

dotnet build Source\PuddingBrowser.Protocol\PuddingBrowser.Protocol.csproj --no-restore --nologo
dotnet build Source\PuddingBrowser.WebView2\PuddingBrowser.WebView2.csproj --no-restore --nologo
dotnet build Source\PuddingHost\PuddingHost.csproj --no-restore --nologo
dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo

dotnet test Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo

dotnet publish Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release --no-restore `
  -o .tmp-build\phase2a1-final-preview `
  --nologo

git diff --check
```

### 9.1 单实例前置条件

Smoke 前检查：

```powershell
Get-CimInstance Win32_Process |
  Where-Object Name -in @('PuddingDesktop.exe','PuddingAgent.exe') |
  Select-Object ProcessId,Name,ExecutablePath,CommandLine
```

若用户当前 Desktop 仍运行，停止并请求用户通过托盘“退出 Pudding”。不得 `Stop-Process -Force` 静默终止，也不得给产品增加绕过单实例的永久开关来迁就测试。

### 9.2 隔离 smoke

新增或扩展 `TestScripts/start-phase2a1-browser-smoke.ps1`，只在 `%TEMP%\PuddingAgent\...` 创建 DesktopHome/DataRoot/UDF。脚本输出 PID、端口、路径和日志位置，不自动访问公网、不触碰 `D:\data`。

人工可见验收：

1. 新版标题/运行中心正常，Core Ready，`/health/ready`=200。
2. Core AutoStart=false 的隔离配置下，Agent Browser 仍能初始化和新建 Page。
3. Tab Strip 可见两个 Tab；切换时只有对应 WebView Surface Visible。
4. 两个本地测试页之间完成导航、Back、Forward、Reload、Stop。
5. “将此页交给 Agent”标记 target；用户切到另一 Tab 后 target 不变。
6. Bridge 状态在收到 HelloAck 后变为 Connected，不出现每 5 秒重连。
7. 使用测试 Broker 发出 `page.list/getInfo/goto`，可见页面和 Activity Pane 同步更新。
8. Restart Core 后 Page/Tab 保留、Bridge 重新握手、旧命令不重放。
9. Stop Core 后 Page 可继续查看，Bridge 不再重连。
10. 明确 Exit 后 Desktop/Core/WebView2 无残留。
11. Workbench UDF 与 Agent Browser UDF 是两个不同绝对路径。

如果执行环境不能完成第 3~10 项可见验证，Phase 2A-1 状态必须保持 `UI Smoke 未验收`。

## 10. Task 7：完成后同步文档

只有 build/test/publish/smoke 全部完成后：

- 70 标记 `completed / accepted` 并勾选 DoD；
- 71、72 标记 `completed`，附测试数量和 smoke 证据；
- 69 标记 `Phase 2A-1 completed`，下一步才改为 Phase 2A-2；
- 68 更新真实 Browser Context/Page/Bridge 能力边界；
- `Agents.md` 将下一阶段改为 Phase 2A-2；
- `Source/code_map.md` 删除“WebView2 Driver 当前为骨架”等过期描述并列出最终入口；
- 两个 README 指向已验收状态；
- `How-Debuge.md` 写入 HelloAck 死锁、Watchdog、generation、Surface、DataContext、UDF 和单实例 smoke 诊断方法。

不得在 smoke 未完成时只修改文档把状态伪装为 accepted。

## 11. Definition of Done

- [ ] Desktop Receive Loop 在等待 HelloAck 前已启动，连接可以真实进入 Connected。
- [ ] Core/Desktop 的 45 秒 Watchdog 可取消阻塞 Receive，且有 fake-clock 测试。
- [ ] Desktop transport 可完全 fake，`DesktopBrowserBridgeClientTests` 存在并覆盖握手、重连、代际和心跳。
- [ ] Host 认证、握手、心跳、断线测试文件全部存在并通过。
- [ ] Registry generation 不依赖具体类型 cast，AwaitingHello 连接不会产生僵尸。
- [ ] Tab Strip 绑定真实 Tabs，Activity Pane 绑定真实 Activities。
- [ ] 两 Page 创建后切换会调用 SurfaceHost.ActivateAsync，只有 active Surface Visible。
- [ ] AgentTargetPageId 可赋值、显示且不随用户 Tab 切换变化。
- [ ] Controller 的集合修改全部位于 UI Dispatcher，所有 Page 操作经过统一 gate。
- [ ] 导航状态、按钮、Tab 标题/URL/Loading 实时同步。
- [ ] Browser Workspace 在 DataRoot Ready 后初始化，不依赖 Core Ready；失败可重试。
- [ ] Dispatcher 有类型安全的 ClearHandler，不使用 `null!`。
- [ ] Protocol/WebView2/Host/Desktop build 全部 0 error。
- [ ] Host/Desktop 定向测试全部通过并报告新增用例数量。
- [ ] Release publish 包含 Desktop、Core 和 Workbench 静态资源。
- [ ] 系统 Temp 隔离 smoke 覆盖双 Tab、真实 Bridge、Restart、Stop 和 Exit。
- [ ] 未静默终止用户旧 Desktop，未触碰 `D:\data` 和无关 dirty files。
- [ ] 68/69/70/71/72、Agents、How-Debuge、README 和 code_map 状态一致。

## 12. 完成报告格式

最终必须报告：

1. 初始 dirty worktree 与严格修改边界；
2. 本文 10 个阻断项逐项修复证据；
3. Hello/Receive/Heartbeat/Watchdog 的最终时序；
4. Tab、Surface、Agent target 和 Activity 的最终数据流；
5. 新增测试文件、用例数和结果；
6. build/test/publish 的命令和结果；
7. smoke 的 PID、动态端口、临时 DataRoot、UDF、双 Tab、重连和退出证据；
8. 保留警告、未执行范围和无关 dirty files；
9. 是否真正满足进入 Phase 2A-2 的条件。

只报告 29/29、74/74 或“代码完成、UI 未测”不算最终验收。
