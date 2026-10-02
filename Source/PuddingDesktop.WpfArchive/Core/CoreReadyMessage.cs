using System.Text.Json;

namespace PuddingDesktop.Core;

/// <summary>
/// Parsed PUDDING_DESKTOP_READY signal from Core stdout.
/// Format: PUDDING_DESKTOP_READY {"protocolVersion":1,"processId":1234,"baseAddress":"http://127.0.0.1:8080"}
/// 能力通道启用时还带 "capabilityEndpoint":"kind:address|protocolVersion|coreInstanceId"（不含凭据）；
/// 关闭时该字段整个缺席。字段名与 Core 侧 Pudding.CapabilityBroker.AspNetCore.CapabilityChannelReadySignal.FieldName 一致。
/// </summary>
public sealed record CoreReadyMessage
{
    public int ProtocolVersion { get; init; } = 1;
    public required int ProcessId { get; init; }
    public required Uri BaseAddress { get; init; }

    /// <summary>
    /// Core 发布的能力端点描述原文；未启用能力通道时为 <c>null</c>。
    /// 这里**只做搬运**：是否可用由 DesktopCapabilityChannelPreflight 严格判定（单一判定入口），
    /// 解析器不提前替它下结论。
    /// </summary>
    public string? CapabilityEndpoint { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal sealed record RawReadyMessage
    {
        public int ProtocolVersion { get; init; }
        public int ProcessId { get; init; }
        public string? BaseAddress { get; init; }
        public string? CapabilityEndpoint { get; init; }
    }
}
