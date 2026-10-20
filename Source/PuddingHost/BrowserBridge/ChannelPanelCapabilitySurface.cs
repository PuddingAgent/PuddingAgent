using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// 面板渲染的**唯一**传输实现：只有能力通道能提供它。
///
/// <para>
/// 与文档预览同样刻意<b>不做运输路由</b>：旧 Bridge 从来没有 panel.render 命令，
/// 「回退 Bridge」不是降级而是假成功。没有活动会话时明确返回 <c>NotConnected</c>。
/// </para>
/// </summary>
internal sealed class ChannelPanelCapabilitySurface(
    Func<DesktopSession?> activeSession) : IDesktopPanelCapabilitySurface
{
    public Task<CapabilityResult<DesktopPanelRenderResult>> RenderPanelAsync(
        DesktopPanelRenderRequest request,
        DesktopCallContext call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);

        var session = activeSession();
        return session is null
            ? Task.FromResult(CapabilityResult<DesktopPanelRenderResult>.Failure(
                DesktopCapabilityError.NotConnected(
                    "panel render requires the capability channel; the legacy bridge has no panel command")))
            : session.RenderPanelAsync(request, call, cancellationToken);
    }
}
