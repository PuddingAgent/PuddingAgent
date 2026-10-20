namespace Pudding.Contracts.Desktop;

/// <summary>
/// 能力通道能覆盖的**文档预览**操作（窄端口）。
///
/// 为什么与 <see cref="IDesktopBrowserCapabilitySurface"/> 分开而不是塞进去：
/// 浏览器端口的前提是「每个操作都有页面目标、都需要 caller 身份投影」，
/// 而文档预览两者都没有。混进去会让那条前提在阅读与审计时失真，
/// 也会让实现方被迫在浏览器适配器里做一件与浏览器无关的事。
///
/// 约定（与浏览器端口一致）：
/// · 参数顺序与 <c>Pudding.CapabilityBroker.DesktopSession</c> 的同名方法一致
///   （后者直接实现本端口，不需要适配代码）；
/// · 结果一律用 <see cref="CapabilityResult{T}"/> 表达，<b>不抛异常</b>；
/// · 实现方不得把正文、HTML 或绝对路径回传。
/// </summary>
public interface IDesktopDocumentCapabilitySurface
{
    /// <summary>
    /// 请 Desktop 在工具区打开一份文档（只传相对路径）。
    /// 同路径重复请求必须幂等激活同一视图（<c>AlreadyOpen</c>），不得开第二个标签。
    /// </summary>
    Task<CapabilityResult<DesktopDocumentPreviewResult>> OpenDocumentAsync(
        DesktopDocumentPreviewRequest request,
        DesktopCallContext call,
        CancellationToken cancellationToken = default);
}
