# Pudding.Contracts 与 Desktop gRPC 能力通道技术方案

- 日期：2026-10-01。
- 状态：Proposed；**2026-10-01 更新：切片 A+B 已实施**（Contracts / Rpc.Protocol / DesktopConnection / IPC 技术探针，见[实施报告](../Reports/Desktop-Contracts-Rpc-SliceABC-2026-10-01.md)）；切片 C–F **未实施**，未接入任何宿主，旧 Bridge 仍在运行。
- 基线：[Shell / Web / 独立 Core ADR](ADR-Desktop-Shell-WebUI-Separate-Core-2026-09-29.md)、[恢复报告](../Reports/Desktop-Shell-Recovery-2026-09-29.md)、[组件化交付规程](../Conventions/组件化交付规程.md)。

## 1. 建议决策

新增 `Pudding.Contracts`，作为平台与传输无关的接口、DTO、消息定义叶程序集。业务访问继续使用既有 HTTP API 和事件通道；Desktop 原生能力逐步改为 Desktop 主动连接 Core 的 gRPC 双向流。Core 只托管一个 Capability 服务，Desktop 不托管 Kestrel，不引用 ASP.NET Core，也不加载进程内 PuddingHost。

保留 WinUI 3 Shell、Web 工作台、独立 Core 子进程及现有启动监督。gRPC 是能力传输方案，不是聊天、记忆、配置、工具业务架构的重写，也不承诺通过换协议获得性能收益。macOS/Linux 只预留协议与适配边界，本轮不选择或实现其 UI 技术。

初稿中的 `DesktopRpc` 应理解为 Desktop 内部能力处理器，而非需要另开监听端口的 gRPC 服务。RPC 发起方向与 TCP/IPC 连接发起方向可以不同：Desktop 发起双向流，Core 在响应流发送命令，Desktop 在请求流回传结果。

```text
Web 工作台 / Human / 第三方 ── HTTP + 既有事件通道 ──> Core
Desktop 的 Shell 业务客户端 ── HTTP ─────────────────> Core

DesktopConnection ── 发起 Connect 双向流 ────────────> Core Capability 服务
                  <── Command / Cancel ────────────── Core Broker
                  ── Hello / Result / Event ────────>

Agent Tool → IPuddingDesktopApi → Core Capability Broker
          → gRPC 流 → DesktopCapabilityHost → DesktopService
          → UI 调度抽象 → DispatcherQueue → UI Thread → WebView2 / WinUI
```

## 2. 当前代码与可复用资产

| 当前资产 | 源码位置 | 本方案处理 |
|---|---|---|
| 纯 JSON 浏览器协议 | `Source/PuddingBrowser.Protocol` | 保留当前合同；不立即整体迁入新 Contracts |
| Core 浏览器远程代理与命令 Broker | `Source/PuddingHost/BrowserBridge` | 保留浏览器业务语义，抽出传输接缝 |
| 认证 WebSocket 入口 | `DesktopBrowserBridgeEndpointExtensions.cs`、协议路径 `/desktop/browser-bridge` | 在 gRPC 达到行为等价后逐步退役 |
| Desktop Bridge 与命令分发 | `Source/PuddingDesktop.WpfArchive/Browser`，由 WinUI 工程链接编译 | 复用准入、暂停、接管、截止时间和结果缓存，不加载 WPF UI |
| WinUI 浏览器驱动 | `Source/PuddingBrowser.WinUI/WinUiBrowserSurfaceHost.cs` | 已有 `WinUiDispatcher` 与 `IWebView2UiDispatcher`；扩展服务接缝，不重新实现驱动 |
| Core 进程监督 | `Source/PuddingDesktop/Hosting/DesktopApplicationCoordinator.cs` 与链接启动器代码 | 启动器仍独占生命周期、DataRoot 与租约职责 |

现有 Bridge 已有关联 ID、连接 Generation、取消和心跳；Core 出站队列上限为 128。当前协议限制单消息 1 MiB，默认心跳 15 秒、超时 45 秒。这些是迁移基线，不是新通道已实现的参数。现有 Snapshot 的 `PageVersion` 与交互后必须重新获取状态的约束必须保留。

Desktop csproj 目前允许引用 Foundation、PuddingCore 与浏览器组件，并用 MSBuild 白名单限制其他工程引用；其中 PuddingCore 作为既有配置合同依赖的现状，不应表述为已经完全无 Core 引用。新 Contracts 不意味着搬迁全部 PuddingCore DTO；存疑类型留在原归属，后续按实际消费逐项拆分。

