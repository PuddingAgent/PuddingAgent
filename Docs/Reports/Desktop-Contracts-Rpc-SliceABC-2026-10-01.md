# Desktop Contracts / gRPC 能力通道：切片 A+B+C 实施报告

- 日期：2026-10-01。
- 方案：[Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md](../Features/Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md)。
- 范围：方案 §8 的 **A（Contracts）+ B（Protocol / Connection）+ IPC 技术探针**，以及 **C（DesktopService 主体）**。
- 明确未做：**未接入宿主**（不改 `PuddingHost`/`PuddingAgent`/`PuddingDesktop` 组合根与 DI）、**未动旧 WebSocket Bridge**、未实现 Shell 对话框/Picker/剪贴板、未切默认传输。
- 交付提交：`66dd7cf`（Contracts）、`b635ecf`（Rpc.Protocol）、`e041d2e`（执行器接缝联合）、`bd300ba`（DesktopConnection）、`b3fcd4c`（IPC 探针）、`533465a`（UI 接缝 + `RequiresPageTarget`）、`4417bbf`（DesktopService）。

## 1. 交付物

| 组件 | 位置 | 边界（编译期强制） |
|---|---|---|
| 契约叶 | `Source/Pudding.Contracts` | 仅 BCL；Target `EnforceContractsBoundary` 使 `ProjectReference`/`PackageReference` 非空即取红 |
| 协议叶 | `Source/Pudding.Rpc.Protocol` | 只有 proto 与生成类型；Target `EnforceProtocolLeafBoundary` 禁止任何 `ProjectReference` |
| Desktop 适配器 | `Source/Pudding.DesktopConnection` | 只引用 Contracts + Rpc.Protocol + `Grpc.Net.Client`；Target `EnforceConnectionBoundary` 禁止 Host/Runtime/Desktop/Browser 工程与 ASP.NET Core/WinUI/WebView2 包 |
| Desktop 服务 | `Source/Pudding.DesktopService` | 只引用 Contracts + DesktopConnection、**零包引用**；Target `EnforceDesktopServiceBoundary` 同规则（切片 C） |
| 技术探针 | `Source/Pudding.Rpc.IpcProbe` | 探针专用（Kestrel + Grpc.AspNetCore 作为服务端替身）；**无产品消费方** |

测试工程（S2：各自只引用被测组件）：`Source/Pudding.ContractsTests`、`Source/Pudding.Rpc.ProtocolTests`、`Source/Pudding.DesktopConnectionTests`。

## 2. 门禁证据（全部本机实测）

| 门禁 | 命令 | 结果 |
|---|---|---|
| 独立构建 | `dotnet build Source\Pudding.Contracts\|Pudding.Rpc.Protocol\|Pudding.DesktopConnection\|Pudding.Rpc.IpcProbe -c Release` | 每个工程 **0 警告 / 0 错误** |
| 独立测试 | `dotnet test Source\Pudding.ContractsTests` | **58/58 通过**（行覆盖 90.4%） |
| 独立测试 | `dotnet test Source\Pudding.Rpc.ProtocolTests` | **17/17 通过** |
| 独立测试 | `dotnet test Source\Pudding.DesktopConnectionTests` | **74/74 通过** |
| 独立测试 | `dotnet test Source\Pudding.DesktopServiceTests` | **58/58 通过**（切片 C） |
| 边界取红 | 临时给 Contracts 注入 `PackageReference Google.Protobuf` | 构建失败并输出 `EnforceContractsBoundary` 的 BCL-only 错误；移除后 `git hash-object` 逐位复原（`2baf6a4b…`） |
| 真实端点探针 | `dotnet temp\build\recovery\bin\Pudding.Rpc.IpcProbe\release\Pudding.Rpc.IpcProbe.dll` | **13/13 通过，exit 0** |
| 解决方案登记 | `dotnet sln PuddingAgentNetwork.slnx list` / `dotnet restore PuddingAgentNetwork.slnx` | 7 个新工程已登记且可解析 |

> 关于「全解决方案构建」门禁（组件化交付规程 §4.2）：该门禁针对 `git mv`/命名空间根变更/工程拆分的**搬迁类**改动。
> 本次**只新增工程、未移动或改名任何既有类型/工程**，既有工程不可能因此编译失败；因此未触发全解决方案构建
> （也避免与并行 AI 的 Desktop 构建争用 `obj`，以及避免触碰运行中产品的 `wwwroot` 部署路径）。
> 新工程自身已逐一构建/测试通过，slnx 登记已由 `list` + 解决方案 restore 验证。

