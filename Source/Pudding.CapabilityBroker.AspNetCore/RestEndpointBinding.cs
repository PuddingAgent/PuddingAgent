using System.Net;

namespace Pudding.CapabilityBroker.AspNetCore;

/// <summary>
/// 把「宿主本来会绑定的 REST 地址」解析成显式 Kestrel 端点。
///
/// 为什么需要它（实测约束）：<c>KestrelServerOptions.Listen*</c> 会**覆盖** <c>UseUrls</c> ⇒
/// 一旦为能力通道新增监听，REST 会静默消失。因此启用通道时必须显式重绑 REST，
/// 而重绑的前提就是先把地址解析成 <see cref="IPAddress"/> + 端口。
///
/// <b>不猜</b>：https（需证书配置）、非通配/回环主机名、不可解析的地址一律**失败**
/// （由宿主 fail closed，提示运维配置 urls 或关闭开关），绝不让 REST 半死不活地留在那里。
/// </summary>
public static class RestEndpointBinding
{
    /// <summary>解析结果：监听地址与端口。端口 <c>0</c> 表示动态端口（启动后才确定）。</summary>
    public readonly record struct KestrelEndpoint(IPAddress Address, int Port);

    public static IReadOnlyList<KestrelEndpoint> Parse(IEnumerable<string>? restUrls)
    {
        var parsed = new List<KestrelEndpoint>();

        foreach (var raw in restUrls ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // `*` / `+` 是 Kestrel/UseUrls 常见的通配写法，但不是合法的 URI 主机名：
            // 先归一化再解析，避免「产品配置合法、启用通道后却启动失败」。
            var normalized = raw.Trim()
                .Replace("://*", "://0.0.0.0", StringComparison.Ordinal)
                .Replace("://+", "://0.0.0.0", StringComparison.Ordinal);

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException(
                    $"无法解析 REST 地址 '{raw}'：启用能力通道时需要显式重绑 REST，地址必须可解析。"
                    + "请修正 urls 配置，或把 Desktop:CapabilityChannel:Enabled 置为 false。");
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"REST 地址 '{raw}' 的 scheme '{uri.Scheme}' 无法在启用能力通道时显式重绑"
                    + "（https 需要证书配置）。请改用 http，或把 Desktop:CapabilityChannel:Enabled 置为 false。");
            }

            parsed.Add(new KestrelEndpoint(ResolveAddress(uri, raw), uri.Port));
        }

        if (parsed.Count == 0)
        {
            throw new InvalidOperationException(
                "启用能力通道但未解析出任何 REST 端点：无法保证 REST 不被 Listen* 覆盖。"
                + "请显式配置 urls，或把 Desktop:CapabilityChannel:Enabled 置为 false。");
        }

        return parsed;
    }

    private static IPAddress ResolveAddress(Uri uri, string raw) => uri.Host switch
    {
        "localhost" or "127.0.0.1" => IPAddress.Loopback,
        "0.0.0.0" or "*" or "+" => IPAddress.Any,
        "[::]" or "::" => IPAddress.IPv6Any,
        _ when IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) => literal,
        _ => throw new InvalidOperationException(
            $"REST 地址 '{raw}' 的主机名 '{uri.Host}' 不是通配/回环/字面 IP：启用能力通道时需要显式重绑 REST，"
            + "无法保证逐字等价。请改用 http://0.0.0.0:<port> 或 http://127.0.0.1:<port>，"
            + "或把 Desktop:CapabilityChannel:Enabled 置为 false。"),
    };
}