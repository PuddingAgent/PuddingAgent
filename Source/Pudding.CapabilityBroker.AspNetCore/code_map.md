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
| `CapabilityChannelConfiguration.cs` | **配置绑定与端点派生**（计划 §7/§9）：`Desktop:CapabilityChannel` 段 → `CapabilityChannelOptions` + 就绪描述。默认关闭；`Transport` 三态（`named-pipe` 缺省 / `loopback-h2c` / `both`）保证「配置与监听一致」；能力线名未知即整体失败；`Describe(...)` 只发布实际监听的端点且不含凭据 |
| `CapabilityChannelTransport.cs` | 传输形态常量（配置取值真源） |
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

## 与产品的接线（重启窗口内只需两行 + 一段配置）

```jsonc
// <DataRoot>/config/system.json（缺省不写 = 关闭，产品行为不变）
"Desktop": {
  "CapabilityChannel": {
    "Enabled": true,
    "Transport": "named-pipe",          // named-pipe | loopback-h2c | both
    "DesktopId": "default",             // 与 Desktop 侧 desktop.json 同一值
    "Grantable": "webview.navigate,webview.execute_javascript,webview.page_state,shell.notification,shell.status"
  }
}
```

```csharp
// PuddingApplicationHost（DesktopChild 模式）
var channel = CapabilityChannelConfiguration.Bind(builder.Configuration);
if (channel.Enabled)
{
    var options = channel.CreateOptions(userScope, productInstanceId, coreInstanceId);   // 非法配置 ⇒ 抛错，宿主记录并继续用旧传输（fail closed）
    builder.Services.AddCapabilityChannel(options, controlTokenAuthenticator, toolRuntimeAuthorizer);
    builder.WebHost.ConfigureKestrel(k => { /* 既有 REST 显式绑定 */ k.ListenForCapabilityChannel(options); });
}
...
if (channel.Enabled) { app.MapCapabilityChannel(); }
// 就绪流程：channel.Describe(userScope, productInstanceId, coreInstanceId) 发布端点（关闭时为 null）
```

仍未做（属于重启窗口内的装配，需外部控制器验收）：①上述代码接入 `PuddingApplicationHost`；
②用既有 `DesktopControlTokenValidator` 实现 `ICoreCapabilityAuthenticator`；③用继承 Tool Runtime 准入的实现
替换探针的 `AllowAll` 授权器；④`Describe(...)` 并入启动就绪流程；⑤Desktop 侧组合根启动宿主。