## 3. 实现要点（与方案的对应）

**切片 A（Contracts）**
- 能力目录是**协商身份**而非权限：线名（`webview.navigate` 等）、版本、`Mutating`/`HasSideEffects`/`RequiresTrustedContext`/`RequiresUserInteraction` 语义集中在一处，契约测试冻结快照。
- 握手协商 `DesktopCapabilityNegotiation.Validate`：Core 只能授予 Desktop 已声明且版本不高于声明的能力；越权授予是协议错误且**不做部分接受**。
- 错误语义：15 个领域码 + 线名 + 默认 `Retryable`/`MayHaveSideEffects`；未知线名 fail closed。`DeadlineExceeded`/`Cancelled` 默认按「尚未开始」取值，执行中由调用方显式标注 `mayHaveSideEffects=true`。
- `CapabilityResult<T>`：不用异常表达业务失败（跨进程必须区分「未执行/可能已生效/可重试」）；`default` 视为未初始化并取红。
- 审计形状即安全边界：`DesktopCapabilityAuditRecord` **没有**脚本正文/URL/剪贴板/Token/页面数据字段，由反射测试断言。

**切片 B（Protocol / Connection）**
- wire 唯一真源是 `Protos/desktop_capability.proto`：`Connect(stream DesktopFrame) → (stream CoreFrame)`；命令 payload 是**白名单 oneof**（禁止「字符串命令名 + 任意 JSON」）；字段号由快照测试冻结（payload 从 10 起编号，1–9 留给身份字段）。
- 连接状态机：单读单写（测试断言同流并发 writer ≤ 1）、握手与世代失效、命令关联与幂等复用、同 ID 不同 payload 拒绝、终态缓存 TTL 过期只回 `outcome_unknown`（**绝不重新执行**）、取消立即回终态、单操作 deadline（排队/执行/回传均计入）、在途上限与 Core 限制取更严格者、字节预算背压（终态不丢、状态事件可丢并计数）、同目标变更串行化、断连确定性结束（未开始 ⇒ `Disconnected`；已开始 ⇒ `OutcomeUnknown`）、每操作恰好一条审计。
- `DesktopConnectionRunner`：指数退避 + jitter 重连；**不跨传输回退、不重放副作用命令**（测试断言新连接上只有握手帧）。
- UI 边界：`IDesktopCapabilityExecutor` 是唯一接缝，只依赖 Contracts；边界测试断言它与其他公共契约**不出现** proto/Grpc/Google.Protobuf/WinUI/ASP.NET Core 类型。

## 3.1 切片 C：DesktopService（准入 · 竞态 · UI 线程边界）

组件 `Source/Pudding.DesktopService`，不依赖任何 UI 类型：UI 动作经 `IDesktopUiDispatcher` 调度、由
`IDesktopUiSurface` 实现方访问 WebView2/窗口/通知。

- **竞态闭环**：同一套 `ValidateAdmission` 在**入队前**与**拿到 UI 线程后**各执行一次。
  测试覆盖：排队期间页面被关闭 ⇒ `invalid_target`；版本被推进 ⇒ `page_version_mismatch`；
  用户接管 ⇒ `user_takeover`；窗口关闭 ⇒ `ui_unavailable`（**不悬挂**）；期限过期 ⇒ `deadline_exceeded`。
  以上五种情况都断言 **surface 从未被调用**。
- **准入策略表**（8 能力 × 3 可信级别，快照断言）：脚本注入仅 `AgentAuthorized`；
  对话框/Picker/剪贴板仅 `Workbench`；**`Workbench` 永不接受脚本注入**（硬不变式 + 断言）。
  与契约层的 `RequiresTrustedContext` 双向自洽断言（该 Traits 的能力绝不允许 `Untrusted`）。
- **目标登记表**：页面版本**只允许前进** —— 回退会让旧 Snapshot/Locator 重新"有效"，测试断言回退被拒。
- **交互状态**：暂停与用户接管是独立轴（接管优先）；变更类能力被拒，只读能力（页面状态）仍可用。
- **副作用标注精确化**：`mayHaveSideEffects` 只在**已进入 surface** 时按能力 Traits 标注；
  入队前取消、排队中取消、排队期间过期都如实返回 `false`（"从未执行"不得被说成"可能已生效"）。
