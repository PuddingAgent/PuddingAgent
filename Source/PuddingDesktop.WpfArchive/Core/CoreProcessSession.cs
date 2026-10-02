namespace PuddingDesktop.Core;

/// <summary>
/// Represents a running Core child process session.
/// </summary>
public sealed record CoreProcessSession
{
    public required int ProcessId { get; init; }
    /// <summary>Loopback address used by Desktop for trusted local control traffic.</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>External HTTP listener exposed by the Core child.</summary>
    public Uri? ListenAddress { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? ReadyAt { get; init; }

    /// <summary>
    /// Core 在就绪信号里发布的能力通道端点描述（`kind:address|protocolVersion|coreInstanceId`，不含凭据）；
    /// 未启用能力通道时为 <c>null</c>。Desktop 侧只做**搬运**：是否可用由
    /// `Pudding.DesktopService.DesktopCapabilityChannelPreflight` 单一判定入口决定。
    /// </summary>
    public string? CapabilityEndpoint { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public int ExitCode { get; init; }
    public bool HasExited { get; init; }
}
