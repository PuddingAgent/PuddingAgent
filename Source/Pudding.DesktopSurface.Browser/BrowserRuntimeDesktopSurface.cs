using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using PuddingBrowser.Abstractions;

namespace Pudding.DesktopSurface.Browser;

/// <summary>
/// 浏览器侧表面：把 <see cref="IDesktopUiSurface"/> 的浏览器操作**映射到既有浏览器抽象**
/// （<see cref="IBrowserRuntime"/>），而不是重建 WebView2 逻辑——现有七个浏览器工具正基于该抽象工作。
///
/// 本轮只实现 <see cref="GetContextsAsync"/>（清单是"先看清有什么"的入口，且不需要任何 DOM 交互），
/// 其余操作按 [映射规格](../../Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md) 逐步补齐。
/// </summary>
public sealed class BrowserRuntimeDesktopSurface
{
    private readonly IBrowserRuntime _runtime;
    private readonly IDesktopBrowserTargetRegistry _targets;

    public BrowserRuntimeDesktopSurface(IBrowserRuntime runtime, IDesktopBrowserTargetRegistry targets)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    /// <summary>
    /// 列出上下文与页面。每个页面必须带**有效版本**——没有版本的页面**不进清单**
    /// （引用会失去版本依据；把 0 当版本返回等于给出假引用）。
    /// </summary>
    public async Task<CapabilityResult<DesktopContexts>> GetContextsAsync(
        DesktopCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_runtime.State != BrowserRuntimeState.Ready)
        {
            return CapabilityResult<DesktopContexts>.Failure(
                DesktopCapabilityError.UiUnavailable($"browser runtime is {_runtime.State}"));
        }

        var contexts = await _runtime.ListContextsAsync(cancellationToken).ConfigureAwait(false);
        var mapped = new List<DesktopContextInfo>(contexts.Count);
        var observedVersion = 0L;

        foreach (var info in contexts)
        {
            var browserContext = await _runtime.GetContextAsync(info.Id, cancellationToken).ConfigureAwait(false);
            if (browserContext is null)
            {
                // 清单与实例不一致（该上下文正在关闭）：如实跳过，不编造。
                continue;
            }

            var pages = await browserContext.ListPagesAsync(cancellationToken).ConfigureAwait(false);
            var mappedPages = new List<DesktopPageInfo>(pages.Count);
            var active = _targets.ActivePage;

            foreach (var page in pages)
            {
                if (page.PageVersion <= 0)
                {
                    // 版本无效 ⇒ 该页面的引用没有版本依据，不进清单（宁可少报，不报假引用）。
                    continue;
                }

                mappedPages.Add(new DesktopPageInfo(
                    new DesktopPageTarget(info.Id.Value, page.Id.Value),
                    new DesktopPageVersion(page.PageVersion),
                    title: page.Title,
                    url: ParseUrl(page.Url),
                    isActive: active is { } activePage
                        && string.Equals(activePage.ContextId, info.Id.Value, StringComparison.Ordinal)
                        && string.Equals(activePage.PageId, page.Id.Value, StringComparison.Ordinal),
                    isAgentTarget: _targets.IsAgentTarget(info.Id.Value, page.Id.Value)));

                observedVersion = Math.Max(observedVersion, page.PageVersion);
            }

            mapped.Add(new DesktopContextInfo(info.Id.Value, _targets.TrustFor(info.Id.Value), mappedPages) { Persistent = info.Persistent });
        }

