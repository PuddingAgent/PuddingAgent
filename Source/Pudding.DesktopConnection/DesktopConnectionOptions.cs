using Pudding.Contracts;
using Pudding.Contracts.Audit;

namespace Pudding.DesktopConnection;

public enum DesktopChannelTransportKind
{
    /// <summary>Windows 产品默认：Core Kestrel Named Pipe + HTTP/2（IPC 是 HTTP/2 的底层传输）。</summary>
    NamedPipe,

    /// <summary>调试备用：显式启用的 Loopback HTTP/2 端点（h2c）。</summary>
    LoopbackHttp2,

    /// <summary>远程 Core/NUC 的独立 TLS HTTP/2 端点（本切片不实现远端连接模式）。</summary>
    Tls,
}

/// <summary>通道传输描述。端点描述里<b>不含</b>任何凭据（凭据走 <see cref="DesktopChannelAuthentication"/>）。</summary>
public sealed record DesktopChannelTransport
{
    private DesktopChannelTransport(DesktopChannelTransportKind kind, string address)
    {
        Kind = kind;
        Address = address;
    }

    public DesktopChannelTransportKind Kind { get; }

    /// <summary>NamedPipe：管道名；LoopbackHttp2/Tls：绝对 URI 基址。</summary>
    public string Address { get; }

    public static DesktopChannelTransport NamedPipe(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200)
        {
            throw new ArgumentException("Pipe name must be 1..200 characters.", nameof(pipeName));
        }

        return new DesktopChannelTransport(DesktopChannelTransportKind.NamedPipe, pipeName);
    }

    /// <summary>仅允许回环地址：不允许因 IPC 失败自动切到公网 TCP。</summary>
    public static DesktopChannelTransport LoopbackHttp2(string baseAddress)
    {
        var uri = ParseAbsolute(baseAddress, "http");
        if (!uri.IsLoopback)
        {
            throw new ArgumentException("Loopback HTTP/2 transport must use a loopback address.", nameof(baseAddress));
        }

        return new DesktopChannelTransport(DesktopChannelTransportKind.LoopbackHttp2, uri.GetLeftPart(UriPartial.Authority));
    }

    public static DesktopChannelTransport Tls(string baseAddress)
    {
        var uri = ParseAbsolute(baseAddress, "https");
        return new DesktopChannelTransport(DesktopChannelTransportKind.Tls, uri.GetLeftPart(UriPartial.Authority));
    }

    private static Uri ParseAbsolute(string? baseAddress, string requiredScheme)
    {
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, requiredScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Address must be an absolute {requiredScheme} URI.", nameof(baseAddress));
        }

        return uri;
    }

    public override string ToString() => $"{Kind}:{Address}";
}

/// <summary>
/// 通道认证：认证先于握手，凭据只经传输元数据传递。
/// 凭据不得出现在日志、URL、UI、诊断包或 <see cref="ToString"/> 里。
/// </summary>
public sealed record DesktopChannelAuthentication
{
    private DesktopChannelAuthentication(string headerName, string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerName) || !headerName.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Header name must be a lowercase 'x-' metadata name.", nameof(headerName));
        }

        HeaderValue = string.IsNullOrEmpty(headerValue)
            ? throw new ArgumentException("Header value must be non-empty.", nameof(headerValue))
            : headerValue;

        HeaderName = headerName;
    }

    public string HeaderName { get; }

    /// <summary>凭据本体：internal 且不参与 ToString/PrintMembers。</summary>
    internal string HeaderValue { get; }

    public static DesktopChannelAuthentication StaticHeader(string headerName, string headerValue) =>
        new(headerName, headerValue);

    public override string ToString() => $"{HeaderName}=***";
}

/// <summary>单条连接（一次握手生命周期）的选项。</summary>
public sealed record DesktopConnectionOptions
{
    public required DesktopInstanceId DesktopId { get; init; }

    public required DesktopProcessInstanceId ProcessInstanceId { get; init; }

    /// <summary>本进程可实现的桌面能力集合。只有真正实现的能力才允许声明。</summary>
    public required DesktopCapability SupportedCapabilities { get; init; }

    public DesktopChannelAuthentication? Authentication { get; init; }

    /// <summary>请求的最大帧体（握手声明用）。</summary>
    public int RequestedMaxFrameBytes { get; init; } = 1024 * 1024;

    /// <summary>本机在途操作上限；Core 若声明更严格的上限则取更严格者。</summary>
    public int MaxInFlightOperations { get; init; } = 16;

    /// <summary>本机出站排队字节预算；Core 若声明更严格的上限则取更严格者。</summary>
    public int MaxQueuedBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>终态结果缓存容量（幂等复用窗口，按最近完成顺序淘汰）。</summary>
    public int MaxTerminalResults { get; init; } = 128;

    /// <summary>终态结果缓存 TTL；过期后重复 ID 只回 <c>OutcomeUnknown</c>，绝不重新执行。</summary>
    public TimeSpan TerminalResultTtl { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>接收侧静默超时（<c>null</c> = 不做存活看门狗）。</summary>
    public TimeSpan? InactivityTimeout { get; init; }

    public IDesktopCapabilityAuditSink AuditSink { get; init; } = NullDesktopCapabilityAuditSink.Instance;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        if (DesktopCapabilities.DeclareFor(SupportedCapabilities).Count == 0)
        {
            throw new ArgumentException("At least one supported capability must be declared.", nameof(SupportedCapabilities));
        }

        if (RequestedMaxFrameBytes is < 1024 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestedMaxFrameBytes), RequestedMaxFrameBytes, "Frame size must be in [1 KiB, 64 MiB].");
        }

        if (MaxInFlightOperations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInFlightOperations), MaxInFlightOperations, "In-flight limit must be positive.");
        }

        if (MaxQueuedBytes < 4 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedBytes), MaxQueuedBytes, "Queue budget must be at least 4 KiB.");
        }

        if (MaxTerminalResults < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTerminalResults), MaxTerminalResults, "Terminal cache capacity must be positive.");
        }

        if (TerminalResultTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TerminalResultTtl), TerminalResultTtl, "Terminal cache TTL must be positive.");
        }

        if (HandshakeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout), HandshakeTimeout, "Handshake timeout must be positive.");
        }

        if (InactivityTimeout is { } inactivity && inactivity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InactivityTimeout), inactivity, "Inactivity timeout must be positive when set.");
        }
    }
}
