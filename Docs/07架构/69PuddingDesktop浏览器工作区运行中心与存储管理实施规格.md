# 69 PuddingDesktop 浏览器工作区、运行中心与存储管理实施规格

> - 状态：**Phase 1B-R / Phase 1B-S 已完成；Phase 2A-1/2 accepted；Phase 2A-3 automated accepted（真实 DeepSeek smoke pending，2026-08-02）**
> - 日期：2026-08-02
> - 前置文档：[ADR-066](67ADR-066抖音个人开发者评论接入与浏览器自动化ADR.md)、[WebView2 实施规格](68抖音接入与通用WebView2自动化开发实施规格.md)
> - 目标平台：Windows 10/11、.NET 10、WPF、WebView2 Evergreen Runtime
> - 本文用途：作为 Phase 1B/1C/2 的 UI、进程管理、IPC、类拆分和验收输入

## 1. 决策摘要

### 1.1 Agent Browser 使用浏览器式工作区，不复用 Workbench

必须增加多标签页、地址栏、后退、前进、刷新/停止和新建标签页，但这些浏览器控件只属于新增的 **Agent Browser** 页面，不包裹现有 Workbench。

- Workbench WebView2 继续只加载 Pudding 自身 `/admin/`；
- Agent Browser 使用独立 Context、Page、UDF 和标签页；
- `IBrowserPage` 与可见标签页一一对应，popup 自动成为新标签页；
- Agent 操作默认在主窗口的 Browser Workspace 中可见；
- 不为每次 Agent 操作自动弹窗；用户可主动“在独立窗口打开”；
- 独立窗口与主窗口不能同时渲染同一个 `PageId`，只允许转移 Surface 所有权；
- V1 使用 `AllowAllBrowserCapabilityPolicy`，UI 的暂停、接管和继续是运行控制，不是权限审批。

推荐形态是“普通浏览器的页面区 + Codex 风格 Agent 活动栏”：

```text
┌─────────────────────────────────────────────────────────────────────────┐
│ Pudding / Agent Browser                                      Caption   │
├───────────┬───────────────────────────────────────────────┬─────────────┤
│ Navigation│ Tab 1 │ Tab 2 │ +                            │ Agent 活动  │
│ Workbench ├───────────────────────────────────────────────┤             │
│ Browser   │ ←  →  ↻  [ https://creator.douyin.com/... ] │ 当前任务    │
│  ●        ├───────────────────────────────────────────────┤ 最近动作    │
│ Runtime   │                                               │ 暂停/接管   │
│ Storage   │               WebView2 Surface                │ 继续        │
│ Settings  │                                               │             │
├───────────┴───────────────────────────────────────────────┴─────────────┤
│ Core 状态 / Browser Bridge 状态 / DataRoot                              │
└─────────────────────────────────────────────────────────────────────────┘
```

在用户停留于 Workbench 时，Agent 启动 Browser 任务只显示导航徽标和非阻塞通知，不强制切页、不抢焦点。用户点击通知后进入 Browser Workspace。设置项 `AutoRevealAgentBrowser` 可在后续版本允许首次操作时自动切换，默认值为 `false`。

### 1.2 Desktop 是产品进程主管，ASP.NET Core 仍是 Core

`PuddingDesktop.exe` 负责窗口、WebView2、系统配置、子进程生命周期、运行状态和桌面诊断；业务 API、Agent、Connector、数据库和 Runtime 继续属于 ASP.NET Core Core。

不把 ASP.NET Core 进程内托管到 WPF，也不让 Controller 直接调用 `MainWindow`。Agent 控制 WebView2 通过进程间 Bridge 完成。

Desktop 是交互式用户会话中的守护进程，不安装成 Windows Service。Phase 1B 后，窗口关闭按钮默认最小化到系统托盘，Core 和外部 HTTP API 继续运行；托盘菜单提供“打开 Pudding、启动、停止、重启、退出”。只有用户明确选择“退出 Pudding”、Windows 会话结束或系统关闭时，才执行 Core 的完整停止流程。设置允许改为“关闭窗口并退出”。

### 1.3 `dev-up.py` 只用于源码开发，最终交付是 Desktop

`dev-up.py` 保留在源码仓库中，继续服务开发者的编译、前端开发服务器、代理、Codex Service 和调试流程。它不是产品组件，不进入安装包，也不作为最终用户启动 Pudding 的入口。

