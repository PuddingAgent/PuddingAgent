# 71 Phase 2A-1 验收补丁：真实 Browser Workspace 与 Bridge 可靠性工作指令

> - 状态：**second implementation present / final acceptance blocked**
> - 日期：2026-08-02
> - 执行者：Pudding 自身 Agent
> - 前置文档：[69 实施规格](69PuddingDesktop浏览器工作区运行中心与存储管理实施规格.md)、[70 Phase 2A-1 工作指令](70Phase2A-1通用BrowserBridge与双标签工作区开发工作指令.md)
> - 本批次性质：对已有 Phase 2A-1 初始实现做验收补丁，不进入 Phase 2A-2、DOM Driver 或 Douyin Adapter

> 2026-08-02 复核：第二轮代码已达到 Protocol/WebView2/Host/Desktop build 0 error、Host 29/29、Desktop 74/74，但发现 HelloAck Receive Loop 启动顺序会导致真实连接必然超时，Heartbeat timeout 无法唤醒阻塞 Receive，Tab/Activity/Surface/Agent target 数据流与计划集成测试、publish、UI smoke 尚未闭环。最终收口按 [72 工作指令](72Phase2A-1最终验收修复Bridge握手Surface切换与UISmoke工作指令.md) 执行。

## 0. 可直接发送给 Pudding Agent 的指令

```text
请完整执行 Docs/07架构/71Phase2A-1验收补丁真实BrowserWorkspace与Bridge可靠性工作指令.md。

当前 Phase 2A-1 只完成了可编译骨架，不能按“测试通过”宣布交付。先冻结 dirty worktree，在现有文件上完成 Acceptance Patch：
1. 把 BrowserWorkspaceView 的“实施中”占位页替换为真实 Windows 11 双标签浏览器工作区；
2. 让 BrowserWorkspaceController 真正拥有 WebView2BrowserRuntime/Context/Page，并实现 IBrowserCommandHandler；
3. 把 Dispatcher 绑定到 Controller，使 Bridge 命令真正驱动可见 WebView2；
4. 修复 Bridge 的 HelloAck、单发送循环、心跳超时、连接代际、取消、断线不重放和单一重连循环；
5. 补齐 Host Bridge、Desktop Client、Controller、真实命令成功路径测试；
6. 完成 publish、隔离 DataRoot/UDF 的 Desktop smoke，并更新 68/69/70/71、How-Debuge.md、README 和 code_map。

开始前必须阅读 Agents.md、Source/code_map.md、Docs/07架构/68、69、70、71 和 How-Debuge.md。先执行 git status --short 并保存本批次允许修改文件清单。不得 reset、checkout 或覆盖 Feishu、RuntimeTests、Storage、外部子模块等无关 dirty files。

不要进入 BrowserWindow、RemoteBrowserRuntime、Agent Tool、DOM/Input/CDP/Network、Douyin 选择器或 dev-up.py 产品化；这些全部属于后续批次。不要清理 D:\data，也不要把 build/test/publish/UDF 输出写入 D:\data。持续推进到本文件 Definition of Done 全部满足；只要真实 UI、Bridge 集成测试或 smoke 未完成，就不得把 Phase 2A-1 标记为 completed。
```

## 1. 当前验收结论

2026-08-02 已执行以下定向验证：

| 验证项 | 结果 |
|---|---|
| `PuddingBrowser.Protocol` build | 0 error |
| `PuddingBrowser.WebView2` build | 0 error |
| `PuddingHost` build | 0 error |
| `PuddingDesktop` build | 0 error |
| `PuddingHost.Tests` | 18/18 passed，但没有 Browser Bridge 测试 |
| `PuddingDesktop.Tests` | 62/62 passed，但缺少 Controller/Client 和真实成功路径覆盖 |

这只能证明项目可编译，不能证明 Phase 2A-1 完成。当前存在以下阻断项：

