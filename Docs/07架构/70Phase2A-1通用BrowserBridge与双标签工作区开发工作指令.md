# 70 Phase 2A-1 通用 Browser Bridge 与双标签工作区开发工作指令

> - 状态：**completed / accepted（2026-08-02，经 73 最终验收）**
> - 日期：2026-08-02
> - 执行者：Pudding 自身 Agent
> - 前置状态：Phase 1A、Phase 1B-R、Phase 1B-S 已完成
> - 依赖文档：[ADR-066](67ADR-066抖音个人开发者评论接入与浏览器自动化ADR.md)、[总体实施规格](68抖音接入与通用WebView2自动化开发实施规格.md)、[Desktop/Browser 实施规格](69PuddingDesktop浏览器工作区运行中心与存储管理实施规格.md)

> 2026-08-02 最终说明：本文记录初始工作包及当时缺口；这些缺口已由 71、72、73 关闭。最终结果为 Host 43/43、Desktop 92/92、Release publish 和隔离可见 WPF/WebView2 smoke 通过。后续 Phase 2A-2 最小闭环已经完成，结果见 [74](74Phase2A-2最小RemoteBrowser与AgentTools实施验收报告.md)。

## 0. 可直接发送给 Pudding Agent 的指令

```text
请完整执行 Docs/07架构/70Phase2A-1通用BrowserBridge与双标签工作区开发工作指令.md。

目标不是只输出计划，而是在保护当前 dirty worktree 的前提下，完成 Phase 2A-1 的代码、测试、发布和隔离桌面 smoke：
1. 通用 PuddingBrowser.Protocol；
2. Core 上的认证 WebSocket Browser Bridge 和 Command Broker；
3. Desktop Bridge Client、Dispatcher 和生命周期重连；
4. 真实 WebView2CompositionControl Context/Page/Navigation 基线；
5. Windows 11 Agent Browser 双标签工作区；
6. 定向测试、文档和 code_map 更新。

开始前必须阅读 Agents.md、Source/code_map.md、Docs/07架构/68、69、70 和 How-Debuge.md，先冻结本批次文件边界。不要修改或回滚无关的 Feishu、RuntimeTests、Storage、外部子模块等已有工作树变更。不要修改 dev-up.py，不要清理或把构建产物写入 D:\data，不要加入抖音选择器或抖音专用逻辑。

按文档的 Phase 2A-1 范围持续推进到验收完成；如果遇到必须改变协议/进程边界的真实阻塞，先提交证据和最小替代方案，不要静默扩大范围。
```

## 1. 本批次交付结果

完成后必须存在一个可见、可操作、可被 Core Bridge 调度的 **Agent Browser** 页面：

- 主窗口新增“Agent Browser”导航；
- 一个 Browser Context 内可创建两个真实 WebView2 标签页；
- 每个标签页有独立 `PageId`，共享 Context UDF，但与 Workbench UDF 完全隔离；
- 用户可新建、激活、关闭标签页，并使用地址栏、后退、前进、刷新/停止；
- Desktop 主动连接 Core 的 `/desktop/browser-bridge` WebSocket；
- Core 可通过 `IDesktopBrowserCommandBroker` 发出最小 Context/Page/Navigation 命令；
- Desktop Dispatcher 执行命令并返回稳定结果；
- Core 重启后 Desktop 自动重连，旧未完成命令失败且不得自动重放；
- UI 显示 Bridge 状态、Agent 最近动作、暂停、用户接管和继续；
- 所有 Browser 底层实现保持通用，不出现 Douyin URL、DOM 选择器或评论业务。

## 2. 已核对的当前事实

执行时以源码复核为准，但不要重新推翻以下已确认边界：