- 最终用户只使用 `PuddingDesktop.exe`；
- Desktop 只提供已发布 Core 的启动、停止、重启、健康状态、日志、自动启动和故障恢复；
- Desktop 不提供源码编译、pnpm 安装、前端开发服务器、反向代理或完整仓库清理按钮；
- 前端生产资源随 Desktop 发布包进入 `core/wwwroot/admin`；
- `dev-up.py` 的 `frontend-only`、开发反向代理、源码 rebuild 和 Codex Service 监督继续属于开发环境；
- Desktop 与 `dev-up.py` 不共享 PID、端口所有权或进程状态，不允许彼此停止对方创建的进程；
- 使用同一个 DataRoot 做人工验证时必须先停止另一套宿主，避免两个 Core 同时访问数据库。

### 1.4 Storage 页面由 Desktop 实现，V1 只清理旧日志

磁盘枚举和文件清理属于桌面系统集成能力，可以位于 `PuddingDesktop`，不需要进入 Core 业务层。

V1 的写操作严格限定为：删除 `<DataRoot>/logs` 下最后修改时间早于当前时间 24 小时的日志文件。数据库、会话、Memory、Workspace、Browser UDF、附件、备份和未知目录只统计，不提供删除按钮。

## 2. 主窗口信息架构

主导航调整为：

1. `Workbench`：现有产品首页、聊天和后台功能；
2. `Agent Browser`：Agent 控制的多标签浏览器；
3. `运行中心`：Core 状态、健康、进程、运行日志和启停；
4. `存储空间`：DataRoot/磁盘统计与旧日志清理；
5. `系统设置`：DataRoot、端口、启动策略、关闭行为和主题。

底部紧凑状态栏保留 Core 状态和实际 Loopback 地址。启动、停止、重启按钮可以继续保留，运行中心提供完整信息和高级操作。

`MainWindow` 只负责页面导航和窗口生命周期，不能继续吸收 Browser、Storage 或进程编排逻辑。新增逻辑必须由 Controller/ViewModel/Service 承担。

## 3. Agent Browser UI 详细规格

### 3.1 Tab Strip

每个 Tab 显示：favicon、标题、加载状态、Agent 活动点和关闭按钮。Tab 状态来自 `PageInfo` 与 Browser Event，不从 XAML 控件反向推断。

```csharp
public sealed partial class BrowserTabViewModel : ObservableObject
{
    public required PageId PageId { get; init; }
    public required BrowserContextId ContextId { get; init; }
    public string Title { get; }
    public Uri? Address { get; }
    public bool IsActive { get; }
    public bool IsLoading { get; }
    public bool IsAgentOperating { get; }
    public string? ErrorText { get; }
}
```

规则：

- 新建 Tab 创建新的 `IBrowserPage`，不创建新的 Context；
- “新建隔离会话”才创建新的 Context/UDF；
- popup/new-window 请求默认创建同 Context 的新 Tab；
- 关闭最后一个 Tab 后显示新标签页空状态，不自动销毁 Persistent Context；
- Tab 顺序是 UI 状态，Page 身份是 Runtime 状态，两者分离；
- Agent 激活某个 Page 时同步选中 Tab，但用户查看其他 Tab 不自动改变 Agent 的目标 Page，除非用户明确点击“将此页交给 Agent”。

### 3.2 Navigation Toolbar

工具栏固定提供：

- 后退 `GoBackAsync`；
- 前进 `GoForwardAsync`；
- 加载中显示停止，否则显示刷新；
- 可编辑地址栏，Enter 调用 `GotoAsync`；
- 新建标签页；
- 在系统浏览器打开；
- 开发者工具；
- 在独立窗口打开/返回主窗口。

地址栏显示当前 Page URL，不显示 Workbench 的 Loopback URL。对于 `about:blank`、内部错误页和尚未创建的 Page，显示明确的空状态。

### 3.3 Agent 控制表现

```csharp
public enum AgentBrowserControlState
{
    Idle,
    Preparing,
    Navigating,
    Acting,
    Waiting,
    UserTakeover,
    Paused,
    Failed
}

public sealed record BrowserControlActivity
{
    public required Guid OperationId { get; init; }
    public required PageId PageId { get; init; }
    public required string Action { get; init; }
    public required string Summary { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ErrorCode { get; init; }
    public BoundingBox? TargetBounds { get; init; }
}
```