## 3. 程序集与契约真源

统一名称采用用户提出的 **Pudding.Contracts**，不另建职责重复的 `PuddingAgent.Contracts`。

| 拟建组件 | 内容与依赖 |
|---|---|
| `Source/Pudding.Contracts` | `net10.0`、仅 BCL；接口、不可变 DTO、错误枚举、能力标识；ProjectReference=0；无 UI、HTTP、gRPC、Protobuf、DI、数据库依赖 |
| `Source/Pudding.Rpc.Protocol` | `Protos/pudding.proto` 与生成的消息、客户端、服务基类；使用 Google.Protobuf、Grpc.Core.Api 与构建期 Grpc.Tools；不包含连接、业务或 UI 实现 |
| `Source/Pudding.DesktopConnection` | Grpc.Net.Client、连接与单写循环、命令关联、取消、背压；依赖 Contracts 与 Rpc.Protocol；无 WinUI/ASP.NET Core |
| Core 侧适配器 | 服务端 Connect、注册表、Broker、授权、DTO 映射，最终由 PuddingHost 组合根装配 |
| Desktop 侧适配器 | DesktopCapabilityHost / DesktopService / WinUI 调度实现；最终由 PuddingDesktop 装配 |

依赖箭头表示“引用”：消费者 → Contracts；传输适配器 → Rpc.Protocol。Contracts 不引用 Rpc.Protocol，也不引用任何调用方。Core 不引用 Desktop 或 WinUI，Desktop 不引用 Host/Runtime/Platform。接入时更新 Shell 引用白名单，允许的连接库还需校验完整依赖闭包，防止间接引入业务 Host。

**两类真源必须区分**：C# 接口与领域 DTO 的真源为 Contracts；线上字段、编号、service 定义的真源为 proto。proto 只生成 wire 类型和 gRPC 桩，不生成手写的领域接口。映射是传输适配器职责，并通过 round-trip 测试保证一致；禁止分别手工维护一套 wire C# 消息与 proto。生成代码放构建输出，不手改、不提交临时生成产物。

初期不移动既有 Browser.Protocol 的类型或改变命名空间；浏览器能力采用显式转换器衔接。完成等价迁移后再决定是否合并协议，避免公共 Contracts 成为所有业务模型的大杂烩。

## 4. 接口形态与能力范围

建议聚合接口按能力分组，所有异步调用支持取消和目标身份。以下是形态示例，类型细节在 S1 时固定：

```csharp
public interface IPuddingDesktopApi
{
    IPuddingDesktopWebViewApi WebView { get; }
    IPuddingDesktopShellApi Shell { get; }
}

public interface IPuddingDesktopWebViewApi
{
    Task<NavigateResult> NavigateAsync(
        DesktopCallContext context, NavigateRequest request,
        CancellationToken cancellationToken = default);
    Task<JavascriptResult> ExecuteJavascriptAsync(
        DesktopCallContext context, JavascriptRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPuddingDesktopShellApi
{
    Task<NotificationResult> ShowNotificationAsync(
        DesktopCallContext context, NotificationRequest request,
        CancellationToken cancellationToken = default);
}
```

`DesktopCallContext` 带目标 DesktopId、OperationId、DeadlineUtc 和业务关联 ID；身份与权限由 Core 的可信运行上下文产生，不信任模型填写的 AgentId/WorkspaceId。WebView 请求必须指定 ContextId/PageId，不能通过“当前激活 Tab”隐式定位。网页导航返回已接受或已完成的明确状态；页面加载完成通过事件/等待能力表达，不能把 `Navigate` 调用返回当作 DOM 已就绪。JavascriptResult 明确 JSON 返回语义与大小限制，避免字符串的二次 JSON 编码。

首个切片为既有浏览器能力的行为等价迁移，随后增加通知与 Shell 状态，再增加对话框、FilePicker、Clipboard。ShowDialog/FilePicker 必须用结构化参数与类型化结果，包含用户取消；文件选择不代表 Core 自动有权访问该路径，远端 Core 场景用文件授权句柄与显式传输，不能返回本机路径就认为 NUC 可读取。

