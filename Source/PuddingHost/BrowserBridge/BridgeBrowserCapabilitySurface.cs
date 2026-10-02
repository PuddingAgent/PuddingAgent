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

    /// <summary>
    /// 页面快照。契约与运行时的选项/结果字段**逐项对应**（都是文本形态：dom / a11y / html），
    /// 因此这里是一次真实搬运：
    /// · 契约没有 `IncludeHidden`/`IncludeIframes`/`IncludeShadowDom`/`MaxDepth` 字段 ⇒ 一律沿用
    ///   运行时默认值（两条传输因此同形，不各写一份默认值）；
    /// · 期望版本不符 ⇒ 明确拒绝（**不得返回过期快照**，与 Desktop 侧同一条规则）；
    /// · 文本超预算 ⇒ 纵深防御再截一次并置 `Truncated`（运行时的标注原样保留）。
    /// </summary>
    public async Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
        BrowserSnapshotRequest request, DesktopCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return NotFound<DesktopSnapshot>(request.Target);
        }

        var version = LiveVersion(page);
        if (!version.IsKnown)
        {
            // 快照里的引用按版本失效：没有活版本的快照不可被引用 ⇒ 响亮失败。
            return CapabilityResult<DesktopSnapshot>.Failure(DesktopCapabilityError.Internal(
                "page has no live version; a snapshot without a version cannot be referenced"));
        }

        if (request.ExpectedPageVersion.IsKnown && request.ExpectedPageVersion.Value != version.Value)
        {
            return CapabilityResult<DesktopSnapshot>.Failure(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"expected page version {request.ExpectedPageVersion.Value} but the page is at {version.Value}; re-acquire state"));
        }

        var options = request.Options;
        var snapshot = await page.SnapshotAsync(
            new SnapshotOptions
            {
                IncludeDom = options.IncludeDom,
                IncludeAccessibilityTree = options.IncludeAccessibilityTree,
                IncludeHtml = options.IncludeHtml,
                MaxNodes = options.MaxNodes,
                MaxTextLength = options.MaxTextLength,
            },
            cancellationToken).ConfigureAwait(false);

        var truncated = snapshot.Truncated;
        var domText = Clamp(snapshot.DomText, options.MaxTextLength, ref truncated);
        var accessibilityTree = Clamp(snapshot.AccessibilityTree, options.MaxTextLength, ref truncated);
        var html = Clamp(snapshot.Html, options.MaxTextLength, ref truncated);

        return CapabilityResult<DesktopSnapshot>.Success(new DesktopSnapshot(
            request.Target, domText, accessibilityTree, html, truncated, snapshot.NodeCount, version));
    }

    /// <summary>纵深防御：超出预算就截断并**如实**把 <paramref name="truncated"/> 置真。</summary>
    private static string? Clamp(string? text, int maxLength, ref bool truncated)
    {
        if (text is null || text.Length <= maxLength)
        {
            return text;
        }

        truncated = true;
        return text[..maxLength];
    }

    public async Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
        BrowserLocateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return NotFound<DesktopLocateResult>(request.Target);
        }

        var version = LiveVersion(page);
        if (!version.IsKnown)
        {
            // 元素引用必须带活版本（契约硬要求）；没有就**响亮**失败，不返回无版本引用。
            return CapabilityResult<DesktopLocateResult>.Failure(DesktopCapabilityError.Internal(
                "page has no live version; element references would be unusable"));
        }

        // 与 Desktop 侧同一套语义：期望版本不符 ⇒ 明确拒绝（而不是让调用方拿到陈旧引用）。
        if (request.ExpectedPageVersion.IsKnown && request.ExpectedPageVersion.Value != version.Value)
        {
            return CapabilityResult<DesktopLocateResult>.Failure(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"expected page version {request.ExpectedPageVersion.Value} but the page is at {version.Value}; re-acquire state"));
        }

        if (ToRuntimeLocator(request.Locator) is not { } locator)
        {
            // 快照引用（Ref）在 Bridge 侧没有等价物：引用只在 Desktop 的快照注册表里有意义。
            return CapabilityResult<DesktopLocateResult>.Failure(DesktopCapabilityError.InvalidRequest(
                $"locator kind '{DesktopLocatorKindWire.NameOf(request.Locator.Kind)}' has no bridge equivalent"));
        }

        var matches = await page.QueryAllAsync(locator, cancellationToken).ConfigureAwait(false);
        var elements = new List<DesktopElementRef>(matches.Count);
        foreach (var handle in matches)
        {
            if (handle.PageVersion <= 0)
            {
                // 元素没有活版本 ⇒ 响亮失败（与 Desktop 侧同一条规则：绝不静默丢弃）。
                return CapabilityResult<DesktopLocateResult>.Failure(DesktopCapabilityError.Internal(
                    "element reference has no live page version"));
            }

            elements.Add(new DesktopElementRef(
                handle.Id.Value,
                handle.Info.Tag,
                DesktopPageVersion.Require(handle.PageVersion),
                handle.Info.Role,
                handle.Info.Name,
                handle.Info.Text,
                handle.Info.Visible,
                handle.Info.Enabled,
                handle.Info.Checked));
        }

        // 预算：超出请求上限就截断并**如实标注**。
        var truncated = elements.Count > request.MaxResults;
        if (truncated)
        {
            elements = elements.GetRange(0, request.MaxResults);
        }

        return CapabilityResult<DesktopLocateResult>.Success(
            new DesktopLocateResult(request.Target, request.Locator, elements, truncated, version));
    }

    public async Task<CapabilityResult<DesktopWaitResult>> WaitForAsync(
        BrowserWaitForRequest request, DesktopCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return NotFound<DesktopWaitResult>(request.Target);
        }

        var result = await page.WaitForAsync(ToRuntimeWaitCondition(request.Condition), cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            // 运行时错误文本可能含选择器/URL ⇒ 不透传，只表示"等失败了"（超时不算失败，见下）。
            return CapabilityResult<DesktopWaitResult>.Failure(
                DesktopCapabilityError.Internal("wait failed in the bridge runtime"));
        }

        // **超时是结果而不是失败**（与 Desktop 侧一致）。
        return CapabilityResult<DesktopWaitResult>.Success(new DesktopWaitResult(
            request.Target, request.Condition, result.TimedOut, BuildPageState(request.Target, page)));
    }

    public async Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
        BrowserInteractRequest request, DesktopCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return NotFound<DesktopInteractionResult>(request.Target);
        }

        var live = LiveVersion(page);
        if (!live.IsKnown)
        {
            return CapabilityResult<DesktopInteractionResult>.Failure(DesktopCapabilityError.Internal(
                "page has no live version; interaction cannot be acted on safely"));
        }

        if (live.Value != request.ExpectedPageVersion.Value)
        {
            return CapabilityResult<DesktopInteractionResult>.Failure(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"expected page version {request.ExpectedPageVersion.Value} but the page is at {live.Value}; re-acquire state"));
        }

        if (await ApplyInteractionAsync(page, request, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return CapabilityResult<DesktopInteractionResult>.Failure(error);
        }

        var state = BuildPageState(request.Target, page);

        // 变更类能力的**结果不变量**：版本必须严格推进（Desktop 侧的同一条检查才是权威，
        // 这里是 Bridge 路径上的纵深防御——不诚实的版本会让旧引用重新"有效"）。
        if (!state.Version.IsKnown || state.Version.Value <= request.ExpectedPageVersion.Value)
        {
            return CapabilityResult<DesktopInteractionResult>.Failure(DesktopCapabilityError.Internal(
                "mutating capability returned a page version that did not advance"));
        }

        return CapabilityResult<DesktopInteractionResult>.Success(new DesktopInteractionResult(request.Target, state));
    }

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

    /// <summary>能力形状的等待条件 → 运行时等待条件（三种一一对应）。</summary>
    private static WaitCondition ToRuntimeWaitCondition(DesktopWaitCondition condition) => condition.Kind switch
    {
        DesktopWaitConditionKind.Selector => new WaitCondition { Selector = condition.Value },
        DesktopWaitConditionKind.SelectorHidden => new WaitCondition { SelectorToHide = condition.Value },
        _ => new WaitCondition { UrlPattern = condition.Value },
    };

    /// <summary>
    /// 执行交互。返回 <c>null</c> 表示成功；返回错误表示**明确拒绝**（不支持的动作用
    /// <c>unsupported_capability</c>，与 Desktop 侧对 `focus` 的处理一致）。
    /// 动作所需的字段已由 <see cref="BrowserInteractRequest"/> 自身按动作校验，因此这里不再重复判空。
    /// </summary>
    private static async Task<DesktopCapabilityError?> ApplyInteractionAsync(
        IBrowserPage page, BrowserInteractRequest request, CancellationToken cancellationToken)
    {
        if (request.Action == DesktopInteractionAction.Scroll)
        {
            // 两个轴都要传：此前只映射了 DeltaY，等于**悄悄丢掉** DeltaX
            // （线缆与 Desktop 侧本来就带 delta_x，因此那是我这边的缺陷）。
            await page.ScrollAsync(
                new ScrollOptions { DeltaX = request.DeltaX, DeltaY = request.DeltaY },
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (request.Action == DesktopInteractionAction.Focus)
        {
            // 运行时没有 focus API，且不拿脚本绕过注入限制 ⇒ 明确拒绝（与 Desktop 侧同一口径）。
            return DesktopCapabilityError.UnsupportedCapability("focus has no bridge runtime equivalent");
        }

        if (ToRuntimeLocator(request.Locator!) is not { } locator)
        {
            return DesktopCapabilityError.InvalidRequest(
                $"locator kind '{DesktopLocatorKindWire.NameOf(request.Locator!.Kind)}' has no bridge equivalent");
        }

        switch (request.Action)
        {
            case DesktopInteractionAction.Click:
                await page.ClickAsync(locator, new ClickOptions(), cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Fill:
                await page.FillAsync(locator, request.Text!, new FillOptions(), cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Press:
                await page.PressAsync(locator, request.Text!, new KeyOptions(), cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Check:
            case DesktopInteractionAction.Uncheck:
                await page.CheckAsync(locator, request.Action == DesktopInteractionAction.Check, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Select:
                await page.SelectAsync(locator, request.Values!, cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Hover:
                await page.HoverAsync(locator, new PointerOptions(), cancellationToken).ConfigureAwait(false);
                break;
            default:
                return DesktopCapabilityError.UnsupportedCapability(
                    $"interaction action '{DesktopInteractionActionWire.NameOf(request.Action)}' has no bridge equivalent");
        }

        return null;
    }

    /// <summary>
    /// 能力形状的定位描述符 → 运行时定位器（十种一一对应，含 <c>Ref</c>——运行时的 <c>LocatorKind</c>
    /// 本身就支持快照引用，因此"Ref 无等价物"是**纠正过的**早先判断）。
    /// 未知/未登记的 kind 返回 <c>null</c>，由调用方明确拒绝。
    /// </summary>
    private static Locator? ToRuntimeLocator(DesktopLocator locator)
    {
        var kind = locator.Kind switch
        {
            DesktopLocatorKind.Ref => LocatorKind.Ref,
            DesktopLocatorKind.Css => LocatorKind.Css,
            DesktopLocatorKind.XPath => LocatorKind.XPath,
            DesktopLocatorKind.Text => LocatorKind.Text,
            DesktopLocatorKind.Role => LocatorKind.Role,
            DesktopLocatorKind.Label => LocatorKind.Label,
            DesktopLocatorKind.Placeholder => LocatorKind.Placeholder,
            DesktopLocatorKind.AltText => LocatorKind.AltText,
            DesktopLocatorKind.Title => LocatorKind.Title,
            DesktopLocatorKind.TestId => LocatorKind.TestId,
            _ => (LocatorKind?)null,
        };

        return kind is not { } resolved
            ? null
            : new Locator
            {
                Kind = resolved,
                Value = locator.Value,
                Name = locator.Name,
                Exact = locator.Exact,
                Nth = locator.Nth,
                HasText = locator.HasText,
            };
    }

    private static CapabilityResult<T> NotFound<T>(DesktopPageTarget target) =>
        CapabilityResult<T>.Failure(DesktopCapabilityError.InvalidTarget(
            $"page target '{target.Key}' was not found in the bridge runtime"));
}