        return CapabilityResult<DesktopContexts>.Success(new DesktopContexts(
            mapped,
            observedVersion > 0 ? new DesktopPageVersion(observedVersion) : default));
    }

    /// <summary>
    /// 读取页面状态：确认导航结果与**当前版本**；不返回页面内容。
    ///
    /// 就绪度的诚实边界：既有抽象只暴露 <see cref="IBrowserPage.IsLoading"/>，
    /// 无法区分 Interactive/Complete/Failed ⇒ 这里只报 <see cref="DesktopPageReadiness.Loading"/>
    /// 或 <see cref="DesktopPageReadiness.Unknown"/>，**不假装**已就绪（需要就绪请用 wait_for）。
    /// 版本缺失时返回 <see cref="DesktopPageVersion.Unknown"/>（调用方据此知道"没有版本依据"）。
    /// </summary>
    public async Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
        DesktopCallContext context, DesktopPageTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);

        if (_runtime.State != BrowserRuntimeState.Ready)
        {
            return CapabilityResult<DesktopPageState>.Failure(
                DesktopCapabilityError.UiUnavailable($"browser runtime is {_runtime.State}"));
        }

        var browserContext = await _runtime
            .GetContextAsync(new BrowserContextId(target.ContextId), cancellationToken).ConfigureAwait(false);
        if (browserContext is null)
        {
            return CapabilityResult<DesktopPageState>.Failure(
                DesktopCapabilityError.InvalidTarget($"context '{target.ContextId}' is not known"));
        }

        var page = await browserContext.GetPageAsync(new PageId(target.PageId), cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return CapabilityResult<DesktopPageState>.Failure(
                DesktopCapabilityError.InvalidTarget($"page '{target.Key}' is not known"));
        }

        var version = page.PageVersion > 0 ? page.PageVersion : page.Info.PageVersion;

        return CapabilityResult<DesktopPageState>.Success(new DesktopPageState(
            target,
            ParseUrl(page.Info.Url),
            version > 0 ? new DesktopPageVersion(version) : DesktopPageVersion.Unknown,
            page.IsLoading ? DesktopPageReadiness.Loading : DesktopPageReadiness.Unknown, page.Info.Title));
    }
    /// <summary>
    /// 标签页激活/关闭（变更类，**必须固定版本**）：版本不符即拒绝，绝不猜"用户指的是哪个页面"。
    ///
    /// 已知边界（诚实登记）：关闭**最后一个**页面后没有活动页，而 <see cref="DesktopTabsResult.Page"/>
    /// 目前不允许为空 ⇒ 此时回带被关闭页面**关闭前**的状态（`Remaining` 为空已足以让调用方知道没有页面了）。
    /// 若产品真需要"关掉最后一个页面"，应把该字段改为可空（契约变更，需同步 wire 与探针）。
    /// </summary>
    public async Task<CapabilityResult<DesktopTabsResult>> TabsAsync(
        DesktopCallContext context, BrowserTabsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (_runtime.State != BrowserRuntimeState.Ready)
        {
            return CapabilityResult<DesktopTabsResult>.Failure(
                DesktopCapabilityError.UiUnavailable($"browser runtime is {_runtime.State}"));
        }

        if (request.Action == DesktopTabAction.New)
        {
            // 新建标签页：没有既有页面 ⇒ 不钉版本；上下文来自请求本身。
            var newTabContext = await _runtime
                .GetContextAsync(new BrowserContextId(request.ContextId), cancellationToken).ConfigureAwait(false);
            if (newTabContext is null)
            {
                return CapabilityResult<DesktopTabsResult>.Failure(
                    DesktopCapabilityError.InvalidTarget($"context '{request.ContextId}' is not known"));
            }

            // 初始地址与是否激活交给运行时一次性完成（与迁移前工具行为一致，且少两次往返）。
            var created = await newTabContext
                .NewPageAsync(
                    new PageCreateOptions { InitialUrl = request.Url, Activate = request.Activate },
                    cancellationToken)
                .ConfigureAwait(false);

            var afterNew = await GetContextsAsync(context, cancellationToken).ConfigureAwait(false);
            if (afterNew.IsFailure)
            {
                return CapabilityResult<DesktopTabsResult>.Failure(afterNew.Error);
            }

            var createdTarget = new DesktopPageTarget(request.ContextId, created.Id.Value);
            return CapabilityResult<DesktopTabsResult>.Success(new DesktopTabsResult(
                createdTarget,
                request.Action,
                new DesktopPageState(
                    createdTarget,
                    ParseUrl(created.Info.Url),
                    LiveVersion(created.PageVersion),
                    created.IsLoading ? DesktopPageReadiness.Loading : DesktopPageReadiness.Unknown,
                    created.Info.Title),
                tabClosed: false,
                afterNew.Value));
        }

        // 其余动作作用于既有页面（新建分支已返回 ⇒ 这里 Target 必非空）。
        var target = request.Target!;
        var browserContext = await _runtime
            .GetContextAsync(new BrowserContextId(target.ContextId), cancellationToken).ConfigureAwait(false);
        if (browserContext is null)
        {
            return CapabilityResult<DesktopTabsResult>.Failure(
                DesktopCapabilityError.InvalidTarget($"context '{target.ContextId}' is not known"));
        }

        var pageId = new PageId(target.PageId);
        var page = await browserContext.GetPageAsync(pageId, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return CapabilityResult<DesktopTabsResult>.Failure(
                DesktopCapabilityError.InvalidTarget($"page '{target.Key}' is not known"));
        }

        // 变更类必须固定版本：版本不符说明目标页在等待期间已变化，切换/关闭的可能是另一个页面。
        if (page.PageVersion != request.ExpectedPageVersion.Value)
        {
            return CapabilityResult<DesktopTabsResult>.Failure(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"page '{target.Key}' is at v{page.PageVersion}, request pinned v{request.ExpectedPageVersion.Value}",
                retryable: true));
        }

        if (request.Action == DesktopTabAction.Close)
        {
            await browserContext.ClosePageAsync(pageId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await page.BringToFrontAsync(cancellationToken).ConfigureAwait(false);
        }

        var remaining = await GetContextsAsync(context, cancellationToken).ConfigureAwait(false);
        if (remaining.IsFailure)
        {
            return CapabilityResult<DesktopTabsResult>.Failure(remaining.Error);
        }

        var active = ResolveActivePage(browserContext.Id.Value, remaining.Value)
            ?? ResolveFallbackPage(target, page);

        return CapabilityResult<DesktopTabsResult>.Success(new DesktopTabsResult(
            target,
            request.Action,
            active,
            tabClosed: request.Action == DesktopTabAction.Close,
            remaining.Value));
    }

    /// <summary>操作后的活动页：优先信注册表（Desktop 知道焦点），它必须仍在剩余清单里。</summary>
    private DesktopPageState? ResolveActivePage(string contextId, DesktopContexts remaining)
    {
        if (_targets.ActivePage is not { } active
            || !string.Equals(active.ContextId, contextId, StringComparison.Ordinal))
        {
            return null;
        }

        var page = remaining.Contexts
            .SelectMany(candidate => candidate.Pages)
            .FirstOrDefault(candidate => string.Equals(candidate.Target.PageId, active.PageId, StringComparison.Ordinal));

        return page is null
            ? null
            : new DesktopPageState(page.Target, page.Url, page.Version, DesktopPageReadiness.Unknown, page.Title);
    }

    /// <summary>注册表还没更新时的回退：取剩余清单里的第一页（关闭时目标页已不在清单里）。</summary>
    private static DesktopPageState ResolveFallbackPage(
        DesktopPageTarget target, IBrowserPage page)
    {
        // 关闭最后一个页面时没有活动页可用：如实回带该页面关闭前的状态（Remaining 为空）。
        return new DesktopPageState(
            target,
            ParseUrl(page.Info.Url),
            page.PageVersion > 0 ? new DesktopPageVersion(page.PageVersion) : DesktopPageVersion.Unknown,
            DesktopPageReadiness.Unknown, page.Info.Title);
    }
    /// <summary>
    /// 定位元素。两条不变量在这里守住：
    /// ①**凭 Ref 定位必须配套来源版本**，且版本不符即拒绝（引用随版本失效）；
    /// ②**每个 Ref 的版本取元素自身**（<see cref="IElementHandle.PageVersion"/>），
    ///   绝不用"页面当前版本"替代——元素来自哪一版就标哪一版。
    /// 命中 0 个不是错误（调用方据此决定等待或换策略）；被裁剪必须如实标注。
    /// </summary>
    public async Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
        DesktopCallContext context, BrowserLocateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (_runtime.State != BrowserRuntimeState.Ready)
        {
            return CapabilityResult<DesktopLocateResult>.Failure(
                DesktopCapabilityError.UiUnavailable($"browser runtime is {_runtime.State}"));
        }

        var page = await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (page.IsFailure)
        {
            return CapabilityResult<DesktopLocateResult>.Failure(page.Error);
        }

        var browserPage = page.Value;
        var pageVersion = browserPage.PageVersion;

        // 调用方给了版本约束（Ref 定位必然有）就必须核对：不符说明引用/目标已过期。
        if (request.ExpectedPageVersion.IsKnown && pageVersion != request.ExpectedPageVersion.Value)
        {
            return CapabilityResult<DesktopLocateResult>.Failure(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"page '{request.Target.Key}' is at v{pageVersion}, request pinned v{request.ExpectedPageVersion.Value}",
                retryable: true));
        }

        var runtimeLocator = ToRuntimeLocator(request.Locator);

        if (request.Locator.IsReference)
        {
            var handle = await browserPage.QueryAsync(runtimeLocator, cancellationToken).ConfigureAwait(false);
            if (handle is null)
            {
                // 引用无效（元素已消失）⇒ 空结果是**成功**，不是错误。
                return CapabilityResult<DesktopLocateResult>.Success(new DesktopLocateResult(
                    request.Target, request.Locator, [], truncated: false, LiveVersion(pageVersion)));
            }

            var mapped = MapHandle(handle);
            return mapped.IsFailure
                ? CapabilityResult<DesktopLocateResult>.Failure(mapped.Error)
                : CapabilityResult<DesktopLocateResult>.Success(new DesktopLocateResult(
                    request.Target, request.Locator, [mapped.Value], truncated: false, LiveVersion(pageVersion)));
        }

        var handles = await browserPage.QueryAllAsync(runtimeLocator, cancellationToken).ConfigureAwait(false);
        var truncated = handles.Count > request.MaxResults;
        var elements = new List<DesktopElementRef>(Math.Min(handles.Count, request.MaxResults));

        foreach (var handle in handles.Take(request.MaxResults))
        {
            var mapped = MapHandle(handle);
            if (mapped.IsFailure)
            {
                return CapabilityResult<DesktopLocateResult>.Failure(mapped.Error);
            }

            elements.Add(mapped.Value);
        }

        return CapabilityResult<DesktopLocateResult>.Success(new DesktopLocateResult(
            request.Target, request.Locator, elements, truncated, LiveVersion(pageVersion)));
    }

    /// <summary>把元素句柄映射成按值引用；**版本取元素自身**（缺失即报错，不静默丢弃）。</summary>
    private static CapabilityResult<DesktopElementRef> MapHandle(IElementHandle handle)
    {
        if (handle.PageVersion <= 0)
        {
            // 元素没有版本 ⇒ 引用会失去依据：这是运行时缺陷，必须响亮而不是静默丢弃命中项。
            return CapabilityResult<DesktopElementRef>.Failure(DesktopCapabilityError.Internal(
                $"element '{handle.Info.Ref}' has no live page version"));
        }

        var info = handle.Info;
        return CapabilityResult<DesktopElementRef>.Success(new DesktopElementRef(
            info.Ref,
            info.Tag,
            new DesktopPageVersion(handle.PageVersion),
            role: info.Role,
            name: info.Name,
            text: info.Text,
            visible: info.Visible,
            enabled: info.Enabled,
            isChecked: info.Checked)
            {
                BoundingBox = info.BoundingBox is { } b ? new DesktopElementBox(b.X, b.Y, b.Width, b.Height) : null,
            });
    }

    private static DesktopPageVersion LiveVersion(long value) =>
        value > 0 ? new DesktopPageVersion(value) : DesktopPageVersion.Unknown;

    private static Locator ToRuntimeLocator(DesktopLocator locator) => new()
    {
        Kind = (LocatorKind)(int)locator.Kind,
        Value = locator.Value,
        Name = locator.Name,
        Exact = locator.Exact,
        Nth = locator.Nth,
        HasText = locator.HasText,
        // v1 不支持 Frame/Has：契约层不表达，故不设置（线缆上也拒绝）。
    };

    private async Task<CapabilityResult<IBrowserPage>> ResolvePageAsync(
        DesktopPageTarget target, CancellationToken cancellationToken)
    {
        var browserContext = await _runtime
            .GetContextAsync(new BrowserContextId(target.ContextId), cancellationToken).ConfigureAwait(false);
        if (browserContext is null)
        {
            return CapabilityResult<IBrowserPage>.Failure(
                DesktopCapabilityError.InvalidTarget($"context '{target.ContextId}' is not known"));
        }

        var page = await browserContext.GetPageAsync(new PageId(target.PageId), cancellationToken).ConfigureAwait(false);
        return page is null
            ? CapabilityResult<IBrowserPage>.Failure(
                DesktopCapabilityError.InvalidTarget($"page '{target.Key}' is not known"))
            : CapabilityResult<IBrowserPage>.Success(page);
    }
    /// <summary>
    /// 读取页面快照。三个要点：
    /// ①**预算**：把请求预算传给运行时，并在返回后再收敛一次（运行时漏了预算也不会无界回传）；
    /// ②**截断如实**：运行时报的 Truncated 与本地收敛产生的截断都体现在结果里，不假装完整；
    /// ③**版本**：给出期望版本时不符即拒绝，返回的快照版本是页面**当前**版本（Ref 的判定依据）。
    /// 请求未指定任何内容（DOM/可达性树/HTML 都为 false）时直接拒绝：不做无内容的空调用。
    /// </summary>
    public async Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
        DesktopCallContext context, BrowserSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Options.HasContent)
        {
            return CapabilityResult<DesktopSnapshot>.Failure(
                DesktopCapabilityError.InvalidRequest("snapshot request requires at least one content kind"));
        }

        if (_runtime.State != BrowserRuntimeState.Ready)
        {
            return CapabilityResult<DesktopSnapshot>.Failure(
                DesktopCapabilityError.UiUnavailable($"browser runtime is {_runtime.State}"));
        }

        var page = await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (page.IsFailure)
        {
            return CapabilityResult<DesktopSnapshot>.Failure(page.Error);
        }

        var browserPage = page.Value;
        if (request.ExpectedPageVersion.IsKnown && browserPage.PageVersion != request.ExpectedPageVersion.Value)
        {
            return CapabilityResult<DesktopSnapshot>.Failure(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"page '{request.Target.Key}' is at v{browserPage.PageVersion}, request pinned v{request.ExpectedPageVersion.Value}",
                retryable: true));
        }

        var snapshot = await browserPage.SnapshotAsync(new SnapshotOptions
        {
            IncludeDom = request.Options.IncludeDom,
            IncludeAccessibilityTree = request.Options.IncludeAccessibilityTree,
            IncludeHtml = request.Options.IncludeHtml,
            MaxNodes = request.Options.MaxNodes,
            MaxTextLength = request.Options.MaxTextLength,
            IncludeHidden = request.Options.IncludeHidden,
            IncludeIframes = request.Options.IncludeIframes,
            IncludeShadowDom = request.Options.IncludeShadowDom,
            MaxDepth = request.Options.MaxDepth,
        }, cancellationToken).ConfigureAwait(false);

        var mapped = new DesktopSnapshot(
            request.Target,
            snapshot.DomText,
            snapshot.AccessibilityTree,
            snapshot.Html,
            snapshot.Truncated,
            snapshot.NodeCount,
            LiveVersion(browserPage.PageVersion));

        // 纵深防御：运行时没守预算时也要在这里收敛（含硬上限）。
        return CapabilityResult<DesktopSnapshot>.Success(
            DesktopCapabilityBudgets.Apply(mapped, request.Options));
    }
    /// <summary>
    /// 等待条件满足。核心语义：**超时不是失败**——用 <see cref="DesktopWaitResult.TimedOut"/> 如实标注，
    /// 并照样回带等待结束时的页面状态（版本可能是等待期间推进后的版本），
    /// 让调用方自己决定重试/换条件/放弃；把它当异常会让上层做出错误的重试决策。
    /// 固定了期望版本时，版本不符即拒绝：等一个已经过去的版本的"就绪"没有意义。
    /// </summary>
    public async Task<CapabilityResult<DesktopWaitResult>> WaitForAsync(
        DesktopCallContext context, BrowserWaitForRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (_runtime.State != BrowserRuntimeState.Ready)
        {
            return CapabilityResult<DesktopWaitResult>.Failure(
                DesktopCapabilityError.UiUnavailable($"browser runtime is {_runtime.State}"));
        }

        var page = await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (page.IsFailure)
        {
            return CapabilityResult<DesktopWaitResult>.Failure(page.Error);
        }

        var browserPage = page.Value;
        if (request.ExpectedPageVersion.IsKnown && browserPage.PageVersion != request.ExpectedPageVersion.Value)
        {
            return CapabilityResult<DesktopWaitResult>.Failure(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"page '{request.Target.Key}' is at v{browserPage.PageVersion}, request pinned v{request.ExpectedPageVersion.Value}",
                retryable: true));
        }

        var condition = ToRuntimeWaitCondition(request);
        var waited = await browserPage.WaitForAsync(condition, cancellationToken).ConfigureAwait(false);

        // 等待可能推进了页面版本（导航/交互所致）⇒ 状态取等待**结束时**的事实。
        var state = new DesktopPageState(
            request.Target,
            ParseUrl(browserPage.Info.Url),
            LiveVersion(browserPage.PageVersion),
            browserPage.IsLoading ? DesktopPageReadiness.Loading : DesktopPageReadiness.Unknown, browserPage.Info.Title);

        return CapabilityResult<DesktopWaitResult>.Success(new DesktopWaitResult(
            request.Target,
            request.Condition,
            waited.TimedOut,
            state,
            // 诊断信息只作为附加说明传递；「超时」由 TimedOut 表达，不走这里。
            waited.Error));
    }

    private static WaitCondition ToRuntimeWaitCondition(BrowserWaitForRequest request) => new()
    {
        Selector = request.Condition.Kind == DesktopWaitConditionKind.Selector ? request.Condition.Value : null,
        SelectorToHide = request.Condition.Kind == DesktopWaitConditionKind.SelectorHidden ? request.Condition.Value : null,
        UrlPattern = request.Condition.Kind == DesktopWaitConditionKind.UrlPattern ? request.Condition.Value : null,
        TimeoutMs = request.TimeoutMs,
    };
    /// <summary>
    /// 执行交互（切片 D 唯一会改变页面状态的能力）。要点：
    /// ①**必须固定版本**且版本不符即拒绝——否则可能操作到另一个页面；
    /// ②动作按类型映射到运行时的显式 API（不自己拼 DOM 脚本）；
    /// ③**交互后回带新的页面状态与版本**（旧 Ref 随之作废）。
    ///   运行时以页面版本作为变更计数，因此交互必然推进版本；服务侧还有 `DesktopMutationInvariants` 兜底。
    /// ④`focus` 在当前运行时没有对应 API ⇒ 明确返回 `unsupported_capability`，**不**用脚本"凑"出来。
    /// </summary>
    public async Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
        DesktopCallContext context, BrowserInteractRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Action == DesktopInteractionAction.Focus)
        {
            return CapabilityResult<DesktopInteractionResult>.Failure(
                DesktopCapabilityError.UnsupportedCapability("the browser runtime has no focus action"));
        }

        if (_runtime.State != BrowserRuntimeState.Ready)
        {
            return CapabilityResult<DesktopInteractionResult>.Failure(
                DesktopCapabilityError.UiUnavailable($"browser runtime is {_runtime.State}"));
        }

        var page = await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (page.IsFailure)
        {
            return CapabilityResult<DesktopInteractionResult>.Failure(page.Error);
        }

        var browserPage = page.Value;
        if (browserPage.PageVersion != request.ExpectedPageVersion.Value)
        {
            return CapabilityResult<DesktopInteractionResult>.Failure(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"page '{request.Target.Key}' is at v{browserPage.PageVersion}, request pinned v{request.ExpectedPageVersion.Value}",
                retryable: true));
        }

        Locator? locator = null;
        IElementHandle? handle = null;

        if (request.RequiresLocator)
        {
            locator = ToRuntimeLocator(request.Locator!);
            handle = await browserPage.QueryAsync(locator, cancellationToken).ConfigureAwait(false);
            if (handle is null)
            {
                // 元素不存在是**目标问题**，不是内部错误：调用方应改定位或先等待。
                return CapabilityResult<DesktopInteractionResult>.Failure(
                    DesktopCapabilityError.InvalidTarget($"no element matches {request.Locator}"));
            }
        }

        await PerformAsync(browserPage, request, locator, cancellationToken).ConfigureAwait(false);

        // 交互后的页面状态：版本取**交互之后**的事实（旧 Ref 由此作废）。
        var state = new DesktopPageState(
            request.Target,
            ParseUrl(browserPage.Info.Url),
            LiveVersion(browserPage.PageVersion),
            browserPage.IsLoading ? DesktopPageReadiness.Loading : DesktopPageReadiness.Unknown, browserPage.Info.Title);

        DesktopElementRef? element = null;
        if (handle is not null)
        {
            var mapped = MapHandle(handle);
            if (mapped.IsFailure)
            {
                return CapabilityResult<DesktopInteractionResult>.Failure(mapped.Error);
            }

            element = mapped.Value;
        }

        return CapabilityResult<DesktopInteractionResult>.Success(
            new DesktopInteractionResult(request.Target, state, element));
    }

    private static async Task PerformAsync(
        IBrowserPage page, BrowserInteractRequest request, Locator? locator, CancellationToken cancellationToken)
    {
        switch (request.Action)
        {
            case DesktopInteractionAction.Click:
                await page.ClickAsync(locator!, new ClickOptions(), cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Fill:
                await page.FillAsync(locator!, request.Text!, new FillOptions(), cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Type:
                await page.TypeAsync(locator!, request.Text!, new TypeOptions(), cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Press:
                await page.PressAsync(locator!, request.Text!, new KeyOptions(), cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Check:
                await page.CheckAsync(locator!, true, cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Uncheck:
                await page.CheckAsync(locator!, false, cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Select:
                await page.SelectAsync(locator!, request.Values!, cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Hover:
                await page.HoverAsync(locator!, new PointerOptions(), cancellationToken).ConfigureAwait(false);
                break;
            case DesktopInteractionAction.Scroll:
                await page.ScrollAsync(
                    new ScrollOptions { DeltaX = request.DeltaX, DeltaY = request.DeltaY }, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Action, "Interaction action is not mapped.");
        }
    }
    /// <summary>
    /// 导航（变更类）：固定版本不符即拒绝；运行时报 `Ok=false` 时映射为**可判定的目标错误**
    /// （而不是一律 internal_error —— 上层要能区分"这个地址去不了"与"运行时坏了"）。
    /// 返回的 <see cref="NavigateResult.Disposition"/> 只表示导航本身完成，**不代表 DOM 可交互**。
    /// </summary>
    public async Task<CapabilityResult<NavigateResult>> NavigateAsync(
        DesktopCallContext context, NavigateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (_runtime.State != BrowserRuntimeState.Ready)
        {
            return CapabilityResult<NavigateResult>.Failure(
                DesktopCapabilityError.UiUnavailable($"browser runtime is {_runtime.State}"));
        }

        var page = await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (page.IsFailure)
        {
            return CapabilityResult<NavigateResult>.Failure(page.Error);
        }

        var browserPage = page.Value;
        if (request.ExpectedPageVersion.IsKnown && browserPage.PageVersion != request.ExpectedPageVersion.Value)
        {
            return CapabilityResult<NavigateResult>.Failure(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"page '{request.Target.Key}' is at v{browserPage.PageVersion}, request pinned v{request.ExpectedPageVersion.Value}",
                retryable: true));
        }

        if (request.Action == DesktopNavigationAction.Goto)
        {
            var navigation = await browserPage
                .GotoAsync(
                    request.Url!,
                    new NavigationOptions { TimeoutMs = request.TimeoutMs },
                    cancellationToken)
                .ConfigureAwait(false);

            // "导航没成功"是**结果事实**而不是能力失败：上层要能区分"这个地址去不了"与"运行时坏了"。
            // 这也与迁移前的工具行为一致（它把 ok/status 作为结果字段返回）。
            return CapabilityResult<NavigateResult>.Success(new NavigateResult(
                NavigateDisposition.Completed,
                navigation.Url,
                LiveVersion(browserPage.PageVersion),
                navigation.Ok,
                navigation.StatusCode,
                navigation.ErrorText,
                browserPage.Info.Title));
        }

        switch (request.Action)
        {
            case DesktopNavigationAction.Back:
                await browserPage.GoBackAsync(cancellationToken).ConfigureAwait(false);
                break;
            case DesktopNavigationAction.Forward:
                await browserPage.GoForwardAsync(cancellationToken).ConfigureAwait(false);
                break;
            case DesktopNavigationAction.Reload:
                await browserPage.ReloadAsync(cancellationToken).ConfigureAwait(false);
                break;
            case DesktopNavigationAction.Stop:
                await browserPage.StopAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                return CapabilityResult<NavigateResult>.Failure(DesktopCapabilityError.InvalidRequest(
                    $"navigation action '{DesktopNavigationActionWire.NameOf(request.Action)}' has no implementation"));
        }

        // 这四个动作在运行时没有等价返回值 ⇒ ok/status/error **未知**，如实留空（不猜）。
        return CapabilityResult<NavigateResult>.Success(new NavigateResult(
            NavigateDisposition.Completed,
            ParseUrl(browserPage.Info.Url),
            LiveVersion(browserPage.PageVersion),
            Title: browserPage.Info.Title));
    }

    /// <summary>
    /// 执行脚本并回带**裸 JSON 片段**的返回值（避免二次编码）。
    /// 脚本正文不进日志（契约已声明）；结果超出预算时**置 Truncated 并返回 null 值**，
    /// 而不是把 JSON 截成半截（半截 JSON 会让调用方解析失败且无法察觉原因）。
    /// </summary>
    public async Task<CapabilityResult<JavascriptResult>> ExecuteJavascriptAsync(
        DesktopCallContext context, JavascriptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (_runtime.State != BrowserRuntimeState.Ready)
        {
            return CapabilityResult<JavascriptResult>.Failure(
                DesktopCapabilityError.UiUnavailable($"browser runtime is {_runtime.State}"));
        }

        var page = await ResolvePageAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (page.IsFailure)
        {
            return CapabilityResult<JavascriptResult>.Failure(page.Error);
        }

        var browserPage = page.Value;
        if (request.ExpectedPageVersion.IsKnown && browserPage.PageVersion != request.ExpectedPageVersion.Value)
        {
            return CapabilityResult<JavascriptResult>.Failure(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"page '{request.Target.Key}' is at v{browserPage.PageVersion}, request pinned v{request.ExpectedPageVersion.Value}",
                retryable: true));
        }

        var value = await browserPage.EvaluateAsync(
            new BrowserScript { Source = request.Script }, cancellationToken).ConfigureAwait(false);

        var (kind, json) = MapScriptValue(value);

        if (json is not null && System.Text.Encoding.UTF8.GetByteCount(json) > request.MaxResultBytes)
        {
            // 截断 JSON 会产出无法解析的半截文本：宁可只标注截断、不给值。
            return CapabilityResult<JavascriptResult>.Success(
                new JavascriptResult(kind, JsonValue: null, Truncated: true));
        }

        return CapabilityResult<JavascriptResult>.Success(new JavascriptResult(kind, json, Truncated: false));
    }

    private static (JavascriptValueKind Kind, string? Json) MapScriptValue(BrowserScriptValue value)
    {
        if (value.Value is not { } element || element.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return string.Equals(value.Type, "undefined", StringComparison.OrdinalIgnoreCase)
                ? (JavascriptValueKind.Undefined, null)
                : (JavascriptValueKind.Null, null);
        }

        var kind = element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => JavascriptValueKind.Boolean,
            System.Text.Json.JsonValueKind.Number => JavascriptValueKind.Number,
            System.Text.Json.JsonValueKind.String => JavascriptValueKind.String,
            _ => JavascriptValueKind.Json,
        };

        return (kind, element.GetRawText());
    }
    private static Uri? ParseUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? null : parsed;
}