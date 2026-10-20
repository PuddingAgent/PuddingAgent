using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// 文档预览的**唯一**传输实现：只有能力通道能提供它。
///
/// <para>
/// 与浏览器能力不同，这里刻意<b>不做运输路由</b>：旧 Bridge 从来没有 document.preview 命令
/// （那次尝试 1cae733 已被 7717b0f 撤销），所以"回退到 Bridge"不是降级而是假成功。
/// 没有活动会话时明确返回 <c>NotConnected</c>，让工具如实报错，而不是猜一个 Desktop 去发命令。
/// </para>
/// </summary>
internal sealed class ChannelDocumentCapabilitySurface(
    Func<DesktopSession?> activeSession) : IDesktopDocumentCapabilitySurface
{
    public Task<CapabilityResult<DesktopDocumentPreviewResult>> OpenDocumentAsync(
        DesktopDocumentPreviewRequest request,
        DesktopCallContext call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);

        var session = activeSession();
        return session is null
            ? Task.FromResult(CapabilityResult<DesktopDocumentPreviewResult>.Failure(
                DesktopCapabilityError.NotConnected(
                    "document preview requires the capability channel; the legacy bridge has no preview command")))
            : session.OpenDocumentAsync(request, call, cancellationToken);
    }
}