1. `PuddingBrowser.Abstractions` 已存在 `IBrowserRuntime`、`IBrowserContext`、`IBrowserPage`、`BrowserContextId`、`PageId`、导航模型和事件模型；它引用 `PuddingCore`，但不引用 WPF/WebView2。
2. `PuddingBrowser.WebView2` 当前是骨架：Context/Page 只记录内存身份，导航和 Surface 仍抛出 `NotImplementedException("Phase 3 stub")`。
3. `IBrowserSurface.Control` 当前错误地暴露标准 `WebView2`；本批次必须改为 `WebView2CompositionControl`，与现有 WindowChrome/Mica 避免 airspace 冲突。
4. `PuddingDesktop` 当前只引用 `PuddingCore`，没有引用 Browser Abstractions/WebView2/Protocol；主导航已有 Workbench、运行中心、存储空间、系统设置。
5. Core 只有一个 `http://127.0.0.1:0` 动态 Loopback 地址，同时服务 HTTP API 和 Workbench。
6. DesktopChild 已有 ControlToken、父进程监控和 `/desktop/shutdown` 等本机控制面；Browser Bridge 必须复用同一个 Token 与 Loopback 验证规则。
7. 当前工作树已有未提交改动。本批次不得 reset、checkout、覆盖或顺手整理不在本文件范围内的修改。

## 3. 传输决策：同端口认证 WebSocket

### 3.1 为什么本批次不使用同端口原生 gRPC

当前 Core 是无 TLS 的动态 Loopback HTTP 端口。原生 ASP.NET Core gRPC 要求 HTTP/2；无 TLS 时 `Http1AndHttp2` 无法通过 ALPN 协商并会回落 HTTP/1.1。gRPC-Web 虽可使用 HTTP/1.1，但不支持客户端流和双向流，因此不符合 Desktop/Core 全双工命令通道。

官方依据：

