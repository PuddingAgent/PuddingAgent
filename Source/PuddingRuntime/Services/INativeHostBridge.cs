using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// 宿主原生能力桥接接口——桌面软件嵌入 PuddingRuntime 时，
/// 通过实现此接口将软件原生功能暴露为受控 Runtime 能力。
/// </summary>
public interface INativeHostBridge
{
    /// <summary>返回本桥接器所提供的能力列表（注册时上报给 Controller）。</summary>
    IReadOnlyList<NativeCapabilityDescriptor> GetCapabilities();

    /// <summary>
    /// 执行指定能力。
    /// 调用方负责权限与审批校验；桥接层只负责实际执行。
    /// </summary>
    Task<NativeCapabilityInvokeResult> InvokeAsync(
        NativeCapabilityInvokeRequest request,
        CancellationToken ct = default);
}
