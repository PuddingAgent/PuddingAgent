namespace Pudding.Contracts.Desktop;

/// <summary>
/// 能力通道能覆盖的**面板渲染**操作（窄端口）。
///
/// 与 <see cref="IDesktopDocumentCapabilitySurface"/> 的关系：两者都是「把东西送到用户眼前」，
/// 但内容来源不同 —— 文档预览只传路径（由 Desktop 读盘），面板渲染把内容直接送过去。
/// 因此拆成两个端口：它们的失败模式与安全要求不一样（前者要守路径边界，后者要守渲染沙箱）。
///
/// 约定（与其它能力端口一致）：
/// · 参数顺序与 <c>Pudding.CapabilityBroker.DesktopSession</c> 的同名方法一致；
/// · 结果一律用 <see cref="CapabilityResult{T}"/> 表达，<b>不抛异常</b>；
/// · 实现方不得把内容回传。
/// </summary>
public interface IDesktopPanelCapabilitySurface
{
    /// <summary>
    /// 把一段自包含 HTML 推到工具区画布。同 <c>panelId</c> 幂等覆盖，不得开第二块画布。
    /// </summary>
    Task<CapabilityResult<DesktopPanelRenderResult>> RenderPanelAsync(
        DesktopPanelRenderRequest request,
        DesktopCallContext call,
        CancellationToken cancellationToken = default);
}