通用 `ShellMessage` 若保留，必须是白名单判别联合；禁止字符串命令名加任意 JSON 演化成无授权的万能调用。任意脚本只限获授权的 Agent 浏览器目标；可信 Workbench、登录态页面和产物预览有独立权限及环境隔离，不能通过此 API 注入工作台。

`IPuddingAgentApi` 可作为普通业务 HTTP 客户端的独立端口，但本轮不新增 AgentRpc 聊天服务。其 Request/Response 应复用现有业务会话与流式语义，`SendMessageAsync` 返回接收/执行标识而非必然等待完整模型回复；取消需授权到具体执行，GetStatus 区分 Core 健康与任务状态。现有 Web 工作台继续直接使用既有 Web API，不绕行 Shell 的 HttpClient。文中 `/api/chat/message` 只作初稿示意，实施必须按实际控制器路由接线。

## 5. 双向流协议

proto 建议采用版本化 package `pudding.capability.v1`：

```proto
syntax = "proto3";
package pudding.capability.v1;
service DesktopCapability {
  rpc Connect(stream DesktopFrame) returns (stream CoreFrame);
}
// DesktopFrame: hello / result / event / heartbeat_ack
// CoreFrame: hello_ack / command / cancel / heartbeat
// 各帧用 oneof 明确类型；完整字段在独立组件切片定义。
```

握手包含 DesktopId、进程实例 ID、支持版本范围、能力集合与各能力版本；Core 返回 ConnectionId、Generation、实际协商能力及限制。认证先于握手，握手成功才开放调用。默认本地产品只绑定当前启动器管理的 Desktop；远端多 Desktop 由可信会话显式选择，禁止随机挑选在线实例。

命令包含 OperationId、ConnectionGeneration、能力类型、目标、DeadlineUtc、TraceId 与类型化 payload；Result 包含相同身份、终态和类型化输出。每条方向一个 reader、一个 writer，其他线程通过有界 Channel 汇聚，禁止多个调用并发写同一个流。每个 OperationId 建待完成表；旧连接结果不能完成新连接命令。

长连接的 gRPC deadline 不代替单操作 deadline。队列、UI 执行与结果回传均计入操作期限。建议起点沿用 128 帧、1 MiB 帧体、15/45 秒心跳，但另加每连接排队字节预算和在途操作上限，参数经压测确认。队列满时明确等待至 deadline 或返回 ResourceExhausted，不静默丢命令；状态事件可合并，终态结果不可丢。大 HTML/截图走限额文件传输或分块专用能力，不无界塞进消息。

断连时完成所有 pending 为 Disconnected/OutcomeUnknown，释放队列和取消注册；指数退避加 jitter 重连，重新握手并增加 Generation。Core 重启还需识别 Core 实例 ID；旧 PageId/Snapshot ref 作失效处理，需要重新发现页面。禁止在新连接自动重放脚本、通知、剪贴板写入等副作用。

同连接同 OperationId 的重复命令复用已有进行中任务或终态缓存；相同 ID 不同 payload 拒绝。缓存需限定容量、TTL，并与 Generation/目标绑定。跨进程崩溃不承诺 exactly-once；执行完成但结果丢失返回 OutcomeUnknown，由调用者查询或用户处理。取消是尽力而为，不能撤销已执行 JS；超过期限后阻止未开始操作，执行中返回“可能已产生副作用”的明确状态。

领域错误至少包括 UnsupportedCapability、NotConnected、Unauthorized、InvalidTarget、PageVersionMismatch、Paused、UserTakeover、DeadlineExceeded、Cancelled、OutcomeUnknown、ResourceExhausted、UiUnavailable。流本身故障使用 gRPC status；单次业务失败用结果错误，避免一条命令失败关闭整个通道。

## 6. DesktopService 与 UI 线程边界

DesktopCapabilityHost 负责解码、协议与权限检查及调度到服务；DesktopService 负责目标校验、准入、生命周期与调用 UI 调度接口。所有触及 WebView2、WinUI、窗口、剪贴板和 Picker 的动作最终进入所属窗口的 DispatcherQueue。网络读写、序列化、重连与审计不占用 UI 线程。

复用 `WinUiDispatcher` 的 TaskCompletionSource 和异步排队形态；新增服务需保证异常/取消总能完成任务、队列拒绝返回 UiUnavailable、窗口退出取消 pending。入队后再次检查取消、deadline、页面版本与权限，解决排队期间目标关闭/用户接管的竞态。现有 Dispatcher 的取消仅在部分阶段检查，不能直接声称已满足完整取消语义。

