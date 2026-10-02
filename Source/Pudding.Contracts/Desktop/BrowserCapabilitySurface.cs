namespace Pudding.Contracts.Desktop;

/// <summary>
/// 能力通道**能覆盖**的浏览器操作（切片 D 的窄端口）。
///
/// 为什么刻意这么窄：七个浏览器工具目前依赖 <c>IBrowserRuntime</c>，而它连同
/// <c>IBrowserContext</c>/<c>IBrowserPage</c> 有 40+ 个成员（CDP、截图、PDF、DevTools、订阅、
/// 拖拽、选择、勾选、上传文件、cookie、权限、前进后退…），能力通道只覆盖下面这九个。
/// 在宽接口上做「整体替换」会让二十多个成员只能在**运行期**抛 <c>NotSupported</c>——
/// 编译期完全看不出来，工具被打成半残。把这个端口抽出来之后，
/// 「通道覆盖不到的操作」在**类型层面**就不存在。
///
/// 约定：
/// · 参数顺序与 <c>Pudding.CapabilityBroker.DesktopSession</c> 的同名方法一致
///   （后者直接实现本端口，不需要任何适配代码）；
/// · 结果一律用 <see cref="CapabilityResult{T}"/> 表达，<b>不抛异常</b>；
/// · 两个实现（能力通道 / 既有 Bridge）必须给出**同形**的结果，由 parity 测试钉住。
/// </summary>
public interface IDesktopBrowserCapabilitySurface
{
    /// <summary>列出上下文与页面；每个页面必须带**有效版本**（没有版本的页面不进清单）。</summary>
    Task<CapabilityResult<DesktopContexts>> GetContextsAsync(
        DesktopCallContext call, CancellationToken cancellationToken = default);

    /// <summary>只读页面状态：确认 URL/版本/就绪度，不返回页面内容。</summary>
    Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
        DesktopPageTarget target, DesktopCallContext call, CancellationToken cancellationToken = default);

    /// <summary>标签页清单/激活/关闭；操作后必须回带**新的**活动页与剩余清单。</summary>
    Task<CapabilityResult<DesktopTabsResult>> TabsAsync(
        BrowserTabsRequest request, DesktopCallContext call, CancellationToken cancellationToken = default);

    /// <summary>导航；结果必须携带**新**页面版本（旧引用自此作废）。</summary>
    Task<CapabilityResult<NavigateResult>> NavigateAsync(
        NavigateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default);

    /// <summary>页面快照；预算由请求给出，实现方必须截断并如实标注。</summary>
    Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
        BrowserSnapshotRequest request, DesktopCallContext call, CancellationToken cancellationToken = default);

    /// <summary>按定位描述符查找元素；每个命中项必须标注当前页面版本。</summary>
    Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
        BrowserLocateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default);

    /// <summary>对元素执行交互；**必须**在交互后返回新的页面状态（旧 Ref 随之作废）。</summary>
    Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
        BrowserInteractRequest request, DesktopCallContext call, CancellationToken cancellationToken = default);

    /// <summary>等待条件满足；超时是结果（<c>TimedOut</c>）而不是失败。</summary>
    Task<CapabilityResult<DesktopWaitResult>> WaitForAsync(
        BrowserWaitForRequest request, DesktopCallContext call, CancellationToken cancellationToken = default);

    /// <summary>在页面里执行脚本；按设计**不推进**页面版本。</summary>
    Task<CapabilityResult<JavascriptResult>> ExecuteJavascriptAsync(
        JavascriptRequest request, DesktopCallContext call, CancellationToken cancellationToken = default);
}
