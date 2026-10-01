# Pudding.CapabilityBroker.AspNetCore CodeMAP

> **Core 侧宿主适配层**（计划 §3）：把 gRPC 服务端流接到 `Pudding.CapabilityBroker`，
> 并提供 Kestrel 端点、DI 与路由注册的装配助手。
> 会话语义全部在 `Pudding.CapabilityBroker`；本工程只做「ASP.NET Core ↔ Broker」的接线。
> 编译期边界 `EnforceCapabilityHostAdapterBoundary`：只允许 Broker / Contracts / Rpc.Protocol（禁止 Desktop 侧与宿主工程）。
> 测试：`Source/Pudding.CapabilityBroker.AspNetCoreTests`（**13 用例**，自托管最小 Kestrel + 真实 gRPC 客户端）

## 文件

| 文件 | 用途 |
|---|---|
| `CapabilityChannelHostExtensions.cs` | `CapabilityChannelOptions`（Desktop 身份、可授予能力、队列/心跳/握手限制、消息上限、端点形态、审计出口）+ `AddCapabilityChannel(...)`（注册 Broker/认证器/服务/gRPC）+ `MapCapabilityChannel(app)` + `ListenForCapabilityChannel(kestrel)` |
| `CoreCapabilityAuthentication.cs` | 认证接缝 `ICoreCapabilityAuthenticator`（**默认拒绝一切**）、`StaticHeaderCapabilityAuthenticator`（测试/受控探针）、`ServerStreamDesktopChannel`（服务端流 → `ICoreDesktopChannel`，串行化写入） |
| `DesktopCapabilityService`（同文件内部类） | 唯一 gRPC 服务：**认证先于握手** → `broker.AcceptAsync` → `await session.Completion` |

## 实测约束（装配产品组合根前必读）

1. **`KestrelServerOptions.Listen*` 会覆盖 `UseUrls`**：一旦调用 `ListenForCapabilityChannel`，
   原先由 `UseUrls` 绑定的 REST 端点就会**静默消失**（`IServerAddressesFeature` 里只剩管道/新端点）。
   ⇒ 产品组合根必须**显式绑定两者**（REST 的 `Listen(...)` + 能力通道的 `ListenForCapabilityChannel(...)`），
   或者改用「复用既有监听器的 `Http1AndHttp2`」方案。该约束由
   `KestrelListenOverridesUseUrls_SoTheAssemblyBindsBothExplicitly` 与
   `Host_WithNamedPipeChannel_KeepsServingHttp11RestEndpoints` 两条测试固定下来。
2. **命名管道端点也会出现在地址列表里**（形如 `http://pipe`，`Uri.Port` 会是默认 80）：
   任何按地址列表探测端口的代码都要排除它。
3. **认证失败不产生会话**：被拒的连接不会进入 Broker 的注册表（测试断言 `Sessions` 为空）。
4. 同一 Desktop 的第二条连接在第一条活跃时被拒（`InvalidArgument`），与 Broker 的单实例传输策略一致。

## 与产品的接线（下一步，需要外部控制器重启验证）

```csharp
// PuddingApplicationHost（DesktopChild 模式，且配置开关为开）：
builder.Services.AddCapabilityChannel(options, controlTokenAuthenticator, toolRuntimeAuthorizer);
builder.WebHost.ConfigureKestrel(k => { /* 既有 REST 显式绑定 */ k.ListenForCapabilityChannel(options); });
...
app.MapCapabilityChannel();
```

仍未做：①把上述两行接入 `PuddingApplicationHost`（含 `<DataRoot>/config/system.json` 的开关，默认关闭）；
②用既有 `DesktopControlTokenValidator` 实现 `ICoreCapabilityAuthenticator`；
③用继承 Tool Runtime 准入的实现替换 `AllowAll`/`DenyAll` 授权器；④把端点描述并入「启动就绪」流程。
