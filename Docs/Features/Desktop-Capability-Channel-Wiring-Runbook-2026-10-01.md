# 能力通道接线手册（切片 C-3 收尾 · 需重启窗口）

> 状态：**全部代码已就绪并通过独立验证**（365 用例 + 真实端点探针 45/45），本手册描述的是
> **唯一会改变产品行为**的一步：把能力通道接入 `PuddingHost` 组合根与 `PuddingDesktop` 组合根。
> 该步必须在外部控制器的重启窗口内执行并验收——Agent 无法验收承载自身的生命周期。
>
> 前置阅读：[技术方案](Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md) §3/§6/§7/§10、
> 适配层 [code_map](../../Source/Pudding.CapabilityBroker.AspNetCore/code_map.md)（含实测约束与接线配方）。

## 0. 不可跳过的三条实测约束

1. **`KestrelServerOptions.Listen*` 会覆盖 `UseUrls`**（本系列实测）：一旦调用
   `ListenForCapabilityChannel`，原先由 `UseUrls` 绑定的 REST 端点会**静默消失**。
   ⇒ 组合根必须**显式绑定两者**（REST 的 `Listen(...)` + 能力通道的 `ListenForCapabilityChannel(...)`），
   或在保持 REST 现有绑定的前提下另择方案。验证方式：`IServerAddressesFeature` 应同时列出 REST 与能力端点。
2. **命名管道端点也会出现在地址列表里**（形如 `http://pipe`，`Uri.Port` 为默认 80）：
   任何「按地址探测端口」的既有代码都必须排除它。
3. **默认关闭**：`Desktop:CapabilityChannel:Enabled` 缺省 false 时，宿主不注册任何服务、不监听任何新端点
   ⇒ 不配置即与今天行为逐字一致。**回滚 = 把该开关置 false 并重启。**

## 1. 配置（`<DataRoot>/config/system.json`）

```jsonc
"Desktop": {
  "CapabilityChannel": {
    "Enabled": true,              // 唯一开关；false/缺省 = 完全不启用
    "Transport": "named-pipe",    // named-pipe（产品默认）| loopback-h2c（调试备用）| both
    "DesktopId": "default",       // 必须与 Desktop 侧 desktop.json 的同一值一致
    "Grantable": "webview.navigate,webview.execute_javascript,webview.page_state,shell.notification,shell.status,browser.snapshot,browser.locate,browser.interact,browser.wait_for,browser.contexts,browser.tabs"
  }
}
```

- `Grantable` 是**上限**：Desktop 声明的能力与之取交集后才是实际可用集合。
- 未登记的能力线名 ⇒ 配置整体失败（不静默忽略）。
- `Transport=loopback-h2c` 必须给 `LoopbackPort`；`named-pipe` 不接受 `LoopbackPort`；
  两者都不接受对方的字段（保证「配置与监听一致」）。

## 2. `PuddingHost` 侧接线（约 5 处）

```csharp
// PuddingApplicationHost.CreateAsync(...) 内，取得 hostOptions 之后：
var capabilityChannel = CapabilityChannelConfiguration.Bind(builder.Configuration);

if (capabilityChannel.Enabled)
{
    // ① 认证：复用既有 ControlToken 校验（按 DataRoot 的 system.json、常量时间比较、支持轮换）
    var controlTokenValidator = new DesktopControlTokenValidator(dataRoot);
    var authenticator = new ControlTokenCapabilityAuthenticator(controlTokenValidator);
    // ② 授权：继承 Tool Runtime 准入（见第 3 节）；在接好之前用 DenyAll（默认即拒绝）
    IDesktopCapabilityAuthorizer authorizer = new ToolRuntimeDesktopCapabilityAuthorizer(/* 既有准入服务 */);

    var channelOptions = capabilityChannel.CreateOptions(
        userScope: /* 当前用户 SID 或用户名 */,
        productInstanceId: dataRoot,
        coreInstanceId: /* Core 实例 ID，建议用启动时生成的 GUID */);

    builder.Services.AddCapabilityChannel(channelOptions, authenticator, authorizer);

    // ③ 端点：显式绑定 REST 与能力通道（**不要**只依赖 UseUrls，见约束 1）
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Listen(IPAddress.Loopback, resolvedRestPort, listen => listen.Protocols = HttpProtocols.Http1);
        kestrel.ListenForCapabilityChannel(channelOptions);
    });
}

// PuddingWebApplicationExtensions 的 Map* 链中：
if (app.Services.GetRequiredService<CapabilityChannelConfiguration>().Enabled) // 或把开关记在 hostOptions
{
    app.MapCapabilityChannel();
}
```

需要新增的两个小类（建议放 `PuddingHost/Hosting/`）：