- **缺陷（本切片内发现并修掉）**：实现最初依赖调度器替服务检查取消，导致**预先取消的调用仍会执行到 surface**
  （测试 `CanceledCaller_ReturnsCancelledAndNeverTouchesTheSurface` 抓红）⇒ 现在服务自己在入队前与
  调用 surface 前各查一次取消，并据此选择 `mayHaveSideEffects` 取值。

**有意取舍**：交互类能力的单窗口互斥推迟到切片 E（那时才有对话框/Picker payload，能做端到端验证）。

### C-2 宿主组合与 WinUI 适配器（本轮追加）

- `DesktopCapabilityHost`：只依赖监督端口 `IDesktopConnectionSupervisor`（`DesktopConnectionRunner` 实现），
  因此**启停/单实例/超时语义可无端点单测**。要点：启动时**二选一传输**（选旧 Bridge 则拒绝启动，
  不做跨传输自动回退）；同一 DesktopId **只允许一个活动能力传输**（进程级占用，停止即归还 ——
  否则产品重启后再也起不来）；停止超时（监督器不理会取消）仍释放占用；`WaitForStateAsync` 供「宿主已就绪」告示。
- 装配验证：测试用**真实 `DesktopConnectionRunner` + 假端点**完成握手（generation=3）并达到 `Ready`。
- `Source/PuddingDesktop.CapabilityHost`（WinUI 库，只引用 Contracts）：`WinUiDesktopUiDispatcher`
  把动作调度到 `DispatcherQueue`，入队失败**以异常结束而不是悬挂**。该类需要真实 DispatcherQueue，
  无独立可测逻辑；其契约由假调度器测试覆盖，本工程只保证编译期符合 `IDesktopUiDispatcher`。

### D-0 `webview.page_state` 接通 wire payload（本轮追加）

此前只有领域 DTO 与只读直连 API，因此 Core 无法经能力通道确认「导航后页面到了哪一版」。现已补齐：

- proto：`GetPageStateCommand` = `CapabilityCommand.payload` 第 4 个白名单分支（13）；
  `PageStateOutcome { url, page_version, readiness }` = `OperationResult.outcome` 第 5 个分支（14）。
- readiness 用**字符串线名**而非枚举：Core 可在不重新发版的前提下识别 Desktop 新增状态；
  未知线名折叠为 `unknown`（只读观测 fail soft），由 `PageReadinessWire_RoundTripsAndFoldsUnknownNames` 断言。
- 命令侧 fail closed：能力与 payload 必须一致（`page_state` 带 `navigate` payload ⇒ `invalid_request`）、
  目标缺失 ⇒ `invalid_target`。
- DesktopService 的命令路径与只读直连 API 共用同一套目标/可信级别/版本/竞态校验。
- 真实端点探针新增 page_state 往返：**named pipe 与 loopback h2c 各一次**，探针 **15/15**。

## 4. 探针实测（真实端点，非假流）
```
PASS  kestrel-named-pipe: Kestrel HTTP/2 已监听 \\.\pipe\pudding-ipc-probe-…
PASS  auth-before-handshake: 缺少控制令牌的连接被拒（unauthorized），未完成握手
PASS  named-pipe-navigate: 命令→结果往返 12.6 ms
INFO  payload 4 KiB / 64 KiB / 256 KiB / 1024 KiB: 往返 2.3 / 1.7 / 2.0 / 5.8 ms（单流 ≈1.7 / 36.1 / 126.6 / 172.3 MiB/s）
PASS  cancel: 取消在 3.1 ms 内生效：cancelled + may_have_side_effects=true
INFO  pipe DACL SDDL: O:<当前用户 SID>D:(A;;0x1f019f;;;<当前用户 SID>)
PASS  pipe-acl: DACL 未对 Anonymous/Everyone 授权
INFO  NamedPipeTransportOptions properties = [ListenerQueueCount, MaxReadBufferSize, MaxWriteBufferSize, CurrentUserOnly, PipeSecurity, CreateNamedPipeServerStream]
INFO  CurrentUserOnly 默认 = True
PASS  pipe-acl-hook: 产品期可用 NamedPipeTransportOptions.CreateNamedPipeServerStream 注入受限 ACL
PASS  loopback-navigate: 命令→结果往返 0.7 ms
=== 结论：13 项通过，0 项失败 ===
```