禁止 `.Wait()` / `.Result`；UI delegate 内执行控件调用并异步 await，不能把返回的 WebView2 对象交给后台线程。对同 Page 的变更串行化；只读并发与跨 Page 并发由能力策略决定。对话框/Picker 单窗口限一个，使用正确窗口 owner，等待用户期间不阻塞流处理。WinUI DispatcherQueue 留在实现程序集，Contracts 只定义平台无关服务接口。

## 7. 传输、配置与安全

| 场景 | 推荐传输与边界 |
|---|---|
| Windows 本机产品 | Core Kestrel Named Pipe + HTTP/2；Desktop GrpcChannel 用 SocketsHttpHandler.ConnectCallback 连接管道；管道 ACL 仅允许运行用户/必要身份 |
| macOS/Linux 后续 | Core UDS + HTTP/2；目录和 socket 权限限制；仅作为后续适配目标 |
| 远程 Core / NUC | 独立 TLS HTTP/2 endpoint，认证与 Desktop 绑定；需单独实现远端连接模式，当前子进程监督不自动支持远端 |
| 调试备用 | 显式启用的 Loopback HTTP/2 endpoint；不因 IPC 失败自动切到公网 TCP |

IPC 是 HTTP/2 的底层传输，不是把 gRPC 改成裸管道自定义协议。微软提供 Named Pipe/UDS 的 Kestrel 与 ConnectCallback 模式，见 [gRPC IPC 文档](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess?view=aspnetcore-10.0)。无 TLS 端点必须明确 HTTP/2，不能假定现有 HTTP/1.1 REST 动态端口可直接承载 gRPC；远程 TLS 端点由 ALPN 协商 HTTP/2，部署还需验证代理双向流支持。

沿用 DesktopHome/desktop.json 与 DataRoot/config/system.json；Desktop 保存连接模式/布局/关闭策略，Core 保存服务端能力通道限制和策略。启动就绪协议增加可选 capability endpoint 描述与实例 ID，仍保留 REST endpoint。端点发现不包含凭据，不让网页决定管道名或远端地址；管道名按用户及产品实例隔离，避免不同 DataRoot 串接。

