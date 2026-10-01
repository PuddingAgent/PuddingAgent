using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Pudding.CapabilityBroker;
using Pudding.CapabilityBroker.AspNetCore;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingHost.Hosting;

/// <summary>
/// 能力通道的组合根接线（切片 C-3）。
///
/// <b>本类型只在 <c>Desktop:CapabilityChannel:Enabled=true</c> 时被调用</b>：
/// 开关关闭（缺省）时，宿主不注册任何服务、不监听任何新端点、不改变任何既有绑定
/// ⇒ 未配置的产品与今天逐字一致。回滚 = 把开关置 false 并重启。
///
/// 三条实测约束（本系列探针/测试证明过）：
/// ① <c>KestrelServerOptions.Listen*</c> 会**覆盖** <c>UseUrls</c> ⇒ 新增能力端点的同时必须
///    **显式重绑 REST**，否则 REST 会静默消失；
/// ② 命名管道端点也会出现在地址列表里（形如 <c>http://pipe</c>）⇒ 按地址探测端口的既有代码必须排除它；
/// ③ 认证先于握手：凭据由既有 <see cref="DesktopControlTokenValidator"/> 做**常量时间**比较，
///    不在这里重新实现，避免两套实现漂移。
/// </summary>
internal static class DesktopCapabilityChannelWiring
{
    /// <summary>既有产品（Desktop ↔ Core）使用的控制令牌 Header 名；两侧必须一致。</summary>
    public const string BridgeControlTokenHeader = "X-Pudding-Desktop-Token";

    /// <summary>
    /// 管道名派生用的「用户作用域」。缺省用「域\用户名」——同一用户在同一台机器上稳定，
    /// 且不随进程重启变化（管道名必须跨 Core 重启保持稳定，Desktop 才有机会重连）。
    /// 可用环境变量覆盖（例如以服务身份运行时显式指定与 Desktop 相同的值）。
    /// </summary>
    public const string UserScopeEnvironmentVariable = "PUDDING_CAPABILITY_USER_SCOPE";

    public static string ResolveUserScope()
    {
        var explicitScope = Environment.GetEnvironmentVariable(UserScopeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitScope))
        {
            return explicitScope;
        }

        var domain = Environment.UserDomainName;
        var user = Environment.UserName;
        return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
    }

    /// <summary>产品实例标识：同一 DataRoot 即同一实例（跨重启稳定）。</summary>
    public static string ResolveProductInstanceId(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("DataRoot is required to derive the capability pipe name.", nameof(dataRoot));
        }

        return Path.GetFullPath(dataRoot);
    }

    /// <summary>
    /// 解析 REST 端点（委派给适配层的可测实现 <see cref="RestEndpointBinding.Parse"/>）。
    /// </summary>
    public static IReadOnlyList<(IPAddress Address, int Port)> ParseRestEndpoints(IEnumerable<string> restUrls) =>
        RestEndpointBinding.Parse(restUrls)
            .Select(endpoint => (endpoint.Address, endpoint.Port))
            .ToArray();
    /// <summary>
    /// 显式绑定 REST（HTTP/1.1）与能力通道。**必须在同一处调用**：先绑 REST，再绑通道。
    /// </summary>
    public static void BindRestAndCapabilityChannel(
        KestrelServerOptions kestrel,
        IReadOnlyList<(IPAddress Address, int Port)> restEndpoints,
        CapabilityChannelOptions channelOptions)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        ArgumentNullException.ThrowIfNull(restEndpoints);
        ArgumentNullException.ThrowIfNull(channelOptions);

        foreach (var (address, port) in restEndpoints)
        {
            kestrel.Listen(address, port, listen => listen.Protocols = HttpProtocols.Http1);
        }

        kestrel.ListenForCapabilityChannel(channelOptions);
    }

    /// <summary>
    /// 启动后校验「REST 与能力端点都真的绑上了」。返回的失败原因可直接进启动日志：
    /// 它会明确区分「REST 被 Listen* 覆盖」与「能力端点没绑上」。
    /// </summary>
    public static CapabilityChannelPreflightReport VerifyBinding(
        IEnumerable<string>? boundAddresses,
        IEnumerable<string> expectedRestAddresses,
        DesktopCapabilityEndpoint? description) =>
        CapabilityChannelPreflight.Check(boundAddresses, expectedRestAddresses, description);
}

/// <summary>
/// 已启用的能力通道的运行时状态（仅在开关打开时注册进 DI）。
/// 它的**存在**即表示「通道已启用」：<c>Build</c>/<c>CaptureBoundAddresses</c> 用它决定映射与预检，
/// 因此不需要在别处重复判断开关。
/// </summary>
internal sealed record DesktopCapabilityChannelRuntime(
    CapabilityChannelConfiguration Configuration,
    CapabilityChannelOptions Options,
    DesktopCapabilityEndpoint Description,
    IReadOnlyList<string> ExpectedRestAddresses);
/// <summary>
/// 能力通道的连接级认证：复用既有 <see cref="DesktopControlTokenValidator"/>
/// （按 DataRoot 的 system.json 每次读取 ⇒ 令牌轮换无需重启，常量时间比较）。
/// </summary>
internal sealed class ControlTokenCapabilityAuthenticator(DesktopControlTokenValidator validator)
    : ICoreCapabilityAuthenticator
{
    private readonly DesktopControlTokenValidator _validator =
        validator ?? throw new ArgumentNullException(nameof(validator));

    public ValueTask<DesktopCapabilityError?> AuthenticateAsync(
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headers);
        cancellationToken.ThrowIfCancellationRequested();

        // 先看配置指定的 Header 名，再看既有 Bridge 使用的 Header 名（两者用同一套校验）。
        var presented = Read(headers, DesktopCapabilityChannelWiring.BridgeControlTokenHeader)
            ?? Read(headers, "x-pudding-control-token");

        if (string.IsNullOrWhiteSpace(presented))
        {
            return ValueTask.FromResult<DesktopCapabilityError?>(
                DesktopCapabilityError.Unauthorized("capability credentials are missing"));
        }

        return ValueTask.FromResult<DesktopCapabilityError?>(
            _validator.Validate(presented)
                ? null
                : DesktopCapabilityError.Unauthorized("capability credentials rejected"));

        static string? Read(IReadOnlyDictionary<string, string> headers, string name) =>
            headers.TryGetValue(name, out var value) ? value : null;
    }
}