```csharp
internal sealed class ControlTokenCapabilityAuthenticator(DesktopControlTokenValidator validator)
    : ICoreCapabilityAuthenticator
{
    public ValueTask<DesktopCapabilityError?> AuthenticateAsync(
        IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        // 复用既有 Header 名（BrowserBridgeProtocol.ControlTokenHeader）与校验逻辑，
        // **不**在本组件内重新实现常量时间比较，避免两套实现漂移。
        var presented = headers.TryGetValue(BrowserBridgeProtocol.ControlTokenHeader, out var value) ? value : null;
        return ValueTask.FromResult<DesktopCapabilityError?>(
            validator.Validate(presented) ? null : DesktopCapabilityError.Unauthorized("capability credentials rejected"));
    }
}
```

## 3. 授权：必须继承 Tool Runtime 准入（不可用 AllowAll 上线）

- 探针里用的是 `AllowAllDesktopCapabilities`，**只能用于探针**。
- 产品必须实现 `IDesktopCapabilityAuthorizer`：把「谁（调用方身份/会话/Agent）能否对**
  哪个目标**执行**哪个能力」映射到既有 Tool Runtime 准入判定；
  无法判定时**返回拒绝**（fail closed，与本系列其他接缝一致）。
- 授权失败语义：返回 `DesktopCapabilityError.Unauthorized(detail)`；detail 不得包含页面内容或凭据。

## 4. 启动就绪流程：发布端点描述

- `capabilityChannel.Describe(userScope, productInstanceId, coreInstanceId)` 产出
  `DesktopCapabilityEndpoint`（**不含凭据**；关闭时返回 `null`）。
- 需要把它并入现有「启动就绪」流程。**重启窗口内第一件事是确认现有就绪流程的形态**
  （REST 就绪端点 / 文件 / stdout 约定），再决定挂在哪一环——本手册不臆测该形态。
- Desktop 侧用 `DesktopChannelTransportResolver.ResolveFromText(...)` 严格解析；解析失败即不启通道
  （不做跨传输回退）。

## 5. `PuddingDesktop` 侧接线（约 3 处）

```csharp
// 组合根：主窗口构造函数取得 DispatcherQueue 之后
var dispatcher = new WinUiDesktopUiDispatcher(DispatcherQueue);
IDesktopUiSurface surface = new WebView2DesktopUiSurface(/* 页面注册表 + CoreWebView2 工厂 */);
var host = new DesktopCapabilityHost(dispatcher, surface, options, supervisorFactory, auditSink);
await host.StartAsync(cancellationToken);

// 传输二选一：选旧 Bridge 则不启能力通道（不做跨传输回退）
// 目标注册表由浏览器控制器在页面创建/关闭时更新；页面版本由 surface 在导航/交互后推进
```

未完成项：`WebView2DesktopUiSurface` 尚未实现（需真实 `CoreWebView2`）；`WinUiDesktopUiDispatcher`
已实现并 0 警告 0 错误编译通过，但**线程访问验证必须在真实 DispatcherQueue 下做**。

## 6. 重启窗口内的验收清单（外部控制器执行）

| # | 动作 | 期望 |
|---|---|---|
| 1 | 关闭 `Enabled` 重启一次 | 行为与今天一致：REST 正常、无新端点、无 `pudding-capability-*` 管道 |
| 2 | 打开 `Enabled`（named-pipe）重启 | `IServerAddressesFeature` 同时列出 REST 与管道；REST 健康检查 200 |
| 3 | 就绪描述发布 | Desktop 能解析出端点（无凭据） |
| 4 | Desktop 启动并拨入 | 握手协商成功，世代 ≥ 1，能力交集符合 `Grantable` |
| 5 | 探针（可执行文件） | 45/45 exit 0 —— 注意探针自带服务端；产品验收应改用**真实 Core** 端点跑同一批断言 |
| 6 | 无凭据/错凭据连接 | 被拒（`unauthenticated`），且**不产生会话** |
| 7 | 关闭 Desktop | Core 侧注册表清空、管道释放；Core 保持存活 |
| 8 | 回滚演练 | `Enabled=false` 重启后回到第 1 步状态 |

## 7. 已知风险与缓解

| 风险 | 缓解 |
|---|---|
| `Listen*` 覆盖 `UseUrls` 导致 REST 全线不可用 | 第 6 节第 2 步专门校验；改动集中在 `ConfigureKestrel` 一处 |
| 授权器未接好就上线（AllowAll 残留） | `AddCapabilityChannel` 的授权器参数默认 = **DenyAll**；探针外不得传 `AllowAll` |
| 就绪描述与实际监听不一致 | `Transport` 三态 + `Describe` 只发布实际监听形态；探针已断言 |
| Desktop 侧线程访问错误 | `WinUiDesktopUiDispatcher` 的 `TryEnqueue` 失败即抛异常（不悬挂）；真实 DispatcherQueue 下验证属验收项 |
| 迁移期双通道并存导致重复执行 | 单实例传输策略（同一 DesktopId 只允许一个活动传输）+ 桌面侧「传输二选一」 |