- [ASP.NET Core gRPC protocol negotiation](https://learn.microsoft.com/en-us/aspnet/core/grpc/aspnetcore?view=aspnetcore-10.0)
- [gRPC-Web streaming limitations](https://learn.microsoft.com/en-us/aspnet/core/grpc/grpcweb?view=aspnetcore-10.0)

V1 不增加证书管理，也不增加第二个 HTTP/2 端口。Bridge 使用现有端口上的 `/desktop/browser-bridge` WebSocket，保留全双工、单连接、低延迟和断线语义。

### 3.2 WebSocket 协议不变量

- 只在 `PuddingHostMode.DesktopChild` 映射端点；Console Host 不开放。
- 只接受 Loopback 远端地址。
- 握手 Header 必须包含现有 `X-Pudding-Desktop-Token`；失败返回 401/403，不升级连接。
- 一个 WebSocket Text Message 对应一个 UTF-8 JSON Envelope。
- 单消息最大 1 MiB；超限用稳定错误/关闭码终止，不能无限累积内存。
- Receive Loop 必须把多个 frame 重组到 `EndOfMessage=true`，按累计字节执行 1 MiB 上限；V1 拒绝 Binary Message。
- 每个连接只有一个发送循环；使用 bounded `Channel<BrowserBridgeEnvelope>`，禁止多个线程并发 `SendAsync`。
- Heartbeat 默认 15 秒，45 秒没有收到对端 Heartbeat/Ack 判定断线。
- Desktop Hello 必须携带协议版本、Desktop instance id 和 capability 列表；版本不兼容返回 `browser_protocol_mismatch`。
- 命令必须有 `operationId` 和 `deadlineUtc`。
- 断线后所有 pending command 完成为 `browser_bridge_disconnected`。
- 已经发送过的命令不自动重放。重复 `operationId` 只返回 Desktop 缓存的终态结果。
- 日志不写 Token、Cookie、Authorization、表单值或完整脚本。

## 4. 范围与明确不做

### 4.1 本批次必须实现

Bridge 命令名称固定为：

```text
context.create
context.close
page.create
page.list
page.getInfo
page.activate
page.close
page.goto
page.goBack
page.goForward
page.reload
page.stop
```

必须实现真实 WebView2 Context/Page/Navigation 与双标签 UI。

### 4.2 本批次不实现

- 不实现 DOM Locator、Click、Fill、Type、Evaluate、CDP、Cookie、Network、Download、Upload、PDF；
- 不创建 `PuddingBrowser.AgentTools`，不向普通 Agent 暴露完整 Browser Tools；
- 不实现 Douyin 项目、选择器、作品/评论/回复；
- 不实现独立 `BrowserWindow` 和 Surface 转移；这是 Phase 2A-2；
- 不增加域名白名单、逐操作审批或 Playwright 兼容层；
- 不修改 Workbench React 页面来承载 Agent Browser；Agent Browser 是原生 WPF 页面；
- 不修改 `dev-up.py`，不让 Desktop 编译源码或启动前端开发服务器；
- 不引入 TLS、第二个 Bridge 端口或 gRPC-Web；
- 不为了旧开发数据增加兼容层。

未实现操作通过 Bridge 返回 `browser_operation_not_supported`，不能直接向 Core 泄漏 `NotImplementedException` 堆栈。

## 5. 项目与文件变更

### 5.1 新建 `PuddingBrowser.Protocol`

```text
Source/PuddingBrowser.Protocol/
  PuddingBrowser.Protocol.csproj
  BrowserBridgeProtocol.cs
  BrowserBridgeEnvelope.cs
  BrowserBridgeMessages.cs
  BrowserBridgeCommandNames.cs
  BrowserBridgeErrorCodes.cs
  BrowserBridgeJsonSerializerContext.cs
  BrowserBridgeSerializer.cs
```

要求：

- `TargetFramework=net10.0`；
- 只引用 `PuddingBrowser.Abstractions`；
- 不引用 ASP.NET Core、WPF、WebView2、PuddingHost、PuddingDesktop；
- 使用 `System.Text.Json` source generation；
- 加入 `PuddingAgentNetwork.slnx`。

### 5.2 Core 新文件

```text
Source/PuddingHost/BrowserBridge/
  IDesktopBrowserConnectionRegistry.cs
  DesktopBrowserConnectionRegistry.cs
  DesktopBrowserConnection.cs
  DesktopBrowserBridgeWebSocketEndpoint.cs
  DesktopBrowserBridgeEndpointExtensions.cs
  IDesktopBrowserCommandBroker.cs
  DesktopBrowserCommandBroker.cs
```

修改：

- `PuddingHost.csproj` 引用 `PuddingBrowser.Protocol`；
- `PuddingServiceCollectionExtensions` 注册 Registry/Broker；
- `PuddingWebApplicationExtensions.MapPuddingApplication` 在 DesktopChild 下映射 Bridge；
- 复用 `DesktopControlTokenValidator`，不要复制 Token 读取逻辑。

`RemoteBrowserRuntime/Context/Page` 延后到 Phase 2A-2；本批次由 Broker 与集成测试直接验证最小命令通路，避免同时引入完整 Agent Tools。

### 5.3 Desktop 新文件

```text
Source/PuddingDesktop/Browser/
  IDesktopBrowserBridgeClient.cs
  DesktopBrowserBridgeClient.cs
  BrowserBridgeConnectionState.cs
  BrowserBridgeStateChangedEventArgs.cs
  BrowserBridgeCommandDispatcher.cs
  BrowserOperationResultCache.cs
  BrowserWorkspaceController.cs
  BrowserWorkspaceViewModel.cs
  BrowserTabViewModel.cs
  AgentBrowserActivityViewModel.cs
  AgentBrowserControlState.cs

Source/PuddingDesktop/Views/
  BrowserWorkspaceView.xaml
  BrowserWorkspaceView.xaml.cs
```

修改：

- `PuddingDesktop.csproj` 引用 Protocol、Abstractions、WebView2；
- `MainWindow.xaml(.cs)` 增加 Agent Browser 导航和页面生命周期；
- `DesktopApplicationCoordinator` 管理 Bridge Connect/Disconnect/Reconnect；
- 明确退出时释放 Bridge、Browser Runtime、Context/Page/Surface；Core 普通重启时保留 Desktop 内的 Page，并只重连 Bridge。

### 5.4 WebView2 Driver 拆分

```text
Source/PuddingBrowser.WebView2/
  WebView2BrowserRuntime.cs
  WebView2BrowserContext.cs
  WebView2BrowserPage.cs
  WebView2BrowserSurface.cs
  WebView2BrowserEventHub.cs
  WpfBrowserSurfaceHost.cs
  IBrowserSurfaceHost.cs
  IWebView2UiDispatcher.cs
```

修改 `PuddingBrowser.WebView2.csproj`：

- TFM 与 Desktop 对齐为 `net10.0-windows10.0.17763.0`；
- 保持 `UseWPF=true`；
- 不引用 PuddingDesktop、PuddingHost 或 Douyin。

## 6. 关键类型与函数签名

### 6.1 Protocol

```csharp
public static class BrowserBridgeProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxMessageBytes = 1024 * 1024;
    public const string EndpointPath = "/desktop/browser-bridge";
    public const string ControlTokenHeader = "X-Pudding-Desktop-Token";
}

public enum BrowserBridgeMessageKind
{
    Hello,
    HelloAck,
    Command,
    CommandResult,
    Cancel,
    Event,
    Heartbeat,
    HeartbeatAck
}

public sealed record BrowserBridgeEnvelope
{
    public int ProtocolVersion { get; init; } = BrowserBridgeProtocol.CurrentVersion;
    public required Guid MessageId { get; init; }
    public Guid? CorrelationId { get; init; }
    public required BrowserBridgeMessageKind Kind { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required JsonElement Payload { get; init; }
}

public sealed record BrowserBridgeCommand
{
    public required Guid OperationId { get; init; }
    public string? ContextId { get; init; }
    public string? PageId { get; init; }
    public required DateTimeOffset DeadlineUtc { get; init; }
    public required string Name { get; init; }
    public required JsonElement Arguments { get; init; }
}

public sealed record BrowserBridgeCommandResult
{
    public required Guid OperationId { get; init; }
    public required bool Success { get; init; }
    public JsonElement? Value { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
```

稳定错误码至少包括：

```text
browser_not_available
browser_bridge_disconnected
browser_protocol_mismatch
browser_invalid_command
browser_deadline_exceeded
browser_cancelled
browser_context_not_found
browser_page_not_found
browser_operation_not_supported
browser_operation_failed
browser_paused
browser_user_takeover
```

### 6.2 Core Broker

```csharp
public interface IDesktopBrowserCommandBroker
{
    bool IsDesktopConnected { get; }

    Task<BrowserBridgeCommandResult> ExecuteAsync(
        BrowserBridgeCommand command,
        CancellationToken cancellationToken);

    Task CancelAsync(Guid operationId, CancellationToken cancellationToken);
}

public interface IDesktopBrowserConnectionRegistry
{
    DesktopBrowserConnection? Current { get; }
    bool TryAttach(DesktopBrowserConnection connection);
    void Detach(Guid connectionId);
}
```

Registry V1 只允许一个 Desktop 连接。新连接不能悄悄替换仍健康的连接；旧连接已关闭时才允许原子替换。

### 6.3 Desktop Bridge Client

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

URI 映射固定为 `http -> ws`、`https -> wss`，路径固定为 `/desktop/browser-bridge`。ControlToken 只加入握手 Header，不进入 query string。

### 6.4 Workspace Controller

```csharp
public interface IBrowserWorkspaceController : IAsyncDisposable
{
    IReadOnlyList<BrowserTabViewModel> Tabs { get; }
    PageId? ActivePageId { get; }
    AgentBrowserControlState ControlState { get; }

    Task InitializeAsync(string dataRoot, CancellationToken ct);
    Task<PageId> CreatePageAsync(PageCreateOptions options, CancellationToken ct);
    Task ActivateAsync(PageId pageId, CancellationToken ct);
    Task CloseAsync(PageId pageId, CancellationToken ct);
    Task NavigateAsync(PageId pageId, Uri uri, CancellationToken ct);
    Task GoBackAsync(PageId pageId, CancellationToken ct);
    Task GoForwardAsync(PageId pageId, CancellationToken ct);
    Task ReloadOrStopAsync(PageId pageId, CancellationToken ct);
    Task SetUserTakeoverAsync(bool enabled, CancellationToken ct);
    Task SetPausedAsync(bool paused, CancellationToken ct);
}
```

### 6.5 Surface 与 Page

```csharp
public interface IBrowserSurface : IAsyncDisposable
{
    PageId PageId { get; }
    WebView2CompositionControl Control { get; }
    CoreWebView2 CoreWebView { get; }
}

public sealed class WpfBrowserSurfaceHost : IBrowserSurfaceHost
{
    public WpfBrowserSurfaceHost(
        IWebView2UiDispatcher dispatcher,
        Panel surfaceContainer);

    public Task<IBrowserSurface> CreateAsync(
        BrowserContextId contextId,
        PageId pageId,
        CoreWebView2Environment environment,
        PageCreateOptions options,
        CancellationToken ct);

    public Task ActivateAsync(PageId pageId, CancellationToken ct);
    public Task CloseAsync(PageId pageId, CancellationToken ct);
}
```

所有 `CoreWebView2Environment`、Control、事件订阅、导航和释放都必须经过 `IWebView2UiDispatcher`。

## 7. 实施顺序

### Task 0：冻结边界与基线

1. 阅读必读文档和 `git status --short`。
2. 记录本批次允许修改/新建的文件清单。
3. 明确列出不属于本批次的已有修改，后续不得覆盖。
4. 运行并记录基线：

```powershell
dotnet build Source\PuddingBrowser.Abstractions\PuddingBrowser.Abstractions.csproj --no-restore --nologo
dotnet build Source\PuddingBrowser.WebView2\PuddingBrowser.WebView2.csproj --no-restore --nologo
dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo
dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo
```

这些 WPF 命令必须串行。

### Task 1：Protocol

1. 新建 Protocol 项目并加入 Solution。
2. 完成 Envelope、Message、Error、CommandName 和 source-generated JSON。
3. `BrowserBridgeSerializer.Deserialize` 在分配大对象前检查消息长度。
4. 对未知 `kind`、缺失 id、非法 deadline、版本不匹配返回稳定协议错误。
5. 添加 Protocol round-trip、未知字段、超限、版本不匹配和错误码测试。

### Task 2：Core WebSocket Endpoint 与 Broker

1. 只在 DesktopChild 注册和映射端点。
2. 在 Upgrade 前验证 Loopback、ControlToken 和 WebSocket 请求。
3. 在路由映射前启用 `UseWebSockets`，完成 Hello/HelloAck、分片重组 Receive Loop、单 Writer Channel 和双向 Close handshake。
4. Broker 为每个 pending operation 建立 `TaskCompletionSource`，使用 `RunContinuationsAsynchronously`。
5. deadline 同时由 Core Broker 和 Desktop Dispatcher 执行；先到者完成结果。
6. 连接断开时一次性失败全部 pending，不保留可重放队列。
7. `IsDesktopConnected=false` 时 `ExecuteAsync` 立即返回 `browser_not_available`。

### Task 3：Desktop Bridge Client 与 Dispatcher

1. Core Ready 后建立连接；Core Stop/Failed/RestartScheduled 时断开。
2. 短暂掉线按 1s、2s、5s、10s 上限重连；Coordinator 提供取消 Token。
3. Dispatcher 支持第 4.1 节固定命令集。
4. `BrowserOperationResultCache` 保存最近 512 个终态或 10 分钟，以先达到者淘汰。
5. 重复 `operationId` 返回缓存结果，不再次执行。
6. Pause/Takeover 时拒绝来自 Core 的新命令，但用户仍可直接操作页面。
7. Activity 列表最多 100 条，只保存动作名、目标摘要、开始/完成时间、结果和错误码。

### Task 4：真实 WebView2 Runtime

1. 把嵌套 Stub Context/Page 拆成独立类。
2. 每个 Context 创建一个 `CoreWebView2Environment`：

```text
<DataRoot>/browser/contexts/<ContextId>/user-data
```

3. 同 Context 的多个页面使用同一 Environment；不同 Context 使用不同 UDF。
4. Workbench 继续使用：

```text
<DataRoot>/browser/workbench/user-data
```

5. `NewPageAsync` 创建真实 `WebView2CompositionControl`；`PageInfo` 监听 Title/Source/Navigation 状态更新。
6. `Goto/Back/Forward/Reload/Stop/BringToFront` 全部实现，支持取消和导航超时。
7. Page 导航成功后增加 `PageVersion`；关闭页面后操作返回稳定 page closed/not found。
8. Popup 在同 Context 创建新 Page/Tab；不要交给系统浏览器。
9. 未实现的 DOM/Input/CDP 方法保留明确能力错误，不伪造成功。

### Task 5：Windows 11 Browser Workspace

页面结构：

```text
Tab Strip: [favicon 标题 activity close] [第二页] [+]
Toolbar:   [←] [→] [刷新/停止] [地址栏] [系统浏览器] [DevTools]
Body:      [WebView2CompositionControl Surface] [Agent Activity Pane]
Banner:    Agent 正在控制 / 已暂停 / 用户接管 / Bridge 断开
```

规则：

- Agent Browser 导航放在 Workbench 与运行中心之间；
- 首次进入时只初始化一个 Context，空状态由用户或命令创建 Page；
- 创建两个 Page 不得创建两个 Context；
- 用户查看其他 Tab 不自动改变 Core 当前目标；“将此页交给 Agent”才改变目标；
- 地址栏 Enter 调用 `GotoAsync`，不是直接设置 WebView `Source`；
- 加载中按钮显示停止，否则显示刷新；
- 关闭最后一个 Tab 显示新标签页空状态，不销毁 Persistent Context；
- Activity Pane 默认宽 300px，可折叠；小窗口先折叠 Activity Pane，再压缩地址栏；
- 使用现有 Theme Resource，不硬编码 Light/Dark 颜色。

### Task 6：生命周期

1. Desktop/Core 正常运行：Bridge connected，Browser Pages 可用。
2. Core 重启：Bridge 断开，pending 失败；Desktop 内 Page/Context 保留；新 Core Ready 后重连。
3. 用户停止 Core：不自动重连，Browser 页面显示“Core 已停止”，允许用户查看当前页面但不接受 Agent 命令。
4. Desktop 明确退出：停止接收命令、断开 Bridge、释放 Browser Context/Page/Surface、释放 Workbench、停止 Core。
5. 关闭到托盘：不释放 Bridge 或 Browser。
6. WebView2 ProcessFailed：对应 Page 进入 Failed，返回稳定错误，不让 Desktop 崩溃。

### Task 7：测试、发布和文档

测试目录：

```text
Tests/PuddingHost.Tests/BrowserBridge/
  DesktopBrowserCommandBrokerTests.cs
  DesktopBrowserBridgeAuthenticationTests.cs
  DesktopBrowserBridgeDisconnectTests.cs

Tests/PuddingDesktop.Tests/Browser/
  BrowserBridgeCommandDispatcherTests.cs
  BrowserOperationResultCacheTests.cs
  BrowserWorkspaceControllerTests.cs
  DesktopBrowserBridgeClientTests.cs
```

必须覆盖：

- 无 Token、错误 Token、非 Loopback 拒绝；
- Hello/Heartbeat/版本不匹配；
- correlation、deadline、cancellation；
- 重复 operation id 不重复执行；
- 断线 pending 全部稳定失败；
- 两个 Tab 创建/激活/导航/后退/前进/刷新/停止/关闭；
- Pause/Takeover 拒绝 Agent 命令；
- Workbench UDF 与 Agent Browser UDF 不同；
- Core 重连不重放旧命令。

更新：

- `Source/code_map.md`；
- `Docs/07架构/68`、`69`、`70` 状态；
- `How-Debuge.md` 的 Bridge、UDF、WebSocket、Page/Surface 诊断路径；
- 两个 Docs README 索引。

## 8. 构建与验收命令

新项目第一次执行允许 restore。之后必须串行：

```powershell
dotnet restore Source\PuddingBrowser.Protocol\PuddingBrowser.Protocol.csproj
dotnet restore Source\PuddingDesktop\PuddingDesktop.csproj

dotnet build Source\PuddingBrowser.Protocol\PuddingBrowser.Protocol.csproj --no-restore --nologo
dotnet build Source\PuddingBrowser.WebView2\PuddingBrowser.WebView2.csproj --no-restore --nologo
dotnet build Source\PuddingHost\PuddingHost.csproj --no-restore --nologo
dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo

dotnet test Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo

dotnet publish Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release --no-restore `
  -o .tmp-build\phase2a1-browser-preview `
  --nologo

.\TestScripts\start-phase1a-desktop-smoke.ps1 `
  -PublishRoot .\.tmp-build\phase2a1-browser-preview

git diff --check
```

不得把 `OutDir`、`BaseOutputPath`、UDF 或测试数据指向 `D:\data`。真实 UI smoke 使用系统 Temp 隔离 DesktopHome/DataRoot；如果要用真实 `D:\data`，必须先停止 dev-up 和其它 Desktop Core，并且只做人工只读验证。

## 9. Definition of Done

- [ ] Protocol 项目加入 Solution，依赖方向合规；
- [ ] `/health/ready` 和 Workbench 在新增 WebSocket 后仍为 HTTP 200；
- [ ] Bridge 只允许 DesktopChild + Loopback + 正确 ControlToken；
- [ ] Core 未连接 Desktop 时立即返回 `browser_not_available`；
- [ ] Desktop/Core 可以 Hello、Heartbeat、命令、结果、取消和断线；
- [ ] pending 命令在断线时全部完成且不重放；
- [ ] duplicate operation id 不重复执行；
- [ ] WebView2CompositionControl 真实 Context/Page/Navigation 已实现；
- [ ] 一个 Context 内两个标签页完整闭环；
- [ ] Workbench 与 Agent Browser UDF 隔离；
- [ ] Windows 11 Tab/Toolbar/Activity Pane 在 Light/Dark 下可读；
- [ ] Core 重启后 Bridge 恢复且 Page 保留；
- [ ] Desktop 明确退出后 Core/WebView2 无残留；
- [ ] 定向 build/test/publish/smoke 通过；
- [ ] 没有修改 `dev-up.py`、Douyin 逻辑、`D:\data` 或无关 dirty files；
- [ ] code_map、Docs、How-Debuge 同步。

## 10. Agent 完成报告格式

Pudding Agent 最终必须报告：

1. 实际新增/修改/删除文件；
2. Protocol、Core、Desktop、WebView2 各自完成的能力；
3. 安全和生命周期边界如何落实；
4. 每条验收命令的结果和测试数量；
5. 真实 UI smoke 的页面、PID、动态端口、重连和退出证据；
6. 已知警告、未运行范围和下一批次建议；
7. 明确列出保留未触碰的用户已有 dirty files。

只给“代码已完成”或只给计划不算交付。
