# Pudding.Rpc.IpcProbe
> 源指纹: *.cs=4-6086239c696c, *.csproj=1-c26eea6bad01 · 条目数: 4 · 最近整理: 2026-11-22

IPC 技术探针（net10.0 控制台 Exe）。用产品 CapabilityBroker 适配层在 Named Pipe 与 Loopback h2c 上验证 Core↔Desktop 互通与安全断言。产品代码不引用本工程。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | 探针入口：串起握手认证帧预算取消与 ACL 断言并给出退出码 | `Program` | `BrokerProbeServer.cs`、`ProbeExecutor.cs`、`NamedPipeAclProbe.cs` | 帧上限 4 MiB 必须高于产品 `RequestedMaxFrameBytes` 默认 1 MiB，否则 1 MiB 单帧用例先在传输层失败 |
| `BrokerProbeServer.cs` | 探针用的 Core 侧 Kestrel 服务端 | `BrokerProbeServer` | `Program.cs` | 必须走产品 `AddCapabilityChannel` 与 `MapCapabilityChannel`，换成手写管道后探针验证的就不再是产品路径 |
| `ProbeExecutor.cs` | WinUI 侧桌面能力的模拟实现 | `ProbeExecutor` | `BrokerProbeServer.cs` | 只模拟不触真实 UI，改接真实桌面自动化会让探针依赖交互式桌面会话 |
| `NamedPipeAclProbe.cs` | 读管道 DACL 并检查 ACL 注入钩子，同时提供报告收集器 | `NamedPipeAclProbe`、`ProbeReport` | `Program.cs` | 客户端句柄常无 READ_CONTROL，必须按对象类型降级重试，删掉降级会把权限不足误报成安全缺陷 |
