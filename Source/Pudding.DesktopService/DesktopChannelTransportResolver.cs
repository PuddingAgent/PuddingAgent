using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;

namespace Pudding.DesktopService;

/// <summary>
/// 把 Core 发布的端点描述解析成 Desktop 侧可用的传输（计划 §7）。
///
/// 拒绝而不是容错：版本不受支持、端点形态未知、地址不可用一律明确失败；
/// <b>凭据不从描述里取</b>——描述结构里没有凭据字段，凭据只能由主机侧注入
/// （<c>DesktopChannelAuthentication</c>）。
/// </summary>
public static class DesktopChannelTransportResolver
{
    public static CapabilityResult<DesktopChannelTransport> Resolve(DesktopCapabilityEndpoint? endpoint)
    {
        if (endpoint is null)
        {
            return CapabilityResult<DesktopChannelTransport>.Failure(
                DesktopCapabilityError.InvalidRequest("capability endpoint description is missing"));
        }

        if (!DesktopProtocolVersion.IsSupported(endpoint.ProtocolVersion))
        {
            return CapabilityResult<DesktopChannelTransport>.Failure(
                DesktopCapabilityError.UnsupportedCapability(
                    $"capability protocol version {endpoint.ProtocolVersion}"));
        }

        try
        {
            var transport = endpoint.Kind switch
            {
                DesktopCapabilityEndpointKind.NamedPipe => DesktopChannelTransport.NamedPipe(endpoint.Address),
                DesktopCapabilityEndpointKind.LoopbackHttp2 => DesktopChannelTransport.LoopbackHttp2(endpoint.Address),
                DesktopCapabilityEndpointKind.Tls => DesktopChannelTransport.Tls(endpoint.Address),
                _ => null,
            };

            return transport is null
                ? CapabilityResult<DesktopChannelTransport>.Failure(
                    DesktopCapabilityError.InvalidRequest($"unknown endpoint kind {endpoint.Kind}"))
                : CapabilityResult<DesktopChannelTransport>.Success(transport);
        }
        catch (ArgumentException ex)
        {
            return CapabilityResult<DesktopChannelTransport>.Failure(
                DesktopCapabilityError.InvalidRequest($"capability endpoint address is not usable ({ex.Message})"));
        }
    }

    /// <summary>解析启动就绪描述里携带的端点字符串并转成传输。</summary>
    public static CapabilityResult<DesktopChannelTransport> ResolveFromText(string? endpointText)
    {
        if (!DesktopCapabilityEndpoint.TryParse(endpointText, out var endpoint))
        {
            return CapabilityResult<DesktopChannelTransport>.Failure(
                DesktopCapabilityError.InvalidRequest("capability endpoint description could not be parsed"));
        }

        return Resolve(endpoint);
    }
}