已有 ControlToken 不进入环境变量、日志、URL、UI 或诊断包。迁移期仅允许本机受限 IPC 中受控复用现有认证校验；权限应再细分到能力和目标。远端采用独立可撤销凭据并走 TLS，不把本地 ControlToken 直接扩展为网络万能权限；标准 gRPC 凭据通常要求安全连接，IPC 的凭据注入需单独验证，不能复制无 TLS TCP 的不安全配置。见 [gRPC 认证文档](https://learn.microsoft.com/en-us/aspnet/core/grpc/authn-and-authz?view=aspnetcore-10.0)。

能力调用继承现有 Tool Runtime 准入、用户授权、暂停和接管语义，桌面端再次校验允许目标。RPC 可达不等于获得桌面操作授权。普通网站与第三方 HTTP 客户端不能建立能力通道。审计记录 TraceId、OperationId、Generation、能力、排队/执行耗时、终态；不记录脚本正文、剪贴板内容、页面敏感数据或 Token。

## 8. 分阶段实施与回退

| 切片 | 交付及门禁 |
|---|---|
| A：Contracts | 先 S1–S4：独立工程、仅引用该组件的测试、边界断言、组件 code_map；冻结首批接口与错误语义。之后 S5 才登记 slnx/引用，首次接入不改变 Bridge 行为 |
| B：Protocol / Connection | 独立 proto 生成与映射测试、假服务端的双向流/取消/背压/旧 Generation 测试；完成 IPC 技术探针，不接产品 UI；各组件依次通过 S1–S4 |
| C：DesktopService | 用假 UI 调度器验证目标、准入、关闭竞态；WinUI 定向验证线程访问；随后 S5 装配新 Host，保留旧 Bridge 可选择 |
| D：浏览器等价接入 | RemoteBrowserRuntime 与既有 Browser Tools 接新 Broker；保留 PageVersion、七项工具、暂停/接管、事件与右侧工具 Tab 语义；对同一 Desktop 只允许一个活动能力传输 |
| E：Shell 能力 | 通知/状态先行，再 Dialog/Clipboard/Picker；逐能力声明支持与授权，不默认开放全部功能 |
| F：默认切换与退役 | 独立测试、宿主组合测试、全工程构建与外部生命周期回归通过后切默认 gRPC；移除旧 WebSocket 接线及临时选择开关，不长期维护双套实现 |

迁移开关在启动时选择传输，不做单命令故障后自动跨传输重试，避免副作用重复。回退需显式断开原连接并完成 pending，再重启选择旧 Bridge；UI 与 HTTP API 继续复用。首个实施范围建议 A+B，本次文档交付不执行这些切片。

任何类型迁移、命名空间变化或工程拆分都要求覆盖全部工程构建。新增组件 S5 前不改宿主、DI 或解决方案。测试与构建使用 `--artifacts-path temp/build/recovery`，先 restore/build 再同目录 `--no-restore`；Desktop build/test/publish 串行，独立 DataRoot/DesktopHome 和测试输出在 `temp/test-out`，不在 D:\data 试跑、不读取生产模型 Secret，不删除锁文件。

## 9. 验收清单与尚待确认事项

1. 编译边界：Contracts 零项目/外部包引用，完整闭包无 WinUI/WebView2/Host；加入禁止依赖使门禁取红。Rpc.Protocol 无业务/UI 实现，生成类型映射与版本测试通过。
2. 协议：握手拒绝不支持版本，缺失能力可发现；并发关联正确，乱序结果正确；断连、取消、deadline、满队列、重复 ID、旧 Generation 与 Core 重启可确定结束，不泄漏 pending。
3. UI：网络线程不能访问控件；排队后关闭窗口、页面导航变化、用户接管、UI 队列拒绝与对话框取消均可结束；Snapshot 旧 PageVersion 被拒绝。
4. 安全：无凭据/错误凭据拒绝；未授权能力和跨 Desktop/Workspace/可信 Workbench 目标拒绝；普通网页不能调用 Shell；日志与诊断包不泄密。
5. 产品：HTTP/Web 既有聊天与事件行为回归；隔离外部控制器验证独立 PID、Core 故障 Shell 存活、重连、重启、托盘隐藏继续工作、退出停止 Core、端点释放与数据租约。内部 Agent 仅验证新构建已部署后的功能，不验收承载自身的生命周期。

实施前需技术探针确认：选用包版本与锁定方式、产品 Named Pipe ACL/用户身份、启动就绪描述的演进、当前 Browser 命令到 proto 的完整映射、消息字节预算与图片/HTML传输、跨平台 UI 和远端部署需求。这些未决项不阻碍先建设纯 Contracts；未测量前不宣称 gRPC 比当前 Bridge 更快。

本次源码核查与外部文档核实只支持方案合理性；未执行产品构建、gRPC 运行或 UI smoke，不把规划写成已完成实现。

## 10. 实施进展（2026-10-01）

| 切片 | 状态 | 证据 |
|---|---|---|
| A：Contracts | ✅ S1–S4 + S5（slnx 登记） | `Source/Pudding.Contracts`（BCL-only，编译期边界）+ `Pudding.ContractsTests` 58/58；边界取红实测 |
| B：Protocol / Connection | ✅ S1–S4 + S5（slnx 登记） | `Source/Pudding.Rpc.Protocol` + 17/17；`Source/Pudding.DesktopConnection` + 74/74；`Source/Pudding.Rpc.IpcProbe` 13/13（Named Pipe + h2c 真实端点） |
| C：DesktopService | 🟡 主体完成（含宿主组合与 WinUI 适配器），产品内装配未做 | `Source/Pudding.DesktopService`（目标校验/准入/入队后竞态复检/UI 调度边界/**DesktopCapabilityHost 启停与单实例传输**）+ 69/69；`Source/PuddingDesktop.CapabilityHost`（`DispatcherQueue` 适配器，编译期边界）；**在 PuddingDesktop 组合根构造并启动**（C-3）未做 |
| D：浏览器等价接入 | ⛔ 未开始 | 旧 `DesktopBrowserBridgeEndpointExtensions` 与 WebSocket Bridge 未动 |
| E：Shell 能力 | ⛔ 未开始 | 目录已预留 `shell.status/dialog/file_picker/clipboard` 线名，但**尚无 payload 与实现**，不会被声明 |
| F：默认切换与退役 | ⛔ 未开始 | — |

细节、探针原始结论、有意偏差与风险见[实施报告](../Reports/Desktop-Contracts-Rpc-SliceABC-2026-10-01.md)。
