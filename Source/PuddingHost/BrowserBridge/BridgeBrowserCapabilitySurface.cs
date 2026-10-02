using System.Text;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// 既有 Bridge 侧的**窄端口**实现（切片 D 第③步的一半）：把"能力形状"的请求翻译到
/// <see cref="IBrowserRuntime"/>，从而让七个浏览器工具在通道未启用时走**同一条端口路径**
/// （组合根按开关二选一，不做跨传输回退）。
///
/// <b>逐操作迁移中</b>：目前实现 `page_state` / `navigate` / `execute_javascript`；
/// 其余操作返回**明确的** `unsupported_capability`——不静默失败，也不返回伪结果，
/// 直到对应映射补齐（映射规格 §8.4 的顺序：先窄端口，再逐操作替换调用点）。
///
/// 两条不撒谎的约定：
/// · 版本只报运行时真正知道的（`PageVersion &gt; 0` 才转成已知版本，否则 `Unknown`）；
/// · 就绪度不猜：`IsLoading` 只能区分"加载中"与"未知"，**绝不**声称 Interactive/Complete。
/// </summary>
internal sealed class BridgeBrowserCapabilitySurface(IBrowserRuntime runtime) : IDesktopBrowserCapabilitySurface
{
    private static readonly Encoding Utf8 = Encoding.UTF8;

    private readonly IBrowserRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public async Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
        DesktopPageTarget target, DesktopCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (await ResolvePageAsync(target, cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return NotFound<DesktopPageState>(target);
        }

        return CapabilityResult<DesktopPageState>.Success(new DesktopPageState(
            target,
            TryParseUrl(page.Info.Url),
            LiveVersion(page),
            page.IsLoading ? DesktopPageReadiness.Loading : DesktopPageReadiness.Unknown));
    }

    public async Task<CapabilityResult<NavigateResult>> NavigateAsync(
        NavigateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return NotFound<NavigateResult>(request.Target);
        }

        var result = await page.GotoAsync(request.Url, new NavigationOptions(), cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            // 错误原文可能含 URL 或页面片段 ⇒ 只回状态码，不透传。
            return CapabilityResult<NavigateResult>.Failure(DesktopCapabilityError.Internal(
                result.StatusCode is { } status ? $"navigation failed (status {status})" : "navigation failed"));
        }

        // 导航已完成 ⇒ 版本取导航后的当前值（旧引用自此作废由 Core 侧按版本比较实现）。
        return CapabilityResult<NavigateResult>.Success(new NavigateResult(
            NavigateDisposition.Completed, result.Url, LiveVersion(page)));
    }

    public async Task<CapabilityResult<JavascriptResult>> ExecuteJavascriptAsync(
        JavascriptRequest request, DesktopCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return NotFound<JavascriptResult>(request.Target);
        }

        var value = await page
            .EvaluateAsync(new BrowserScript { Source = request.Script }, cancellationToken)
            .ConfigureAwait(false);

        var kind = KindOf(value);
        var json = value.Value?.GetRawText();

        // 预算：超限就**截断并如实标注**，绝不回传半截 JSON（那会被当成合法结果）。
        if (json is not null && Utf8.GetByteCount(json) > request.MaxResultBytes)
        {
            return CapabilityResult<JavascriptResult>.Success(
                new JavascriptResult(kind, JsonValue: null, Truncated: true));
        }

        return CapabilityResult<JavascriptResult>.Success(new JavascriptResult(kind, json, Truncated: false));
    }

    // ── 尚未迁移的操作：明确拒绝，不静默 ────────────────────────────────

    public Task<CapabilityResult<DesktopContexts>> GetContextsAsync(
        DesktopCallContext call, CancellationToken cancellationToken = default) =>
        NotMigrated<DesktopContexts>(DesktopCapability.BrowserContexts);

    public Task<CapabilityResult<DesktopTabsResult>> TabsAsync(
        BrowserTabsRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        NotMigrated<DesktopTabsResult>(DesktopCapability.BrowserTabs);

    public Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
        BrowserSnapshotRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        NotMigrated<DesktopSnapshot>(DesktopCapability.BrowserSnapshot);

    public Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
        BrowserLocateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        NotMigrated<DesktopLocateResult>(DesktopCapability.BrowserLocate);

    public Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
        BrowserInteractRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        NotMigrated<DesktopInteractionResult>(DesktopCapability.BrowserInteract);

    public Task<CapabilityResult<DesktopWaitResult>> WaitForAsync(
        BrowserWaitForRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        NotMigrated<DesktopWaitResult>(DesktopCapability.BrowserWaitFor);

    // ── 内部 ───────────────────────────────────────────────────────────

    private async Task<IBrowserPage?> ResolvePageAsync(DesktopPageTarget target, CancellationToken cancellationToken)
    {
        var context = await _runtime
            .GetContextAsync(new BrowserContextId(target.ContextId), cancellationToken)
            .ConfigureAwait(false);

        return context is null
            ? null
            : await context.GetPageAsync(new PageId(target.PageId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>版本只报运行时真正知道的：没有活版本就是 <c>Unknown</c>，绝不编一个。</summary>
    private static DesktopPageVersion LiveVersion(IBrowserPage page) =>
        page.PageVersion > 0 ? DesktopPageVersion.Require(page.PageVersion) : DesktopPageVersion.Unknown;

    private static Uri? TryParseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null;

    /// <summary>脚本值的线上类型：按 Playwright 的 type/subtype 语义映射，判不出就按是否有值折叠。</summary>
    private static JavascriptValueKind KindOf(BrowserScriptValue value) => value.Type switch
    {
        "undefined" => JavascriptValueKind.Undefined,
        "object" when string.Equals(value.Subtype, "null", StringComparison.Ordinal) => JavascriptValueKind.Null,
        "boolean" => JavascriptValueKind.Boolean,
        "number" => JavascriptValueKind.Number,
        "string" => JavascriptValueKind.String,
        _ => value.Value is null ? JavascriptValueKind.Undefined : JavascriptValueKind.Json,
    };

    private static CapabilityResult<T> NotFound<T>(DesktopPageTarget target) =>
        CapabilityResult<T>.Failure(DesktopCapabilityError.InvalidTarget(
            $"page target '{target.Key}' was not found in the bridge runtime"));

    private static Task<CapabilityResult<T>> NotMigrated<T>(DesktopCapability capability) =>
        Task.FromResult(CapabilityResult<T>.Failure(DesktopCapabilityError.UnsupportedCapability(
            $"{DesktopCapabilities.NameOf(capability)} is not migrated to the narrow port yet")));
}
