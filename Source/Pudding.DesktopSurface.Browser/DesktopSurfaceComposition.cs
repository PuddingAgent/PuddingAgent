using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace Pudding.DesktopSurface.Browser;

/// <summary>
/// 把两个协作者组装成 DesktopService 需要的单一 <see cref="IDesktopUiSurface"/>：
/// · 浏览器 9 项 → <see cref="BrowserRuntimeDesktopSurface"/>（映射到既有 <c>IBrowserRuntime</c>）；
/// · 对话框/文件选择器/剪贴板/通知/只读状态 5 项 → <see cref="DesktopShellSurface"/>
///   （它带预算纵深防御与「异常不越界」归一）。
///
/// 为什么组装要独立成类：DesktopService 的接缝是「一个表面」，而实现被刻意拆成不同所有者
/// （浏览器映射 / WinUI 交互设施 / Shell 自身）。组装本身<b>没有逻辑</b>——只是把 14 个成员各归其位，
/// 因此可以用假协作者逐个钉住「哪个成员走哪个实现」，避免接线张冠李戴
/// （张冠李戴的后果是能力"能调用但做错事"，比编译错误难发现得多）。
/// </summary>
public sealed class DesktopSurfaceComposition : IDesktopUiSurface
{
    private readonly BrowserRuntimeDesktopSurface _browser;
    private readonly DesktopShellSurface _shell;

    public DesktopSurfaceComposition(
        BrowserRuntimeDesktopSurface browser,
        DesktopShellSurface shell)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    // ── 浏览器 9 项 ────────────────────────────────────────────────────────

    public Task<CapabilityResult<NavigateResult>> NavigateAsync(
        DesktopCallContext context, NavigateRequest request, CancellationToken cancellationToken) =>
        _browser.NavigateAsync(context, request, cancellationToken);

    public Task<CapabilityResult<JavascriptResult>> ExecuteJavascriptAsync(
        DesktopCallContext context, JavascriptRequest request, CancellationToken cancellationToken) =>
        _browser.ExecuteJavascriptAsync(context, request, cancellationToken);

    public Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
        DesktopCallContext context, DesktopPageTarget target, CancellationToken cancellationToken) =>
        _browser.GetPageStateAsync(context, target, cancellationToken);

    public Task<CapabilityResult<DesktopTabsResult>> TabsAsync(
        DesktopCallContext context, BrowserTabsRequest request, CancellationToken cancellationToken) =>
        _browser.TabsAsync(context, request, cancellationToken);

    public Task<CapabilityResult<DesktopContexts>> GetContextsAsync(
        DesktopCallContext context, CancellationToken cancellationToken) =>
        _browser.GetContextsAsync(context, cancellationToken);

    public Task<CapabilityResult<DesktopContextInfo>> CreateContextAsync(
        DesktopCallContext context, BrowserContextCreateRequest request, CancellationToken cancellationToken) =>
        _browser.CreateContextAsync(request, context, cancellationToken);

    public Task<CapabilityResult<DesktopContextClosed>> CloseContextAsync(
        DesktopCallContext context, BrowserContextCloseRequest request, CancellationToken cancellationToken) =>
        _browser.CloseContextAsync(request, context, cancellationToken);

    public Task<CapabilityResult<DesktopWaitResult>> WaitForAsync(
        DesktopCallContext context, BrowserWaitForRequest request, CancellationToken cancellationToken) =>
        _browser.WaitForAsync(context, request, cancellationToken);

    public Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
        DesktopCallContext context, BrowserInteractRequest request, CancellationToken cancellationToken) =>
        _browser.InteractAsync(context, request, cancellationToken);

    public Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
        DesktopCallContext context, BrowserLocateRequest request, CancellationToken cancellationToken) =>
        _browser.LocateAsync(context, request, cancellationToken);

    public Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
        DesktopCallContext context, BrowserSnapshotRequest request, CancellationToken cancellationToken) =>
        _browser.SnapshotAsync(context, request, cancellationToken);

    // ── Shell 5 项 ─────────────────────────────────────────────────────────

    public Task<CapabilityResult<DesktopDialogResult>> RequestDialogAsync(
        DesktopCallContext context, DesktopDialogRequest request, CancellationToken cancellationToken) =>
        _shell.RequestDialogAsync(context, request, cancellationToken);

    public Task<CapabilityResult<DesktopFilePickerResult>> RequestFilePickerAsync(
        DesktopCallContext context, DesktopFilePickerRequest request, CancellationToken cancellationToken) =>
        _shell.RequestFilePickerAsync(context, request, cancellationToken);

    public Task<CapabilityResult<DesktopClipboardContent>> ReadClipboardAsync(
        DesktopCallContext context, ClipboardReadRequest request, CancellationToken cancellationToken) =>
        _shell.ReadClipboardAsync(context, request, cancellationToken);

    public Task<CapabilityResult<DesktopNotificationResult>> ShowNotificationAsync(
        DesktopCallContext context, DesktopNotificationRequest request, CancellationToken cancellationToken) =>
        _shell.ShowNotificationAsync(context, request, cancellationToken);

    public Task<CapabilityResult<DesktopShellStatus>> GetShellStatusAsync(
        DesktopCallContext context, CancellationToken cancellationToken) =>
        _shell.GetShellStatusAsync(context, cancellationToken);
}
