# Pudding.DesktopConnection CodeMAP

> Desktop 侧**传输适配器**：Desktop 主动发起 gRPC 双向流，Core 经响应流下发能力命令。
> 依赖：`Pudding.Contracts`（领域真源）+ `Pudding.Rpc.Protocol`（wire 真源）+ `Grpc.Net.Client`。
> 编译期 Target `EnforceConnectionBoundary` 禁止引用 Host/Runtime/Desktop/Browser 工程与
> ASP.NET Core/WinUI/WebView2 包（UI 调度由 `IDesktopCapabilityExecutor` 实现方负责）。
> 测试：`Source/Pudding.DesktopConnectionTests`（**68 + 6 用例**，假服务端确定性验证，无需 Kestrel）

## 连接状态机（`DesktopConnection.cs`）

| 关注点 | 实现 |
|---|---|
| 单读单写 | 读循环由 `RunAsync` 驱动；写循环 `WriterLoopAsync` 独占 `IAsyncEnumerable` 式消费；其他线程只写入无界 `Channel`。测试断言「同一条流上并发 writer ≤ 1」 |
| 握手 | 连接建立后由 Desktop 先写 `DesktopHello`（此阶段独占流）；首个 Core 帧必须是 `hello_ack`；校验 connection_id / generation / 协商版本 / 授予能力，任一不合法即 `Faulted`（不部分接受） |
| 世代失效 | `Generation` 来自 ack；`command.generation != 当前` ⇒ 不执行，回 `not_connected`；结果帧恒用当前世代 |
| 命令关联 | `OperationRegistry`：进行中复用（同 ID 同 payload 不重复执行）、终态幂等重放、同 ID 不同 payload 拒绝（回瞬时错误，不改写原记录）、TTL 过期只回 `outcome_unknown`（**绝不重新执行**） |
| 取消 | `OperationCancel` 按世代校验；取消后立即回终态（不等待执行器自愿观察 token），`mayHaveSideEffects` 按能力 Traits 如实标注 |
| deadline | 单操作 deadline 独立于长连接；排队、执行、回传都计入。执行器无视期限 ⇒ 回 `deadline_exceeded` + `mayHaveSideEffects=true`，且不再回第二条结果 |
| 在途上限 | `_inFlight` 信号量；与 Core 声明的更严格上限取小（预占差额实现收缩）；等待超过 deadline ⇒ `resource_exhausted`，不静默丢命令 |
| 背压 | `ByteBudget`：终态结果阻塞等待预算（不可丢，丢则审计 `outcome_unknown`）；状态事件/心跳应答预算不足即丢弃并计数 |
| 同目标串行 | `TargetSerializationGate` 按 `ContextId/PageId` 串行化 `Mutating` 能力（如 navigate/execute_javascript）；只读能力不经过门 |
| 断连 | 所有 pending 确定结束：未开始 ⇒ `Disconnected`（未执行）；已开始 ⇒ `OutcomeUnknown`（可能已生效）；取消源释放、流释放、写队列完成 |
| 存活 | 收到任意 Core 帧刷新时间戳；可选 `InactivityTimeout` 看门狗超时即 `NotConnected` 故障 |
| 审计 | 每操作恰好一条 `DesktopCapabilityAuditRecord`（`AuditFlag` 保证），含排队/执行时长与终态；审计异常不影响结果 |

## 监督与传输

| 文件 | 用途 |
|------|------|
| `DesktopConnectionRunner.cs` | 反复建连；指数退避 + jitter（`BackoffHistory` 可观测）；成功握手后重置梯度；**不跨传输回退、不重放任何副作用命令**；`Generation` 在握手成功当刻对外可见 |
| `IDesktopConnectionSupervisor.cs` | 监督端口（状态/世代/尝试次数/最后错误/状态事件 + `RunAsync`/`DisposeAsync`）：宿主只依赖该端口，因此启停与单实例语义可以**不启动任何端点**就被测试；生产实现是 `DesktopConnectionRunner` |
| `GrpcDesktopChannelStreamFactory.cs` | `GrpcChannel` + `SocketsHttpHandler.ConnectCallback`：NamedPipe（产品默认）/ Loopback h2c（调试备用）/ TLS（远端，后续）；`MaxReceive/SendMessageSize` 绑定帧上限 |
| `DesktopChannelStream.cs` | 双向流抽象（读/写/半关闭）。存在的理由：连接状态机可**不启动 gRPC 服务端**被确定性测试 |
| `DesktopConnectionOptions.cs` | 身份、能力声明、认证（`x-…` 元数据，**凭据不进 ToString/日志**）、在途/字节/终态缓存上限、握手与静默超时、审计出口、`TimeProvider`（可测时钟） |
| `ByteBudget.cs` | 按字节预留的预算（`SemaphoreSlim` 无法按字节取许可）；`ReserveAsync` 支持 FIFO 与取消，`ShrinkTo` 支持协商收缩 |

## 接缝与映射

| 文件 | 用途 |
|------|------|
| `IDesktopCapabilityExecutor.cs` | **UI 侧唯一接缝**：`ExecuteAsync(descriptor, request, context, ct)`，只依赖 Contracts；实现方负责 `DispatcherQueue` 调度、准入与目标校验 |
| `Mapping.cs` | `CoreFrameMapping.Decode`（proto → 领域，fail closed：op id/generation/deadline/能力/payload 一致性/目标/URL/脚本与结果上限）；指纹只覆盖 payload（deadline/trace 不参与），保证重试指纹稳定；`DesktopFrameMapping`（领域 → proto，含 `ErrorOutcome` 语义、`Hello` 的契约版本区间、就绪度/Shell 状态线名走契约层真源（`DesktopPageReadinessWire`/`DesktopShellStatusWire`）） |

## 与方案的对应（切片 B）

- 计划 §5：双向流、握手与世代、命令关联/幂等/取消/背压/断连/错误分类 —— 已实现并测试。
- 计划 §6：网络与序列化不占用 UI 线程（本组件无线程亲和）；UI 边界留在执行器实现方。
- 计划 §7：能力调用继承准入语义（由 Core 侧与执行器实现方负责）；审计不含脚本/URL/剪贴板/Token。
- 尚未实现（后续切片）：Core 侧 Kestrel 服务与 Broker 装配（切片 C/D）、Shell 对话框/Picker/剪贴板（切片 E）、
  旧 WebSocket Bridge 退役（切片 F）、真实 Named Pipe 端到端（`PuddingRpc.IpcProbe` 技术探针负责验证）。