1. `BrowserWorkspaceView.xaml` 仍显示“Agent Browser — Phase 2A-1 实施中”，没有 Tab Strip、地址栏、导航按钮、Surface 容器、Activity Pane 或暂停/接管控制。
2. `BrowserWorkspaceController` 只维护内存 Tab；`CreatePageAsync` 没有调用 `IBrowserContext.NewPageAsync`，导航、后退、前进、刷新和停止没有调用真实 `IBrowserPage`。
3. `BrowserBridgeCommandDispatcher.SetHandler(...)` 在产品代码中没有调用；所有正常 Bridge 命令最终只能返回 `browser_not_available`。
4. `WebView2BrowserRuntime`、`WpfBrowserSurfaceHost` 和 `WebView2BrowserPage` 已存在，但 Desktop 产品路径从未实例化或挂载它们。
5. `WebView2BrowserPage` 保存了 UI Dispatcher，却直接从 Bridge 后台线程读写 `CoreWebView2`；这违反 WebView2 UI 线程约束。
6. Desktop 在发送 Hello 后立即进入 Connected，没有等待 `HelloAck.Accepted=true`；协议不匹配也可能短暂显示为已连接。
7. Core 在 Hello 完成前就把打开的 WebSocket 视为可用连接，Broker 可能过早发送命令。
8. Core 的命令发送循环与 Receive Loop 中的 HelloAck、HeartbeatAck、错误响应会并发调用同一个 WebSocket 的 `SendAsync`。
9. 当前没有 15 秒 Heartbeat / 45 秒失联判定；Desktop 也没有周期性 Heartbeat。
10. Broker 使用跨连接的全局发送 Channel。旧连接断开后，未消费命令可能被新连接读取，违反“旧命令不重放”。旧连接 finally 还可能误伤新连接的 pending command。
11. Desktop 的失败重连可以递归启动多个 `ReconnectLoopAsync`；Receive/Send 同时失败时也可能产生重复重连循环。
12. `Tests/PuddingHost.Tests/BrowserBridge/` 不存在；计划中的 `BrowserWorkspaceControllerTests`、`DesktopBrowserBridgeClientTests` 也不存在。
13. 68/69/70、两个 README、`Source/code_map.md` 和 `How-Debuge.md` 尚未记录真实完成状态和诊断方法。

因此本批次必须先关闭 Phase 2A-1 的验收缺口，不能直接推进 Phase 2A-2。

## 2. 范围和文件边界

### 2.1 允许修改

```text
Source/PuddingBrowser.Protocol/**
Source/PuddingBrowser.WebView2/**
Source/PuddingDesktop/Browser/**
Source/PuddingDesktop/Views/BrowserWorkspaceView.xaml(.cs)
Source/PuddingDesktop/Hosting/DesktopApplicationCoordinator.cs
Source/PuddingDesktop/MainWindow.xaml(.cs)
Source/PuddingDesktop/PuddingDesktop.csproj
Source/PuddingHost/BrowserBridge/**
Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.BrowserBridge.cs
Source/PuddingHost/Extensions/PuddingWebApplicationExtensions.cs
Source/PuddingHost/PuddingHost.csproj
Tests/PuddingHost.Tests/BrowserBridge/**
Tests/PuddingHost.Tests/PuddingHost.Tests.csproj
Tests/PuddingDesktop.Tests/Browser/**
Tests/PuddingDesktop.Tests/PuddingDesktop.Tests.csproj
TestScripts/start-phase2a1-browser-smoke.ps1（仅在确有自动化价值时新增）
Agents.md
How-Debuge.md
Source/code_map.md
Docs/README.md
Docs/07架构/README.md
Docs/07架构/68、69、70、71
PuddingAgentNetwork.slnx（仅修正本批次项目注册）
```

### 2.2 明确禁止

- 不修改 `dev-up.py` 的产品定位或删除它；它仍是源码开发脚本。
- 不新增独立 `BrowserWindow`，不做 Surface 从主窗口移出/移回。
- 不实现 Core 侧 `RemoteBrowserRuntime/Context/Page`，不注册 Agent Browser Tools。
- 不实现 DOM、Input、CDP、Network、Download、Screenshot 或 PDF。
- 不写 Douyin URL、DOM 选择器、评论逻辑或账号逻辑。
- 不触碰 `D:\data`；真实 smoke 使用系统 Temp 下隔离的 DesktopHome、DataRoot 和 UDF。
- 不修改或回滚无关 Feishu、RuntimeTests、Storage 和 `external/github.hyfree.GM` 工作树内容。

## 3. Task 0：冻结边界并建立失败基线

开始时保存：

```powershell
git status --short
git diff -- Source/PuddingBrowser.Protocol Source/PuddingBrowser.WebView2 `
  Source/PuddingDesktop/Browser Source/PuddingDesktop/Views/BrowserWorkspaceView.xaml `
  Source/PuddingDesktop/Views/BrowserWorkspaceView.xaml.cs Source/PuddingHost/BrowserBridge `
  Tests/PuddingHost.Tests Tests/PuddingDesktop.Tests/Browser