当 Agent 正在操作：

- Tab 显示 Accent 活动点；
- 地址栏右侧显示“Agent 正在控制”；
- 页面上方显示不遮挡内容的状态条；
- Activity Pane 保留最近 100 个动作，默认只显示动作类型、目标摘要、耗时和结果；
- click/fill/drag 前可通过 `TargetBounds` 绘制 600–1000ms 的 WPF 高亮框；
- 不在普通日志记录 Cookie、Authorization、表单内容或完整脚本；
- “暂停自动化”取消当前可取消命令并暂停后续命令派发；
- “我要接管”进入 `UserTakeover`，WebView2 保持可交互，Agent 命令在 Core 侧等待；
- “继续”恢复命令派发；
- 关闭窗口或 Page 时，正在等待的命令返回稳定的 `browser_page_closed`。

V1 不增加域名白名单、逐操作确认或脚本能力限制。用户控制按钮用于可观测性、调试和紧急停止。

### 3.4 独立窗口

不自动弹出 Agent Browser 窗口。`BrowserWindow` 是同一 Browser Workspace 的可选承载面：

- 一个 Desktop 实例最多一个 BrowserWindow；
- 打开 BrowserWindow 时，把 Browser Surface 容器从主窗口转移过去；
- 主窗口 Browser 页面显示“浏览器已在独立窗口打开”和“移回主窗口”；
- 关闭 BrowserWindow 只把 Surface 移回主窗口，不关闭 Context/Page；
- Desktop 退出时统一关闭 BrowserWindow、Page、Context 和 WebView2 Environment。

Agent Browser 与 Workbench 一样使用 `WebView2CompositionControl`。现有 `IBrowserSurface.Control` 必须从标准 `WebView2` 改为 `WebView2CompositionControl`，使自定义 WindowChrome、圆角、Activity Pane 和动作高亮不受 WPF airspace 限制。

## 4. Core 与 Desktop Browser Bridge

### 4.1 通信方向

采用 **Desktop 主动连接 Core 的认证 WebSocket 全双工通道**。端点位于现有 ASP.NET Core 动态 Loopback HTTP 端口的 `/desktop/browser-bridge`；Desktop 是客户端，因此 Desktop 不监听第二个端口，也不引用 `PuddingHost`。

