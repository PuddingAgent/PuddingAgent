# Pudding.Rpc.Protocol CodeMAP

> **wire-only 叶程序集**：`Protos/desktop_capability.proto` + 其生成类型（消息 / 客户端桩 / 服务基类）
> 依赖：`Google.Protobuf` + `Grpc.Core.Api` + 构建期 `Grpc.Tools`（`PrivateAssets=all`）。
> 编译期 Target `EnforceProtocolLeafBoundary` 强制 `ProjectReference = 0`。
> 测试：`Source/Pudding.Rpc.ProtocolTests`（只引用本组件；17 用例含 5 条边界断言 + 字段号快照）

## 真源与生成

| 项 | 说明 |
|---|---|
| 真源 | `Protos/desktop_capability.proto`（字段、编号、service 定义）。生成代码落 `obj`，**不入库、禁止手改** |
| 命名空间 | proto `pudding.capability.v1` → C# `Pudding.Rpc.Protocol.V1` |
| 服务 | 唯一 service `DesktopCapability`，唯一方法 `Connect(stream DesktopFrame) → (stream CoreFrame)`，`GrpcServices=Both` |
| 领域真源 | C# 接口与领域 DTO 的真源是 `Pudding.Contracts`；本程序集**不含**映射/连接/业务/UI 代码 |
| 重新生成 | `dotnet build Source\Pudding.Rpc.Protocol\Pudding.Rpc.Protocol.csproj -c Release`（Grpc.Tools 调 protoc） |

## 帧与握手

| 消息 | 用途 |
|---|---|
| `DesktopFrame` | Desktop → Core：`hello(1)` / `result(2)` / `event(3)` / `heartbeat_ack(4)`，`oneof frame` |
| `CoreFrame` | Core → Desktop：`hello_ack(1)` / `command(2)` / `cancel(3)` / `heartbeat(4)`，`oneof frame` |
| `DesktopHello` | desktop_id、process_instance_id、`ProtocolRange`、能力声明、请求帧上限。认证先于握手（传输层元数据），握手成功才开放调用 |
| `CoreHelloAck` | connection_id、**generation**（旧世代失效依据）、协商版本、实际能力、`ChannelLimits`、core_instance_id |
| `ChannelLimits` | max_frame_bytes / max_in_flight_operations / max_queued_bytes / heartbeat_interval_ms / heartbeat_timeout_ms（默认值是压测前的起点，不是已测结论） |
| `Heartbeat` / `HeartbeatAck` | 序号回显，用于判定对端存活 |

## 命令与结果

| 消息 | 用途 |
|---|---|
| `CapabilityCommand` | operation_id、generation、capability（线名）、trace_id、correlation_id、`deadline`(Timestamp)、`oneof payload` |
| `CapabilityCommand.payload` | **白名单判别联合**：`navigate(10)` / `execute_javascript(11)` / `show_notification(12)` / `get_page_state(13)`。无 payload ⇒ `PayloadCase.None`，映射层必须 fail closed |
| `OperationCancel` | 尽力而为取消；不能撤销已执行的脚本 |
| `OperationResult` | operation_id、generation、`oneof outcome`：`navigate(10)` / `execute_javascript(11)` / `show_notification(12)` / `error(13)` / `page_state(14)` |
| `NavigateOutcome` | `NavigateDisposition`（Unspecified/Accepted/Completed）+ current_url + page_version。Accepted/Completed **都不代表 DOM 已就绪** |
| `JavascriptOutcome` | `JavascriptValueKind` + **裸 JSON 片段** + truncated |
| `PageStateOutcome` | 只读页面状态：url / page_version / **readiness 字符串线名**（unknown/loading/interactive/complete/failed；未知线名在映射层折叠为 unknown，便于 Core 识别 Desktop 新增状态） |
| `ErrorOutcome` | 领域错误：code（线名真源在 Contracts）/ message / retryable / may_have_side_effects。流故障不用它，用 gRPC status |
| `DesktopEvent` | 状态事件（可合并）：`page_state(10)` / `channel_status(11)`。终态结果不可丢，走 `OperationResult` |

## 兼容性纪律

- 已发布字段号与 oneof 分支号**永不重用**；删除字段用 `reserved`（当前无删除）。
- 字段号快照由 `WireContractSnapshotTests` 断言（`hello=1`、`command=2`、payload 从 10 起编号，留 1–9 给身份字段；结果 outcome 同样从 10 起，`error=13`、`page_state=14`）。
- 身份字段（operation_id/generation）在命令与结果中**编号一致**，便于映射与审计关联。