```

先补测试或最小 test seam，使下列问题能够稳定复现：

- HelloAck 前不得 Connected / Broker 不得可用；
- 同一 WebSocket 只有一个发送者；
- 旧连接队列不能被新连接消费；
- 一个断线事件只启动一个重连循环；
- Dispatcher 绑定真实 Handler 后可以成功执行 `context.create`、`page.create`、`page.goto`；
- 两个 Page 属于同一个 Context，关闭/激活实际改变 Surface。

不要用 `Task.Delay` 猜时序。引入可控的 Clock、WebSocket transport 或连接代际 test seam。

## 4. Task 1：收紧 Protocol 项目

### 4.1 依赖边界

`PuddingBrowser.Protocol` 当前 DTO 没有使用 `PuddingBrowser.Abstractions` 类型，应删除不必要的 ProjectReference，使其成为纯 `net10.0` JSON 协议项目：

```text
PuddingBrowser.Protocol -> System.Text.Json only
PuddingHost             -> PuddingBrowser.Protocol
PuddingDesktop          -> PuddingBrowser.Protocol + Abstractions + WebView2
```

Protocol 不得引用 WPF、WebView2、ASP.NET Core、PuddingHost 或 PuddingDesktop。

### 4.2 增加稳定参数和结果 DTO

新增 `BrowserBridgeCommandPayloads.cs`，不要在 Controller 中散落大小写敏感的 `JsonElement.GetProperty(...)`：

```csharp
public sealed record ContextCreateArguments
{
    public string? ContextId { get; init; }
}

public sealed record PageCreateArguments
{
    public string? InitialUrl { get; init; }
    public bool Activate { get; init; } = true;
}

public sealed record PageGotoArguments
{
    public required string Url { get; init; }
    public int TimeoutMs { get; init; } = 30_000;
}

public sealed record BrowserContextDescriptor
{
    public required string ContextId { get; init; }
    public required string UserDataDirectory { get; init; }
    public required int PageCount { get; init; }
}

public sealed record BrowserPageDescriptor
{
    public required string ContextId { get; init; }
    public required string PageId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required long PageVersion { get; init; }
    public bool IsActive { get; init; }
}
```

将这些类型加入 source-generated `BrowserBridgeJsonSerializerContext`。参数缺失、URL 非绝对 HTTP/HTTPS、Context/Page id 空白时返回 `browser_invalid_command`，不得把 `JsonException` 或内部堆栈回传 Core。

## 5. Task 2：修复 Core Bridge 的连接与发送模型

### 5.1 每个连接拥有自己的发送队列

把 outbound Channel 从全局 Broker 移到 `DesktopBrowserConnection`。建议公开以下最小边界：

```csharp
public sealed class DesktopBrowserConnection : IAsyncDisposable
{
    public Guid ConnectionId { get; }
    public long Generation { get; }
    public bool IsHandshakeAccepted { get; }
    public DateTimeOffset LastReceivedAt { get; }
    public ChannelReader<BrowserBridgeEnvelope> Outbound { get; }

    public ValueTask EnqueueAsync(
        BrowserBridgeEnvelope envelope,
        CancellationToken cancellationToken);

    public bool TryAcceptHello(BrowserBridgeHello hello, out BrowserBridgeHelloAck ack);
    public void MarkReceived(DateTimeOffset now);
}
```

规则：

- Channel 容量保持 128，`FullMode=Wait`；只能由该连接自己的 Send Loop 消费。
- HelloAck、Heartbeat、HeartbeatAck、Command、Cancel、Event 和协议错误全部进入同一个 Channel。
- Endpoint 中除 WebSocket Close handshake 外，不得在 Receive Loop 直接调用 `SendAsync`。
- 新连接建立时创建新 Channel；断开时 complete 并丢弃旧 Channel，绝不转交给下一连接。
- `IsDesktopConnected` 必须要求 `IsHandshakeAccepted=true`，不仅是 Socket Open。

### 5.2 连接代际和 pending command

Broker 的 pending state 必须记录命令发往哪个 ConnectionId/Generation：

```csharp
private sealed record PendingBrowserOperation(
    Guid OperationId,
    Guid ConnectionId,
    long ConnectionGeneration,
    TaskCompletionSource<BrowserBridgeCommandResult> Completion);