这里明确修正早期“同一明文 HTTP 端口承载双向原生 gRPC”的设计：ASP.NET Core 原生 gRPC 要求 HTTP/2；微软文档明确说明，没有 TLS 时 `Http1AndHttp2` 无法协商并会回落 HTTP/1.1，而 gRPC-Web 在 HTTP/1.1 上又不支持客户端流和双向流。Pudding V1 不为本机 Bridge 引入证书、第二端口或 gRPC-Web 降级，因此同端口 WebSocket 是更简单、可测试且保留全双工语义的实现。参考：[ASP.NET Core gRPC protocol negotiation](https://learn.microsoft.com/en-us/aspnet/core/grpc/aspnetcore?view=aspnetcore-10.0)、[gRPC-Web streaming limitations](https://learn.microsoft.com/en-us/aspnet/core/grpc/grpcweb?view=aspnetcore-10.0)。

```text
Core Agent Tool
  -> RemoteBrowserRuntime (IBrowserRuntime proxy)
  -> DesktopBrowserCommandBroker
  -> /desktop/browser-bridge (authenticated WebSocket)
  -> DesktopBrowserBridgeClient
  -> BrowserBridgeCommandDispatcher
  -> WebView2BrowserRuntime
  -> IWebView2UiDispatcher
  -> WpfBrowserSurfaceHost / BrowserWorkspaceView
```

Desktop 在收到 Core Ready 后，用同一个 ControlToken 建立 Bridge；Core 重启会中断 WebSocket，Desktop 将所有未完成命令完成为 `browser_bridge_disconnected`，然后按新地址重连。断线时不能自动重放已发送命令。

### 4.2 新项目

新增 `Source/PuddingBrowser.Protocol/PuddingBrowser.Protocol.csproj`，目标框架为 `net10.0`，只包含协议常量、JSON Envelope、消息模型、稳定错误码和序列化上下文，不引用 WPF/WebView2/ASP.NET Core。

```csharp
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
```

一个 WebSocket Text Message 对应一个 UTF-8 JSON Envelope，V1 单消息上限 1 MiB。重复 `operation_id` 只允许返回 Desktop 缓存的终态结果，不能重复执行 click/fill/type 等有副作用操作。每个连接只有一个发送循环，业务线程通过 bounded `Channel<BrowserBridgeEnvelope>` 排队，禁止并发调用 `SendAsync`。

Screenshot、PDF、下载等大结果写入 DataRoot 受控目录，Bridge 只返回 artifact id、相对路径、MIME、长度和 SHA-256；不通过 WebSocket 发送无限大的 Base64。

### 4.3 Core 侧接口

```csharp
public interface IDesktopBrowserCommandBroker
{
    bool IsDesktopConnected { get; }

    Task<BrowserCommandResult> ExecuteAsync(
        BrowserCommand command,
        CancellationToken cancellationToken);

    Task CancelAsync(Guid operationId, CancellationToken cancellationToken);
}

public sealed class RemoteBrowserRuntime : IBrowserRuntime
{
    // 把 Abstractions 调用映射为 BrowserCommand；不引用 WebView2/WPF。
}
```

Console 模式没有已连接 Desktop 时，不注册可执行的 Browser Tools；调用内部代理必须返回 `browser_not_available`，不能等待到 HTTP 超时。

### 4.4 Desktop 侧接口

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

public interface IBrowserWorkspaceController : IAsyncDisposable
{
    IReadOnlyList<BrowserTabViewModel> Tabs { get; }
    PageId? ActivePageId { get; }

    Task<PageId> CreatePageAsync(PageCreateOptions options, CancellationToken ct);
    Task ActivateAsync(PageId pageId, CancellationToken ct);
    Task CloseAsync(PageId pageId, CancellationToken ct);
    Task SetUserTakeoverAsync(bool enabled, CancellationToken ct);
    Task SetDetachedAsync(bool detached, CancellationToken ct);
}
```

`BrowserBridgeCommandDispatcher` 只负责查找 Runtime/Context/Page、派发和转换稳定错误；所有 WebView2 调用仍必须经过 `IWebView2UiDispatcher`，禁止在 WebSocket 接收循环直接访问控件。

## 5. 运行中心与开发脚本边界

### 5.1 普通用户界面

运行中心必须显示：

- Core 状态、PID、启动时间、运行时长和退出码；
- 实际 Loopback 地址和 `/health/ready`；
- Core 可执行文件、DataRoot、工作空间和环境；
- 启动、停止、重启；
- 自动启动、异常退出自动恢复；
- 关闭行为、系统托盘和可选的 Windows 登录后启动；
- 最近 500 行 stdout/stderr，支持复制、导出和打开日志目录；
- Bridge 连接状态和 WebView2 Runtime 状态；
- “生成诊断包”，只收集脱敏状态、版本、配置键名和最近日志，不收集 Token/Cookie/完整表单数据。

### 5.2 进程编排

现有 `CoreProcessSupervisor` 继续负责单次进程启动和关闭；新增 Orchestrator 负责策略，避免把重试逻辑塞进 Supervisor：

```csharp
public interface IDesktopRuntimeOrchestrator : IAsyncDisposable
{
    DesktopRuntimeSnapshot Snapshot { get; }
    event EventHandler<DesktopRuntimeChangedEventArgs>? Changed;

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task RestartAsync(CancellationToken ct);
    Task SetAutoRestartAsync(bool enabled, CancellationToken ct);
}

public sealed record CoreRestartPolicy
{
    public bool Enabled { get; init; } = true;
    public int MaxAttempts { get; init; } = 3;
    public int WindowSeconds { get; init; } = 60;
    public int InitialDelaySeconds { get; init; } = 2;
    public int MaxDelaySeconds { get; init; } = 30;
}
```

```csharp
public enum DesktopCloseBehavior
{
    MinimizeToTray,
    ExitAndStopCore
}
```

异常退出按 2s、4s、8s 上限退避；60 秒内超过 3 次进入 `CoreFailed`，停止自动重启并在 UI 显示明确错误。用户主动停止、配置无效和 DataRoot 缺失不触发自动重启。

Desktop 必须是单实例。第二个进程只激活现有窗口；不得同时启动第二个 Core 或同时占用同一 WebView2 UDF。

### 5.3 `dev-up.py` 与 Desktop 的职责矩阵

| 能力 | `dev-up.py` 源码开发环境 | 最终交付 Desktop |
|---|---:|---:|
| 编译 Backend/Workbench | 是 | 否 |
| 启动前端开发服务器和代理 | 是 | 否 |
| 监督开发用 Codex Service | 是 | 否 |
| `--rebuild`、`--frontend-only`、`--auto-yolo` | 是 | 否 |
| 清理仓库白名单构建/临时输出 | 是 | 否 |
| 启动已发布 Core | 否 | 是 |
| Core 启动/停止/重启 | 仅管理 dev-up 创建的开发 Core | 仅管理 Desktop 创建的产品 Core |
| 健康、状态和运行日志 | 开发栈 | 产品 Core |
| Workbench Bootstrap | 通过开发 Web 入口 | 内嵌 Workbench |
| DataRoot 存储统计和旧日志清理 | 否 | 是 |

两者可以提供相似的启停按钮或命令，但进程所有权严格隔离。Desktop 不读取 `tmp/dev/*.pid`，`dev-up.py` 也不查找或终止 Desktop 的动态端口子进程。最终发布 smoke 必须在没有 Python、Node 和源码仓库的干净环境运行。

## 6. 存储空间页面

### 6.1 页面布局

参考微信“存储空间”信息层级，但区分 Pudding 数据与整个磁盘：

```text
存储空间
Pudding 数据 2.1 GB                    [重新扫描]
████ Pudding 数据  ███████ 其他磁盘数据  ░░░ 可用空间
数据根目录 D:\data                     磁盘 D: 458 GB / 可用 120 GB

日志                         187 MB     [清理]
数据库与索引                 1.8 GB     [管理]
会话、Agent 与记忆            220 MB     [管理]
Browser 数据与缓存            96 MB      [管理]
附件与下载                    40 MB      [管理]
备份                          0 KB       [管理]
异常开发产物                  580 MB     [打开目录]
其他                          18 MB      [打开目录]
```

“管理”在 V1 只展开明细或打开目录，不执行删除。只有日志行显示“清理”。

### 6.2 分类规则

分类必须 first-match 且不重复计数：

| 分类 | DataRoot 相对路径 |
|---|---|
| Logs | `logs/**` |
| DatabaseAndIndex | `databases/**`, `fulltext-index/**` |
| ConversationAndMemory | `sessions/**`, `agents/**`, `memory/**`, `workspaces/**` |
| AssetsAndDownloads | `assets/**`, `browser/downloads/**`, `browser/screenshots/**`, `browser/traces/**` |
| Browser | `browser/**`, `channels/*/runtime/webview2/**` |
| Backups | `backups/**` |
| Configuration | `config/**`, `agent-templates/**`, `channels/**` 中未被前述规则命中的文件 |
| UnexpectedBuildOutput | `build-validation/**`, `codex-test-results/**` 等已知开发输出污染 |
| Temporary | `temp/**`, `tmp/**` |
| Other | 未分类文件 |

Browser 的 downloads/screenshots/traces 必须在 Browser UDF 规则之前匹配，避免进入 Browser 通用分类。`UnexpectedBuildOutput` 只提示“该目录不应位于 DataRoot”，不能混入普通缓存一键清理。

### 6.3 统计模型与接口

```csharp
public sealed record StorageSnapshot
{
    public required string DataRoot { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required long LogicalBytes { get; init; }
    public long? AllocatedBytes { get; init; }
    public required long DriveTotalBytes { get; init; }
    public required long DriveFreeBytes { get; init; }
    public required IReadOnlyList<StorageCategorySnapshot> Categories { get; init; }
    public required IReadOnlyList<StorageScanWarning> Warnings { get; init; }
}

public interface IStorageAnalysisService
{
    Task<StorageSnapshot> AnalyzeAsync(
        string dataRoot,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken);
}
```

统计在线程池执行，UI 最多每 200ms 更新一次进度。枚举不跟随 Symbolic Link/Junction/Reparse Point；无权访问、文件消失和扫描期间变化作为 Warning 返回，不让页面崩溃。

`LogicalBytes` 使用文件长度。NTFS 可取得 allocated size 时填充 `AllocatedBytes`，否则 UI 明确显示“文件逻辑大小”。磁盘总量/可用量使用 `DriveInfo`，不能把 Pudding 的逻辑大小冒充为精确物理占用。

### 6.4 一天前日志清理

```csharp
public interface ILogRetentionService
{
    Task<LogCleanupPreview> PreviewAsync(
        string dataRoot,
        TimeSpan retention,
        CancellationToken cancellationToken);

    Task<LogCleanupResult> ExecuteAsync(
        LogCleanupPreview preview,
        IProgress<LogCleanupProgress>? progress,
        CancellationToken cancellationToken);
}
```

固定规则：

1. `retention` 的 V1 默认值和最小值都是 24 小时；
2. 只扫描规范化后的 `<DataRoot>/logs`；
3. DataRoot 不能是盘符根目录，logs 不能是链接或 Reparse Point；
4. 只允许 `.log`、`.jsonl`、`.txt`、`.gz`、`.zip`；
5. Preview 保存每个候选项的规范化路径、长度、`LastWriteTimeUtc` 和文件标识；
6. UI 显示文件数、预计释放空间、最早/最晚时间和按子目录分组；
7. 用户二次确认后执行；
8. 删除前重新读取签名，已变化、已消失、晚于 cutoff 或越界的文件跳过；
9. 单文件失败不终止整个批次，结果列出 Deleted/Skipped/Failed；
10. 只删除 logs 下清理后为空的真实目录，不删除 logs 根目录；
11. 完成后立即重新统计 StorageSnapshot；
12. 不执行 SQLite checkpoint/VACUUM，不清理 Browser UDF，不调用递归通用删除命令。

该实现等价迁移 `D:\data\clear.py` 已验证的安全策略，但产品运行不依赖 Python 或 `uv`。

## 7. 文件与类拆分

### 7.1 Browser Workspace/Bridge

新增 1 个共享项目和以下主要源文件；以职责边界为准，不再以固定文件数量作为验收条件：

```text
PuddingBrowser.Protocol/
  PuddingBrowser.Protocol.csproj
  BrowserBridgeProtocol.cs
  BrowserBridgeEnvelope.cs
  BrowserBridgeMessages.cs
  BrowserBridgeJsonSerializerContext.cs
  BrowserBridgeErrorCodes.cs

PuddingHost/BrowserBridge/
  DesktopBrowserBridgeWebSocketEndpoint.cs
  DesktopBrowserCommandBroker.cs
  DesktopBrowserConnectionRegistry.cs
  DesktopBrowserConnection.cs
  RemoteBrowserRuntime.cs
  RemoteBrowserContext.cs
  RemoteBrowserPage.cs

PuddingDesktop/Browser/
  DesktopBrowserBridgeClient.cs
  BrowserBridgeCommandDispatcher.cs
  BrowserOperationResultCache.cs
  BrowserWorkspaceController.cs
  BrowserWorkspaceViewModel.cs
  BrowserTabViewModel.cs
  AgentBrowserActivityViewModel.cs

PuddingDesktop/Views/
  BrowserWorkspaceView.xaml
  BrowserWorkspaceView.xaml.cs
```

同时实现现有 `WebView2BrowserRuntime`、`WpfBrowserSurfaceHost`，并修改 `MainWindow`、`DesktopApplicationCoordinator` 和项目引用。首批不实现独立 `BrowserWindow.xaml(.cs)` 的 Surface 转移，待双标签主窗口闭环稳定后再进入 Phase 2A-2。

### 7.2 运行中心

新增 14 个主要源文件：

```text
PuddingDesktop/Runtime/
  IDesktopRuntimeOrchestrator.cs
  DesktopRuntimeOrchestrator.cs
  DesktopRuntimeSnapshot.cs
  CoreRestartPolicy.cs
  CoreRestartAttemptWindow.cs
  DesktopSingleInstanceService.cs
  DesktopBackgroundModeService.cs
  DesktopTrayIconService.cs
  AutoStartRegistrationService.cs
  IDiagnosticBundleService.cs
  DiagnosticBundleService.cs

PuddingDesktop/ViewModels/RuntimeCenterViewModel.cs
PuddingDesktop/Views/RuntimeCenterView.xaml(.cs)
```

`CoreStatusView` 在 RuntimeCenter 验收后删除，不保留两套状态页兼容层。

### 7.3 Storage

新增 12 个主要源文件：

```text
PuddingDesktop/Storage/
  StorageModels.cs
  StorageCategoryCatalog.cs
  IDataRootSafetyValidator.cs
  DataRootSafetyValidator.cs
  IStorageAnalysisService.cs
  StorageAnalysisService.cs
  ILogRetentionService.cs
  LogRetentionService.cs
  StorageSizeFormatter.cs

PuddingDesktop/ViewModels/StorageViewModel.cs
PuddingDesktop/Views/StorageView.xaml
PuddingDesktop/Views/StorageView.xaml.cs
```

## 8. 实施顺序

### Phase 1B-R：运行中心

状态：**已完成（2026-08-02）**。

1. 增加 RuntimeOrchestrator 和自动重启熔断；
2. 把 CoreStatus 页面演进为 RuntimeCenter；
3. 增加单实例激活、系统托盘、后台运行、日志查看和诊断包；
4. 保持现有 Start/Stop/Restart 行为回归通过。

验收：异常退出可在策略内恢复，快速失败会熔断；用户主动停止不会自动拉起；关闭主窗口默认进入托盘且 Core 继续健康；明确退出后 Core 和 WebView2 子进程全部释放；第二实例只激活主窗口。

交付证据：

- `CoreProcessSupervisor` 保持单进程启动/停止职责，`DesktopRuntimeOrchestrator` 独立承载异常恢复、2s/4s/8s 退避、60 秒 3 次窗口和熔断；
- 主导航中的旧 `CoreStatusView` 已由 Windows 11 卡片式 Runtime Center 替代，展示当前/最近 PID、启动时间、运行时长、健康、动态 Loopback、最近退出码、恢复状态、环境和最近 500 行输出；
- Desktop 使用本地命名 `Semaphore` 保证单实例，并通过仅当前用户可访问的 Named Pipe 激活主窗口；默认关闭行为为隐藏到系统托盘，托盘提供打开、启动、停止、重启和明确退出；
- 登录后启动只在用户保存设置时修改 HKCU Run；Desktop 启动失败、DataRoot 缺失或 Core 失败都不阻塞设置与运行中心；
- 诊断包只在用户点击后生成，包含脱敏运行快照、最近日志和配置键名，不复制 Token、Cookie、Authorization 或配置值；
- `PuddingDesktop.Tests` 当前 62/62 通过，覆盖退避/熔断、主动停止、意外恢复、单实例激活、后台模式、诊断脱敏和配置保留；
- Release 发布包生成成功；系统 Temp 隔离 smoke 验证了窗口关闭后后台存活、第二实例激活现有窗口、强制终止 Core 后自动换 PID 恢复、用户点击停止后 Core 持续保持停止，以及 `ExitAndStopCore` 关闭后 Desktop/Core/WebView2 三个已记录 PID 全部释放，未修改 `D:\data`。

### Phase 1B-S：存储空间

状态：**已完成（2026-08-02）**。

1. 完成安全校验、分类器和只读统计；
2. 完成微信式 Storage 页面；
3. 完成日志 Preview/Confirm/Delete/Rescan；
4. 使用隔离临时 DataRoot 测试，不在测试中修改 `D:\data`。

验收：分类总量不重复；Junction 不跟随；一天内日志保留；变化文件跳过；非日志文件不删除。

交付证据：

- Storage 的 9 个模型/服务文件、ViewModel、View 和 MainWindow 导航已经落地；
- `PuddingDesktop.Tests` 新增分类、安全根目录、扫描、预览和执行 5 组测试；Storage 完成时 49/49 通过，合并 Runtime Center 测试后当前为 62/62；
- Release 发布包生成成功，包含 Desktop 和嵌套 `core/`；
- 使用系统 Temp 下的隔离 DataRoot 完成真实 WPF/Core/WebView2/Storage 视觉 smoke，未触碰 `D:\data`；
- 清理 UI 使用 Preview → 内联确认 → 重校验 → 逐文件删除 → 重扫；零候选时不进入确认态。

### Phase 2A：Browser Workspace 与 Bridge

1. 新增 Protocol 和 Core/Desktop 双向连接；
2. 实现 Browser Workspace、Tab Strip、Toolbar、Activity Pane；
3. 实现 Surface Host 与 Runtime 的 Context/Page/Navigation 基线；
4. 增加独立窗口 Surface 转移；
5. 再进入 DOM、Input、CDP、Network 等 Driver 能力。

验收：Agent 可创建两个 Tab，分别导航、激活、刷新、后退、前进和关闭；用户可以看见 Agent 活动并暂停/接管；Core 重启后 Bridge 可恢复且旧命令不会重放。

## 9. 测试要求

### 9.1 Desktop 单元测试

- `CoreRestartAttemptWindowTests`；
- `DesktopRuntimeOrchestratorTests`；
- `DesktopSingleInstanceServiceTests`；
- `StorageCategoryCatalogTests`；
- `DataRootSafetyValidatorTests`；
- `StorageAnalysisServiceTests`；
- `LogRetentionServicePreviewTests`；
- `LogRetentionServiceExecutionTests`；
- `BrowserWorkspaceControllerTests`；
- `BrowserBridgeCommandDispatcherTests`。

### 9.2 Bridge 集成测试

- 未带 ControlToken 拒绝；
- 非 Loopback 连接拒绝；
- Hello/Heartbeat/Reconnect；
- 命令 correlation 和 deadline；
- cancellation；
- 重复 operation id 不重复执行；
- Desktop 断开时未完成命令稳定失败；
- Screenshot artifact 不走大 Base64。

### 9.3 UI Smoke

- Windows Light/Dark、100%/150%/200% DPI；
- 最小窗口宽度下 Tab/地址栏/Activity Pane 可降级；
- Workbench 与 Agent Browser UDF 隔离；
- 两个 Tab、popup、新建/关闭/切换；
- BrowserWindow 移出和移回；
- Core 停止时 Browser 页面显示可恢复状态；
- Storage 扫描期间切页/取消不崩溃；
- 清理确认框显示预计文件数和字节数。

## 10. 下一步工作指令

下一开发批次实施 **Phase 2A Browser Workspace 与 Core/Desktop Bridge**，继续保留 ASP.NET Core 业务核心，也不修改 `dev-up.py` 的产品边界：

1. 新建 `PuddingBrowser.Protocol`，定义 WebSocket JSON envelope、命令/结果/事件、稳定错误码、deadline 和 operation id 幂等语义；
2. 在 Core 实现 `DesktopBrowserCommandBroker`、连接注册表和认证 WebSocket Endpoint；Desktop 未连接时 Browser Tool 必须立即返回 `browser_not_available`；
3. 在 Desktop 实现 Bridge Client、断线重连和 Dispatcher，所有 WebView2 调用统一切换到 WPF UI Dispatcher，Core 重启时终结旧命令且不得重放副作用；
4. 新增 Agent Browser 导航项、Browser Workspace、Tab Strip、地址栏、后退/前进/刷新/停止、Activity Pane 和暂停/接管/继续状态；WorkBench 继续使用自己的 UDF；
5. 先完成 Context/Page/Navigation 的两个标签页闭环，再进入 DOM/Input/CDP/Network 等完整 Driver；不为抖音写入底层特例；
6. 新增 Protocol、Broker、Dispatcher、重连和两个 Tab 的定向测试，并使用系统 Temp 下的隔离 DesktopHome/DataRoot/UDF 做真实窗口 smoke。

Phase 2A-1 初始任务包见 [70](70Phase2A-1通用BrowserBridge与双标签工作区开发工作指令.md)，两轮收口见 [71](71Phase2A-1验收补丁真实BrowserWorkspace与Bridge可靠性工作指令.md)、[72](72Phase2A-1最终验收修复Bridge握手Surface切换与UISmoke工作指令.md)，最终测试、发布与可见 smoke 证据见 [73](73Phase2A-1验收证据收口与Phase2A-2准入工作指令.md)。Phase 2A-2 最小 Remote Runtime/Context/Page 与三项 Agent Tools 已 accepted，证据见 [74](74Phase2A-2最小RemoteBrowser与AgentTools实施验收报告.md)；Phase 2A-3 Snapshot/Locator/Interact/Wait 契约与自动验收见 [75](75Phase2A-3SnapshotLocatorInteractWait开发工作指令.md)、[76](76Phase2A-3通用WebView2页面操作实施验收报告.md)。下一准入项是真实 DeepSeek Agent 可见 smoke，通过后再进入 Douyin Adapter。

`dev-up.py` 继续只承担源码开发环境；最终产品进程主管始终是 `PuddingDesktop.exe`。
