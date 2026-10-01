# Pudding.CapabilityBroker CodeMAP

> **Core 侧能力 Broker**（计划 §3/§5）：接受 Desktop 主动拨入的双向流、完成握手协商，
> 并作为「已连接 Desktop 的注册表 + 能力调用入口」。
> 平台无关：只依赖 `Pudding.Contracts` + `Pudding.Rpc.Protocol`；编译期 Target
> `EnforceCapabilityBrokerBoundary` 禁止引用 ASP.NET Core / Desktop 侧组件 / 宿主 / Runtime。
> 测试：`Source/Pudding.CapabilityBrokerTests`（**57 用例**，假通道确定性验证，不需要 Kestrel）

## 为什么有 `ICoreDesktopChannel`

服务端的 gRPC 流是 `<c>IAsyncStreamReader</c>` + `<c>IServerStreamWriter</c>`，属于 ASP.NET Core 类型。
本组件用两方法的 `ICoreDesktopChannel` 把它抽象出来，于是**全部会话语义（协商、关联、期限、取消、队列、断连收尾）
都能在没有 Kestrel 的情况下被确定性测试**；宿主侧薄适配层只做流类型转换 + `AcceptAsync` + `await session.Completion`。

## 文件

| 文件 | 用途 |
|---|---|
| `CapabilityBroker.cs` | 接受连接：读 hello（带握手超时）→ 校验身份/版本/能力 → 授予 `声明 ∩ 本机允许` → 回 ack → 建立会话并注册；**同一 Desktop 只允许一个活动传输**；每次接受递增世代（旧 PageId/Snapshot ref 由此作废）；会话结束自动从注册表移除 |
| `DesktopSession.cs` | 会话：命令下发（有界队列按帧数与期限）、结果关联（未知 OperationId 与**旧世代结果**一律忽略）、期限到点本地给 `deadline_exceeded`、调用方取消发 cancel 帧并立即给终态、断连把 pending 收尾为 `OutcomeUnknown`（已发）/`Disconnected`（未发）并归还在途额度 |
| `WireMapping.cs` | `CoreCommandEncoder`（领域 → proto，四种 payload；指纹只覆盖能力与业务 payload）、`DesktopResultDecoder`（proto → 领域，fail closed：payload 与命令能力不一致 ⇒ `internal_error`；错误码/重试/副作用语义如实还原）、`PageReadinessWire`（就绪度线名，未知折叠为 `unknown`） |
| `ICoreDesktopChannel.cs` | 服务端流接缝 + `DesktopLinkState` + 授权接缝 `IDesktopCapabilityAuthorizer`（默认 **DenyAll**：RPC 可达 ≠ 获得桌面操作授权）+ `AllowAll`（仅测试/受控探针） |
| `DesktopCapabilityPolicy.cs` | 本机可授予能力上限、在途/队列/心跳/握手参数；`ToWireLimits` 下发实际限制 |
| `CapabilityEndpointNaming.cs` | **端点命名与描述**（计划 §7）：管道名 = `pudding-capability-<作用域哈希>`（按用户 + 产品实例派生，不同 DataRoot 不串接；哈希而非明文，管道名出现在系统工具里也不泄漏作用域）；`LoopbackEndpoint`/`TlsEndpoint` 校验端口范围、TLS 必须显式 host；描述经 `ToEndpointString()` 发布 |

## 语义要点（与 Desktop 侧对称）

| 情形 | Core 侧行为 |
|---|---|
| Desktop 自称身份不符 | 拒绝握手（`unauthorized`）：身份来自本机可信配置，不接受对端自称 |
| 授予 | 只授予 `Desktop 声明 ∩ 本机允许`；协商失败的越权声明不做部分接受 |
| 无任何可授予能力 | 握手成功但 `NegotiatedCapabilities = None`，任何调用明确 `unsupported_capability` |
| 授权 | 每次调用经 `IDesktopCapabilityAuthorizer`；默认拒绝 |
| 重复 OperationId | 同 payload 复用进行中的任务；不同 payload ⇒ `invalid_request` |
| 在途/队列耗尽 | 等到该操作 deadline ⇒ `resource_exhausted`；**断连**则给 `not_connected`（两种语义必须区分） |
| 期限到点 | 本地 `deadline_exceeded`；命令已发出时 `mayHaveSideEffects=true` |
| 取消 | 发 `OperationCancel` + 本地立即终态（`mayHaveSideEffects=true`），Desktop 的迟到结果被忽略 |
| 断连 | pending 收尾为 `Disconnected`/`OutcomeUnknown`，归还在途额度，注册表移除（不悬挂、不泄漏） |
| 审计 | 每个操作恰好一条 `DesktopCapabilityAuditRecord`（成功/失败/拒绝/断连），不含脚本正文或页面数据 |

## 与宿主的关系（下一步）

本组件**不托管 Kestrel、不注册 DI、不发布端点**。宿主（PuddingHost）需要做的只有：
①把服务端流适配成 `ICoreDesktopChannel`；②在已认证的 `Connect` 服务方法里 `AcceptAsync` 并 `await session.Completion`；
③在组合根注入 `CapabilityBroker` 与真正的 `IDesktopCapabilityAuthorizer`（继承 Tool Runtime 准入语义）。
端点发布/就绪描述与远端 TLS 仍属后续切片；产品内装配需要重启 Core，本组件本身已可独立构建与测试。
