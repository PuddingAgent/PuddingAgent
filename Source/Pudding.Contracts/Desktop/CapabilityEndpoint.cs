using System.Globalization;

namespace Pudding.Contracts.Desktop;

public enum DesktopCapabilityEndpointKind
{
    /// <summary>Windows 本机产品：Named Pipe + 显式 HTTP/2。</summary>
    NamedPipe,

    /// <summary>调试备用：显式启用的 Loopback HTTP/2（h2c）。</summary>
    LoopbackHttp2,

    /// <summary>远端 Core/NUC：独立 TLS HTTP/2 端点。</summary>
    Tls,
}

/// <summary>
/// Core 发布的「能力通道端点描述」（计划 §7）：只含端点形态、地址、协议版本与 Core 实例 ID。
///
/// 结构性保证：<b>没有任何凭据字段</b>（Token/密码/密钥都不在这里；凭据由 Desktop 主机侧另行注入），
/// 也没有「让网页决定管道名或远端地址」的输入面——描述由 Core 产生，Desktop 只做校验与解析。
/// 管道名按用户与产品实例隔离（由 <c>CapabilityEndpointNaming</c> 派生），不同 DataRoot 不会串接。
/// </summary>
public sealed record DesktopCapabilityEndpoint
{
    public const int MaxPipeNameLength = 200;

    private DesktopCapabilityEndpoint(
        DesktopCapabilityEndpointKind kind, string address, int protocolVersion, string? serverInstanceId)
    {
        Kind = kind;
        Address = address;
        ProtocolVersion = protocolVersion;
        ServerInstanceId = serverInstanceId;
    }

    public DesktopCapabilityEndpointKind Kind { get; }

    /// <summary>NamedPipe：管道名；LoopbackHttp2/Tls：绝对 URI 的 authority 部分（不含路径/凭据）。</summary>
    public string Address { get; }

    public int ProtocolVersion { get; }

    /// <summary>Core 进程实例 ID：Desktop 用它识别「Core 是否换了实例」。</summary>
    public string? ServerInstanceId { get; }

    public static DesktopCapabilityEndpoint NamedPipe(
        string pipeName, int protocolVersion = DesktopProtocolVersion.Current, string? serverInstanceId = null)
    {
        if (!IsValidPipeName(pipeName))
        {
            throw new ArgumentException(
                $"Pipe name must be 1..{MaxPipeNameLength} characters from [A-Za-z0-9._-] (no separators).",
                nameof(pipeName));
        }

        return new DesktopCapabilityEndpoint(
            DesktopCapabilityEndpointKind.NamedPipe, pipeName, RequireVersion(protocolVersion), NormalizeInstanceId(serverInstanceId));
    }

    public static DesktopCapabilityEndpoint LoopbackHttp2(
        Uri address, int protocolVersion = DesktopProtocolVersion.Current, string? serverInstanceId = null) =>
        new(DesktopCapabilityEndpointKind.LoopbackHttp2,
            RequireAddress(address, "http", requireLoopback: true),
            RequireVersion(protocolVersion),
            NormalizeInstanceId(serverInstanceId));

    public static DesktopCapabilityEndpoint Tls(
        Uri address, int protocolVersion = DesktopProtocolVersion.Current, string? serverInstanceId = null) =>
        new(DesktopCapabilityEndpointKind.Tls,
            RequireAddress(address, "https", requireLoopback: false),
            RequireVersion(protocolVersion),
            NormalizeInstanceId(serverInstanceId));

    public static bool IsValidPipeName(string? pipeName)
    {
        if (string.IsNullOrEmpty(pipeName) || pipeName.Length > MaxPipeNameLength)
        {
            return false;
        }

        foreach (var c in pipeName)
        {
            var allowed = c is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or '-';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>紧凑字符串形式，便于嵌入任何「启动就绪」描述（不含凭据）。</summary>
    public string ToEndpointString() => string.Concat(
        Scheme(Kind),
        ":",
        Address,
        "|",
        ProtocolVersion.ToString(CultureInfo.InvariantCulture),
        "|",
        ServerInstanceId ?? string.Empty);

    /// <summary>严格解析：形态、地址、版本、实例 ID 任一不合法即失败（绝不静默回退）。</summary>
    public static bool TryParse(string? text, out DesktopCapabilityEndpoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length != 3)
        {
            return false;
        }

        var head = parts[0];
        var separator = head.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var scheme = head[..separator];
        var address = head[(separator + 1)..];

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0)
        {
            return false;
        }

        var instanceId = string.IsNullOrEmpty(parts[2]) ? null : parts[2];

        try
        {
            endpoint = scheme switch
            {
                "named-pipe" => NamedPipe(address, version, instanceId),
                "loopback-h2c" when Uri.TryCreate(address, UriKind.Absolute, out var loopback) =>
                    LoopbackHttp2(loopback, version, instanceId),
                "tls" when Uri.TryCreate(address, UriKind.Absolute, out var tls) => Tls(tls, version, instanceId),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            endpoint = null;
        }

        return endpoint is not null;
    }

    private static string Scheme(DesktopCapabilityEndpointKind kind) => kind switch
    {
        DesktopCapabilityEndpointKind.NamedPipe => "named-pipe",
        DesktopCapabilityEndpointKind.LoopbackHttp2 => "loopback-h2c",
        DesktopCapabilityEndpointKind.Tls => "tls",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unregistered endpoint kind."),
    };

    private static int RequireVersion(int protocolVersion) => protocolVersion > 0
        ? protocolVersion
        : throw new ArgumentOutOfRangeException(
            nameof(protocolVersion), protocolVersion, "Protocol version must be positive.");

    private static string? NormalizeInstanceId(string? serverInstanceId)
    {
        if (string.IsNullOrWhiteSpace(serverInstanceId))
        {
            return null;
        }

        return DesktopProcessInstanceId.IsValid(serverInstanceId)
            ? serverInstanceId
            : throw new ArgumentException(
                "Server instance id must be a valid process instance identifier.", nameof(serverInstanceId));
    }

    private static string RequireAddress(Uri? address, string scheme, bool requireLoopback)
    {
        if (address is null || !address.IsAbsoluteUri
            || !string.Equals(address.Scheme, scheme, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(address.UserInfo)
            || address.Query.Length > 0
            || address.Fragment.Length > 0)
        {
            throw new ArgumentException(
                $"Address must be an absolute {scheme} URI without credentials, query or fragment.", nameof(address));
        }

        if (requireLoopback && !address.IsLoopback)
        {
            throw new ArgumentException("Loopback transport must use a loopback address.", nameof(address));
        }

        return address.GetLeftPart(UriPartial.Authority);
    }

    public override string ToString() => ToEndpointString();
}