```

- 结果只允许完成同一连接代际的 pending operation。
- `Detach(connectionId)` 只失败该连接代际的 pending，不能失败新连接的命令。
- Registry attach 新连接时先拒绝仍然有效的已认证连接；旧 Socket 已失效才允许替换。
- 相同 `OperationId` 已 pending 时不得覆盖 TCS；返回稳定的 invalid/duplicate 错误或复用同一 pending task。
- cancellation 或 deadline 发生且命令已经入队时，向同一连接 enqueue `BrowserBridgeCancel`；本地 completion 只完成一次。
- caller cancellation 返回 `browser_cancelled`；deadline 返回 `browser_deadline_exceeded`，不得混淆。

推荐调整接口：

```csharp
public interface IDesktopBrowserCommandBroker
{
    bool IsDesktopConnected { get; }

    Task<BrowserBridgeCommandResult> ExecuteAsync(
        BrowserBridgeCommand command,
        CancellationToken cancellationToken);

    Task CancelAsync(Guid operationId, CancellationToken cancellationToken);

    void HandleResult(
        Guid connectionId,
        BrowserBridgeCommandResult result);

    void FailPendingForConnection(
        Guid connectionId,
        string errorCode,
        string message);
}
```

Endpoint 不得向具体实现 `DesktopBrowserCommandBroker` 强制类型转换。

### 5.3 Hello 和心跳

- Socket Upgrade 后先进入 `AwaitingHello`，5 秒内第一条业务消息必须是 Hello。
- 版本或 capability 不兼容：通过单发送队列返回拒绝 HelloAck，然后以 PolicyViolation 正常关闭。
- Accepted Hello 后 Broker 才能发送命令。
- 每 15 秒由 Core enqueue Heartbeat；任何合法消息更新 `LastReceivedAt`。
- 45 秒没有收到 Desktop 消息/HeartbeatAck，关闭该连接并只失败该代际 pending。
- Heartbeat task、Receive task、Send task 共用连接 CTS；finally 必须可重入、幂等且完整 await。
- 日志只记录 connection id、generation、状态、错误码和耗时；不记录 Token、Cookie、Authorization、Arguments 表单值或脚本正文。

### 5.4 认证边界

保持：

- 只在 `PuddingHostMode.DesktopChild` 注册服务和映射 `/desktop/browser-bridge`；Console Host 不暴露端点。
- RemoteIpAddress 必须为 Loopback。
- Header `X-Pudding-Desktop-Token` 使用现有 `DesktopControlTokenValidator` 固定时间比较。
- 无 Token/错误 Token 返回 401，非 Loopback 返回 403，非 WebSocket 返回 400。

## 6. Task 3：把 Desktop Bridge Client 做成单一生命周期状态机

### 6.1 API

保留 `IDesktopBrowserBridgeClient` 外部能力，但内部增加生命周期串行化和期望连接状态：

```csharp
public interface IDesktopBrowserBridgeClient : IAsyncDisposable
{
    BrowserBridgeConnectionState State { get; }
    event EventHandler<BrowserBridgeStateChangedEventArgs>? StateChanged;