对方案 §9「实施前需技术探针确认」的回答：

| 待确认项 | 实测结论 |
|---|---|
| 选用包版本 | `Google.Protobuf 3.35.1` + `Grpc.Core.Api/Grpc.Net.Client/Grpc.Tools/Grpc.AspNetCore 2.84.0`（与 `Grpc.AspNetCore` 自身依赖的 Protobuf 版本对齐；SDK 10.0.401 / AspNetCore 10.0.12） |
| Named Pipe ACL / 用户身份 | Kestrel 建出的管道 DACL **只含当前用户 SID**；`CurrentUserOnly` 默认 `True`；需要自定义 ACL 时用 `CreateNamedPipeServerStream` 钩子（本版本**没有** `CreatePipe`） |
| 启动就绪描述演进 | 本切片不改启动协议；探针只消费 `DesktopHello`/`CoreHelloAck`，端点发现仍待切片 C 决定 |
| Browser 命令到 proto 映射 | 已落地 navigate / execute_javascript / show_notification 三条 payload 与 inspectable 映射（含 fail-closed 校验）；其余浏览器能力（snapshot/locate/interact/wait）待切片 D |
| 消息字节预算 / 大帧 | 1 MiB 单帧往返 5.8 ms 通过（chunk 上限 4 MiB）；HTML/截图仍应走限额文件或分块专用能力 |
| 跨平台与远端 | 未实施；仅保留 `Tls` 传输枚举与显式 `https` 校验 |

## 5. 有意偏差与实现决定

1. **接口返回 `CapabilityResult<T>` 而非裸 DTO**（方案 §4 示例为 `Task<NavigateResult>`）：领域错误必须能跨进程传递且区分「未执行/可能已生效/可重试」，异常会丢语义。
2. **能力目录 ⊇ 已实现能力**：`shell.status/dialog/file_picker/clipboard` 先冻结线名与语义（预留身份），只有真正实现的能力才出现在 `DesktopConnectionOptions.SupportedCapabilities` 中被声明；未声明的能力 Core 一授予即握手失败。
3. **执行器接缝使用 Contracts 判别联合**（`DesktopCapabilityRequest`/`DesktopCapabilityResponse`，提交 `e041d2e`）：让 UI 实现与 proto 完全解耦。
4. **同 ID 不同 payload 回「瞬时错误」**：不为冲突的重复 ID 改写原记录、不产生第二条审计（一个操作一条审计）。
5. **取消立即回终态**：不等待执行器「自愿」观察 `CancellationToken`（被取消的操作可能仍在后台运行，结果不再回传且如实标注 `mayHaveSideEffects`）。
6. **探针 ACL 探针的一个坑（已修）**：`GetSecurityInfo` 返回的 owner/group/dacl 指针指向描述符内部，**只能 `LocalFree` 描述符本体**；误释放 owner 会导致 `STATUS_HEAP_CORRUPTION`（0xC0000374）。这条经验写进了 `NamedPipeAclProbe` 注释。

## 6. 未实施与风险（诚实登记）

- **未接入宿主**：Core 侧 Connect 服务/注册表/Broker（切片 D）与 Desktop 侧在 `PuddingDesktop` 组合根
  构造 `WinUiDesktopUiDispatcher`/`IDesktopUiSurface` 并启动 `DesktopCapabilityHost`（C-3）尚未接线，
  因此**没有**任何运行中的产品行为改变；本切片不构成「gRPC 已替代 Bridge」。
- **UI 线程语义未在真实 WinUI 上验证**：`IDesktopUiDispatcher` 的契约（队列拒绝必返回 `ui_unavailable`、
  窗口退出必须让任务完成）用假调度器验证过；真实 `DispatcherQueue` 的线程访问与队列拒绝行为需在
  WinUI 应用内定向验证（需要外部控制器重启到新构建）。
- **未测**：真实 ConPTY/Shell 能力、远端 TLS 端点、代理对双向流的支持、`Grpc.Tools` 在 CI 上的 protoc 可用性。
- **性能声明边界**：探针数字是**单流、本机、空载**往返，不能据此宣称比现有 Bridge 更快（方案 §9 的要求）。
- 方案中「旧 WebSocket Bridge 逐步退役」「移除临时传输开关」均未开始。
