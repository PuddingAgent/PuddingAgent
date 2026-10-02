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

    // ── 上下文与标签页 ────────────────────────────────────────────────

    /// <summary>
    /// 列出上下文与页面。三条诚实约定：
    /// · 页面**没有活版本就不进清单**（契约本身也禁止：`DesktopPageInfo` 无活版本即抛）；
    /// · 就绪度只按 `IsLoading` 说 `Loading`/`Unknown`；
    /// · `IsActive`/`IsAgentTarget`/`Trust` 在 Bridge 侧**无从得知**，一律取保守值
    ///   （`false` / `Untrusted`）—— Desktop 侧的目标注册表才是这些字段的权威，
    ///   因此**工具不得用它们做准入判断**（这是两条传输之间的已知不对称，已登记）。
    /// </summary>
    public async Task<CapabilityResult<DesktopContexts>> GetContextsAsync(
        DesktopCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var summaries = await _runtime.ListContextsAsync(cancellationToken).ConfigureAwait(false);
        var contexts = new List<DesktopContextInfo>(summaries.Count);
        var observed = DesktopPageVersion.Unknown;

        foreach (var summary in summaries)
        {
            if (await _runtime.GetContextAsync(summary.Id, cancellationToken).ConfigureAwait(false) is not { } context)
            {
                // 清单里有、实体取不到：跳过，而不是编一个空上下文出来。
                continue;
            }

            var pages = await context.ListPagesAsync(cancellationToken).ConfigureAwait(false);
            var pageInfos = new List<DesktopPageInfo>(pages.Count);

            foreach (var listed in pages)
            {
                // 逐个取实体是为了拿到**活版本**与真实的前进/后退/加载标志
                // （清单里的 PageInfo 没有这些）；拿不到实体或拿不到活版本的页面不进清单。
                if (await context.GetPageAsync(listed.Id, cancellationToken).ConfigureAwait(false) is not { } live)
                {
                    continue;
                }

                if (BuildPageInfo(context.Id, live) is not { } info)
                {
                    continue;
                }

                pageInfos.Add(info);
                observed = Max(observed, info.Version);
            }

            contexts.Add(new DesktopContextInfo(context.Id.Value, DesktopContextTrust.Untrusted, pageInfos));
        }

        return CapabilityResult<DesktopContexts>.Success(new DesktopContexts(contexts, observed));
    }

    public async Task<CapabilityResult<DesktopTabsResult>> TabsAsync(
        BrowserTabsRequest request, DesktopCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await ResolveContextAsync(request.Target, cancellationToken).ConfigureAwait(false) is not { } context
            || await context.GetPageAsync(new PageId(request.Target.PageId), cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return NotFound<DesktopTabsResult>(request.Target);
        }

        // 状态要在**操作之前**取：关闭之后页面就没了，那时再读 Info 不可靠。
        var state = BuildPageState(request.Target, page);

        var tabClosed = false;
        switch (request.Action)
        {
            case DesktopTabAction.Activate:
                await page.BringToFrontAsync(cancellationToken).ConfigureAwait(false);
                break;
            case DesktopTabAction.Close:
                await context.ClosePageAsync(new PageId(request.Target.PageId), cancellationToken).ConfigureAwait(false);
                tabClosed = true;
                break;
            default:
                return CapabilityResult<DesktopTabsResult>.Failure(DesktopCapabilityError.InvalidRequest(
                    $"unsupported tab action '{DesktopTabActionWire.NameOf(request.Action)}'"));
        }

        // 操作后必须回带**新的**剩余清单（契约要求），因此复用上下文清单的实现。
        var remaining = await GetContextsAsync(call, cancellationToken).ConfigureAwait(false);
        if (remaining.IsFailure)
        {
            return CapabilityResult<DesktopTabsResult>.Failure(remaining.Error!);
        }

        return CapabilityResult<DesktopTabsResult>.Success(
            new DesktopTabsResult(request.Target, request.Action, state, tabClosed, remaining.Value));
    }

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

    private async Task<IBrowserContext?> ResolveContextAsync(DesktopPageTarget target, CancellationToken cancellationToken) =>
        await _runtime
            .GetContextAsync(new BrowserContextId(target.ContextId), cancellationToken)
            .ConfigureAwait(false);

    private async Task<IBrowserPage?> ResolvePageAsync(DesktopPageTarget target, CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(target, cancellationToken).ConfigureAwait(false);

        return context is null
            ? null
            : await context.GetPageAsync(new PageId(target.PageId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 页面清单项：**没有活版本就返回 null**（契约禁止无版本项，引用会失去版本依据）。
    /// </summary>
    private static DesktopPageInfo? BuildPageInfo(BrowserContextId contextId, IBrowserPage page)
    {
        if (page.PageVersion <= 0)
        {
            return null;
        }

        return new DesktopPageInfo(
            new DesktopPageTarget(contextId.Value, page.Id.Value),
            DesktopPageVersion.Require(page.PageVersion),
            page.Info.Title,
            TryParseUrl(page.Info.Url),
            // Bridge 侧不知道活动页与授权目标：取保守值，且工具不得据此做准入判断。
            isActive: false,
            isAgentTarget: false,
            canGoBack: page.CanGoBack,
            canGoForward: page.CanGoForward,
            isLoading: page.IsLoading);
    }

    private static DesktopPageState BuildPageState(DesktopPageTarget target, IBrowserPage page) =>
        new(
            target,
            TryParseUrl(page.Info.Url),
            LiveVersion(page),
            page.IsLoading ? DesktopPageReadiness.Loading : DesktopPageReadiness.Unknown);

    private static DesktopPageVersion Max(DesktopPageVersion left, DesktopPageVersion right) =>
        !right.IsKnown ? left
        : !left.IsKnown ? right
        : right.Value > left.Value ? right : left;

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