    Task ConnectAsync(
        Uri coreBaseAddress,
        string controlToken,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}
```

实现必须满足：

1. `ConnectAsync` 幂等；相同 endpoint/token 已连接或正在连接时不新建第二条连接。
2. 每次连接使用独立 CTS、Socket、outbound Channel、HelloAck TCS 和 generation。
3. Send Loop 先启动，Hello 进入 outbound Channel；所有消息只有 Send Loop 调用 `SendAsync`。
4. 收到且验证 `HelloAck.Accepted=true` 后才从 Connecting 进入 Connected；5 秒未收到则失败。
5. Heartbeat 每 15 秒发送；45 秒没有收到任何服务端消息则触发断线。
6. Receive/Send/Heartbeat 任一路失败都调用同一个原子 `CompleteConnectionOnce(...)`。
7. 全实例只能存在一个 reconnect task；1s/2s/5s/10s 退避，不递归创建 ReconnectLoop。
8. Coordinator 明确 Stop/Failed/RestartScheduled 时设置 desired-connected=false，取消重连；新的 Core Ready 地址到达后才重新连接。
9. Core 地址改变时旧 connection 必须先完整关闭；旧 generation 的回调不得改变新 generation 的状态。
10. 不使用遗失所有权的 `CancellationToken.None` 启动无限后台重连；使用 Desktop lifetime token。

为便于确定性测试，抽出最小 transport/clock seam，例如：

```csharp
public interface IDesktopBrowserWebSocketFactory
{
    IDesktopBrowserWebSocket Create();
}

public interface IBrowserBridgeClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
```

不要为了测试复制一套生产状态机。

## 7. Task 4：接通真实 WebView2 Runtime、Context、Page 和 Dispatcher

### 7.1 UI 线程约束

`WebView2BrowserPage` 的每个 CoreWebView2 操作都必须经过 `IWebView2UiDispatcher`：

- Source/DocumentTitle/CanGoBack/CanGoForward 读取；
- Navigate、GoBack、GoForward、Reload、Stop、OpenDevToolsWindow；
- 事件订阅/取消；
- Dispose。

`GotoAsync` 要序列化同一个 Page 的并发导航，确保自己的 NavigationCompleted handler 不会被另一导航错误完成；取消/超时后解绑 handler。`ProcessFailed` 转换为 Page failed 状态和 `browser_operation_failed`，不能让 WPF 进程崩溃。

### 7.2 Surface 单一所有者

修复当前 `WpfBrowserSurfaceHost.CloseAsync` 和 `WebView2BrowserPage.DisposeAsync` 都 dispose Surface 的双重所有权。选择一个明确规则：

- 推荐由 `WpfBrowserSurfaceHost` 拥有 Control/Surface：负责从 Panel 移除并 dispose；Page 只取消 CoreWebView2 事件；或
- 由 Page 拥有 Surface：Host 只 attach/detach。

整个项目只能采用一种规则，并以测试验证一次关闭只 dispose 一次。所有 `_surfaces` 和 `_pages` 访问必须由 Controller 串行 gate 或 UI Dispatcher 保护。

### 7.3 固定 UDF

Agent Browser 默认 persistent context 使用：

```text
<DataRoot>/browser/agent-browser/user-data
```

Workbench 保持：

```text
<DataRoot>/browser/workbench/user-data
```

二者路径必须用 `Path.GetFullPath` 比较且不相等。不要在日志输出 Cookie 或 profile 内容。

## 8. Task 5：重写 BrowserWorkspaceController 为真实执行器

### 8.1 责任和构造函数

`BrowserWorkspaceController` 同时实现 `IBrowserWorkspaceController` 与 `IBrowserCommandHandler`，但不直接创建 WPF 控件：

```csharp
public sealed class BrowserWorkspaceController :
    IBrowserWorkspaceController,
    IBrowserCommandHandler,
    IAsyncDisposable
{
    public BrowserWorkspaceController(
        IBrowserRuntime runtime,
        IBrowserSurfaceHost surfaceHost,
        BrowserWorkspaceViewModel viewModel);

    public Task InitializeAsync(CancellationToken cancellationToken);
    public Task<BrowserBridgeCommandResult> ExecuteAsync(
        BrowserBridgeCommand command,
        CancellationToken cancellationToken);
}
```

Controller 使用一个 `SemaphoreSlim` 串行修改 Context/Page/Tab/active target。它必须保存：

```csharp
private IBrowserContext? _context;
private readonly Dictionary<PageId, IBrowserPage> _pages;
private PageId? _activePageId;
private PageId? _agentTargetPageId;
```

### 8.2 命令映射

| Bridge command | 真实调用 | 返回值 |
|---|---|---|
| `context.create` | `IBrowserRuntime.CreateContextAsync` | `BrowserContextDescriptor` |
| `context.close` | `IBrowserRuntime.CloseContextAsync` | empty success |
| `page.create` | `IBrowserContext.NewPageAsync` | `BrowserPageDescriptor` |
| `page.list` | `IBrowserContext.ListPagesAsync` | descriptor array |
| `page.getInfo` | `IBrowserContext.GetPageAsync` | descriptor |
| `page.activate` | `IBrowserSurfaceHost.ActivateAsync` + `BringToFrontAsync` | descriptor |
| `page.close` | `IBrowserContext.ClosePageAsync` | empty success |
| `page.goto` | `IBrowserPage.GotoAsync` | navigation + page descriptor |
| `page.goBack` | `IBrowserPage.GoBackAsync` | page descriptor |
| `page.goForward` | `IBrowserPage.GoForwardAsync` | page descriptor |
| `page.reload` | `IBrowserPage.ReloadAsync` | page descriptor |
| `page.stop` | `IBrowserPage.StopAsync` | page descriptor |

要求：

- 一个 Desktop 默认只有一个 persistent Agent Browser Context；重复 `context.create` 返回现有 context，不创建另一个 UDF。
- 两个 Page 必须共享这个 Context 和 Environment，但拥有不同 PageId/Control。
- 用户切换可见 Tab 只改变 `ActivePageId`；只有“将此页交给 Agent”才改变 `AgentTargetPageId`。
- Pause/UserTakeover 只拒绝来自 Bridge 的新命令；用户工具栏导航仍可操作。
- `SetPausedAsync`、`SetUserTakeoverAsync` 必须同步 Dispatcher gate 和 ViewModel 状态。
- 找不到对象返回 `browser_context_not_found` / `browser_page_not_found`，不抛裸异常。
- Page/Context 关闭后同步移除 Tab、Surface 和字典；关闭最后一页保留 Context 并显示空状态。
- Controller Dispose 先禁止新命令，再关闭 Page/Context/Runtime，最后释放 gate。

### 8.3 产品接线

必须存在一条可追踪的产品调用链：

```text
DesktopApplicationCoordinator/Core Ready
  -> DesktopBrowserBridgeClient
  -> BrowserBridgeCommandDispatcher
  -> BrowserWorkspaceController (IBrowserCommandHandler)
  -> WebView2BrowserRuntime
  -> WebView2BrowserContext
  -> WebView2BrowserPage
  -> WebView2CompositionControl
```

`BrowserWorkspaceView.InitializeAsync(...)` 建议签名：

```csharp
public Task InitializeAsync(
    string dataRoot,
    BrowserBridgeCommandDispatcher dispatcher,
    IDesktopBrowserBridgeClient bridgeClient,
    CancellationToken cancellationToken);

public ValueTask DisposeAsync();
```

初始化成功后必须调用 `dispatcher.SetHandler(controller)`。退出时先 unset/disable handler，再 dispose Controller/Runtime。DataRoot 有效后即可初始化本地 Browser Workspace；Core 未启动不阻止用户看到页面和当前网页，只把 Bridge 状态显示为“Core 已停止”。初始化失败只在 Browser 页面显示可恢复错误，不得阻塞 Desktop、Settings、Runtime Center 或 Workbench。

## 9. Task 6：实现 Windows 11 Browser Workspace UI

删除“Phase 2A-1 实施中”占位内容。页面至少包含：

```text
┌ Tab Strip: [标题 ×] [标题 ×] [+]                       ┐
├ Toolbar: [←][→][↻/×] [地址栏................][转交 Agent] ┤
├─────────────────────────────┬─────────────────────────┤
│ WebView2 SurfaceContainer   │ Agent Activity Pane     │
│ 或“新建标签页”空状态         │ Bridge / 控制状态 / 活动 │
└─────────────────────────────┴─────────────────────────┘
```

必须实现：

- Tab 新建、切换、关闭；当前 Tab 标题、Url、Loading 状态实时更新。
- 后退、前进、刷新/停止和地址栏 Enter；无 scheme 输入按 `https://` 解析，非法地址显示内联错误。
- Surface 容器只能显示 active Page 的 Control，不能重建 WebView 伪装成切换。
- Agent Activity Pane 默认约 300px，可折叠；显示 Bridge 状态、控制状态、最近动作、目标摘要、耗时和错误码。
- “暂停 Agent”“用户接管”“继续”“将此页交给 Agent”按钮有明确状态和禁用规则。
- Bridge 未连接时显示非阻塞状态条；不遮挡用户查看当前网页。
- 使用现有 DynamicResource、Segoe Fluent Icons、圆角、Focus Visual、Light/Dark；不得使用 Emoji 充当正式图标，不得硬编码主题颜色。
- 当前 `BoolToVisibilityConverter` 不能直接绑定 enum；新增正确 converter 或由 ViewModel 暴露布尔属性。
- 小窗口先折叠 Activity Pane，再压缩地址栏；Tab 区可以水平滚动。

`MainWindow` 必须：

- 在 DataRoot 可用后初始化 BrowserWorkspace；
- Core Ready/Stop/Restart 只改变 Bridge，不能销毁现有 Browser Context/Page；
- 明确 Exit 时 await `BrowserWorkspaceView.DisposeAsync()`；关闭到托盘不释放 Browser；
- Browser 初始化和释放异常写 DesktopDiagnosticLog，但不阻塞退出。

本批次仍不增加独立 BrowserWindow。

## 10. Task 7：补齐测试

### 10.1 Host 测试

新增：

```text
Tests/PuddingHost.Tests/BrowserBridge/
  DesktopBrowserCommandBrokerTests.cs
  DesktopBrowserBridgeAuthenticationTests.cs
  DesktopBrowserBridgeHandshakeTests.cs
  DesktopBrowserBridgeDisconnectTests.cs
  DesktopBrowserBridgeHeartbeatTests.cs
```

必要时在 `PuddingHost.Tests.csproj` 增加 `Microsoft.AspNetCore.TestHost` 10.0.0，用真实 WebSocket TestServer 覆盖：

- Console 模式没有 endpoint；DesktopChild 才映射。
- 无 Token、错误 Token、非 Loopback、非 WebSocket 状态码。
- Hello 前 Broker 返回 `browser_not_available`。
- Hello accepted/rejected、协议版本不匹配、首消息不是 Hello。
- 所有 outbound message 经过单一发送队列；并发 Command + Heartbeat 不并发 SendAsync。
- correlation、deadline、caller cancellation 和 Cancel envelope。
- 断线只失败该 generation pending；新连接不受旧 finally 影响。
- 旧连接队列的命令不在重连后重放。
- 45 秒心跳超时使用 fake clock，不实际等待。
- duplicate operation id 不覆盖 TCS、不重复发送。

### 10.2 Desktop 测试

保留现有测试并新增：

```text
Tests/PuddingDesktop.Tests/Browser/
  BrowserWorkspaceControllerTests.cs
  DesktopBrowserBridgeClientTests.cs
  WebView2BrowserPageThreadingTests.cs（若可用 fake CoreWebView seam）
```

至少覆盖：

- Controller 创建一个 Context、两个真实抽象 Page，并激活不同 Surface。
- `page.goto/goBack/goForward/reload/stop/close/list/getInfo` 都调用正确的 fake `IBrowserPage`。
- Dispatcher 绑定 Controller 后正常命令 Success=true，不再只测 pause/error 分支。
- 两个 Page 的 ContextId 相同、PageId 不同；关闭最后一页不关闭 persistent Context。
- Pause/UserTakeover 拒绝 Bridge 命令但用户方法仍执行。
- duplicate operation id 只调用 handler 一次。
- deadline/cancel/unknown id 使用稳定错误码。
- Client 等待 HelloAck 后才 Connected；拒绝/超时不进入 Connected。
- Receive/Send 同时失败只产生一个 reconnect task。
- Disconnect/Stop 取消 reconnect；新 Core Ready 才恢复。
- generation N 的晚到事件不能覆盖 generation N+1 状态。
- Workbench UDF 与 Agent Browser UDF 绝对路径不同。
- Surface 关闭只 dispose 一次。

测试不得依赖真实 `D:\data`、公网或用户浏览器 profile。纯单元测试使用 Temp；真实 WebView2 留给 UI smoke。

## 11. Task 8：构建、发布和真实 smoke

先 restore 新依赖，随后串行执行，避免 WPF 共用 obj 的 RG1000：

```powershell
dotnet restore Source\PuddingBrowser.Protocol\PuddingBrowser.Protocol.csproj
dotnet restore Source\PuddingDesktop\PuddingDesktop.csproj
dotnet restore Tests\PuddingHost.Tests\PuddingHost.Tests.csproj

dotnet build Source\PuddingBrowser.Protocol\PuddingBrowser.Protocol.csproj --no-restore --nologo
dotnet build Source\PuddingBrowser.WebView2\PuddingBrowser.WebView2.csproj --no-restore --nologo
dotnet build Source\PuddingHost\PuddingHost.csproj --no-restore --nologo
dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo

dotnet test Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo

dotnet publish Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release --no-restore `
  -o .tmp-build\phase2a1-acceptance-preview `
  --nologo

.\TestScripts\start-phase1a-desktop-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase2a1-acceptance-preview

git diff --check
```

若修改了 Browser smoke 脚本，应使用系统 Temp 创建隔离 DesktopHome/DataRoot，不得使用 `D:\data`。真实 smoke 必须记录：

1. Desktop PID、Core PID、动态 Loopback 端口和 `/health/ready` HTTP 200；
2. Workbench 正常加载；
3. Agent Browser 不再显示“实施中”；
4. 新建两个 Tab，分别导航到两个本地/稳定测试页面；
5. Tab 切换显示对应 WebView2，后退/前进/刷新/停止可用；
6. Bridge 先 Connecting，再在 HelloAck 后 Connected；
7. Core Restart 后 Tab/Page 保留，Bridge 自动恢复，旧命令不重放；
8. Stop Core 后页面仍可查看，Bridge 显示已停止且不重连；
9. 明确 Exit 后 Desktop/Core/WebView2 无残留进程；
10. Workbench UDF 与 Agent Browser UDF 为两个不同绝对路径。

如果当前执行环境不能完成可见 UI smoke，必须明确标记“未验收”，不得将文档状态改为 completed。

现有 NU1903/NU1904 警告应原样记录；不要在本批次顺手升级全仓依赖。若移除 Protocol 的不必要依赖后其告警自然消失，可记录为依赖边界改善。

## 12. Task 9：完成后更新文档

只有所有 DoD 完成后才能：

- 将 70 状态改为 `completed / accepted`，逐项勾选 DoD；
- 将 69 状态改为 `Phase 2A-1 completed`，下一步指向 Phase 2A-2；
- 在 68 记录通用 Browser 的当前真实实现边界；
- 在 `Agents.md` 将下一阶段改为 Phase 2A-2，但仍强调不进入 Douyin 特化；
- 更新两个 README 和 `Source/code_map.md`，列出 Protocol、Host Broker、Desktop Client、Controller、View 和测试入口；
- 在 `How-Debuge.md` 写入 Bridge 诊断：endpoint、连接代际、Hello、Heartbeat、pending、UDF、WebView2 ProcessFailed、重连日志和隐私过滤。

Phase 2A-2 的独立工作文档应在本批次验收通过后另行创建，不得与本补丁混合实现。

## 13. Definition of Done

- [ ] `BrowserWorkspaceView` 不再含“实施中”占位内容；双标签、工具栏、Surface 和 Activity Pane 可见可用。
- [ ] 产品代码实例化 `WebView2BrowserRuntime`，Controller 真正创建 Context/Page/Surface。
- [ ] Dispatcher 在产品路径绑定 Controller，Bridge 命令可成功驱动可见 Page。
- [ ] 所有 CoreWebView2 操作经过 UI Dispatcher。
- [ ] 一个 persistent Context 内两个 Page 完成创建、切换、导航和关闭闭环。
- [ ] Workbench 与 Agent Browser UDF 绝对路径不同。
- [ ] HelloAck 前两端均不认为 Bridge 可用。
- [ ] 每个连接只有一个 WebSocket 发送循环。
- [ ] Heartbeat 15 秒、失联 45 秒语义有 fake-clock 测试。
- [ ] 旧 connection generation 不能发送/完成/失败新 generation 的命令。
- [ ] 断线后 pending 稳定失败，旧命令不重放。
- [ ] Desktop 同时最多一个 reconnect task，Stop 后不重连。
- [ ] caller cancellation、deadline、duplicate operation id 均有稳定语义。
- [ ] Host BrowserBridge 测试、Desktop Controller/Client/成功路径测试全部存在并通过。
- [ ] Protocol、WebView2、Host、Desktop 定向 build 全部 0 error。
- [ ] Host/Desktop 定向测试全部通过，并报告新增测试数量。
- [ ] Release publish 成功且包含 `core/PuddingAgent.exe` 与 Workbench 静态资源。
- [ ] 隔离 Desktop smoke 覆盖两个 Tab、Bridge 重连、Core Stop 和明确退出。
- [ ] 没有修改 `dev-up.py`、Douyin 逻辑、`D:\data` 或无关 dirty files。
- [ ] 68/69/70/71、两个 README、`Source/code_map.md`、`Agents.md`、`How-Debuge.md` 同步。

## 14. 完成报告格式

最终报告必须包含：

1. 冻结的初始 dirty worktree 与实际修改文件；
2. 上述 13 个阻断项逐项如何关闭；
3. Protocol/Core/Desktop/WebView2/UI 各层的最终调用链；
4. Host/Desktop 新增测试文件、用例数和结果；
5. build/test/publish 的完整结果与保留警告；
6. UI smoke 的 PID、端口、两个 Tab、UDF、重连、Stop 和退出证据；
7. 未运行或失败的验收项；
8. 保留未触碰的无关 dirty files；
9. 是否满足进入 Phase 2A-2 的条件。

只报告“编译通过”“测试通过”或“骨架完成”不算验收交付。
