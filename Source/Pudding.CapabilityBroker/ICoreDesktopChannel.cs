using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.CapabilityBroker;

/// <summary>Core 侧的一条已认证双向流（由 ASP.NET Core gRPC 服务适配 <c>IAsyncStreamReader</c>/<c>IServerStreamWriter</c>）。</summary>
public interface ICoreDesktopChannel
{
    /// <summary>发送一帧；流已关闭时抛异常（由会话折叠为断连）。</summary>
    ValueTask SendAsync(Proto.CoreFrame frame, CancellationToken cancellationToken);

    /// <summary>读取下一帧；对端正常结束返回 <c>null</c>。</summary>
    ValueTask<Proto.DesktopFrame?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Core 侧会话状态（两侧状态机各自拥有，不跨程序集共享）。</summary>
public enum DesktopLinkState
{
    Handshaking,
    Ready,
    Disconnected,
    Faulted,
}

/// <summary>
/// 每次能力调用的授权判定。身份与权限由 Core 的<b>可信运行上下文</b>产生，
/// 绝不使用模型或网页填写的字段（计划 §7）。
/// </summary>
public interface IDesktopCapabilityAuthorizer
{
    ValueTask<DesktopCapabilityError?> AuthorizeAsync(
        DesktopCapabilityAuthorizationContext context, CancellationToken cancellationToken);
}

public sealed record DesktopCapabilityAuthorizationContext(
    DesktopInstanceId DesktopId,
    ConnectionGeneration Generation,
    DesktopCapabilityDescriptor Capability,
    DesktopCapabilityRequest Request,
    DesktopCallContext Call);

/// <summary>显式放行全部能力的授权器：只在测试与受控探针中使用。</summary>
public sealed class AllowAllDesktopCapabilities : IDesktopCapabilityAuthorizer
{
    public static readonly AllowAllDesktopCapabilities Instance = new();

    private AllowAllDesktopCapabilities()
    {
    }

    public ValueTask<DesktopCapabilityError?> AuthorizeAsync(
        DesktopCapabilityAuthorizationContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DesktopCapabilityError?>(null);
}

/// <summary>默认授权器：<b>拒绝</b>。RPC 可达不等于获得桌面操作授权（计划 §7）。</summary>
public sealed class DenyAllDesktopCapabilities : IDesktopCapabilityAuthorizer
{
    public static readonly DenyAllDesktopCapabilities Instance = new();

    private DenyAllDesktopCapabilities()
    {
    }

    public ValueTask<DesktopCapabilityError?> AuthorizeAsync(
        DesktopCapabilityAuthorizationContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DesktopCapabilityError?>(
            DesktopCapabilityError.Unauthorized(
                $"no authorizer is configured for '{context.Capability.Name}'"));
}
