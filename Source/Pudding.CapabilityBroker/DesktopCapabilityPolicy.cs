using Pudding.Contracts;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.CapabilityBroker;

/// <summary>
/// Core 侧握手与限制策略：授予能力 = 本机允许授予的上限 ∩ Desktop 声明（越权授予不做部分接受）。
/// 队列与心跳参数只声明本机上限，实际生效值由双方取更严格者。
/// </summary>
public sealed record DesktopCapabilityPolicy
{
    /// <summary>本机允许授予的能力上限（未列出的能力绝不授予）。</summary>
    public required DesktopCapability Grantable { get; init; }

    /// <summary>单连接在途操作上限（Core 侧排队预算）。</summary>
    public int MaxInFlightPerConnection { get; init; } = 8;

    /// <summary>单连接出站队列上限（帧数）。</summary>
    public int MaxQueuedFrames { get; init; } = 128;

    /// <summary>单连接出站字节预算。</summary>
    public int MaxQueuedBytes { get; init; } = 4 * 1024 * 1024;

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>握手等待上限（对端必须先发 hello）。</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal void Validate()
    {
        if (Grantable == DesktopCapability.None)
        {
            throw new ArgumentException("At least one capability must be grantable.", nameof(Grantable));
        }

        if (MaxInFlightPerConnection < 1 || MaxQueuedFrames < 1 || MaxQueuedBytes < 4 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInFlightPerConnection), MaxInFlightPerConnection, "Limits are invalid.");
        }

        if (HandshakeTimeout <= TimeSpan.Zero || HeartbeatInterval <= TimeSpan.Zero || HeartbeatTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout), HandshakeTimeout, "Timeouts must be positive.");
        }
    }

    internal Proto.ChannelLimits ToWireLimits() => new()
    {
        MaxInFlightOperations = (uint)MaxInFlightPerConnection,
        MaxQueuedBytes = (uint)MaxQueuedBytes,
        HeartbeatIntervalMs = (uint)HeartbeatInterval.TotalMilliseconds,
        HeartbeatTimeoutMs = (uint)HeartbeatTimeout.TotalMilliseconds,
    };
}
