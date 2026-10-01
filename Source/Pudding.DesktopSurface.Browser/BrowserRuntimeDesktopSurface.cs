using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;

namespace Pudding.DesktopSurface.Browser;

/// <summary>
/// 浏览器侧表面：把 <see cref="IDesktopUiSurface"/> 的浏览器操作**映射到既有浏览器抽象**
/// （<see cref="IBrowserRuntime"/>），而不是重建 WebView2 逻辑——现有七个浏览器工具正基于该抽象工作。
///
/// 本轮只实现 <see cref="GetContextsAsync"/>（清单是"先看清有什么"的入口，且不需要任何 DOM 交互），
/// 其余操作按 [映射规格](../../Docs/Features/Desktop-Surface-Browser-Mapping-2026-10-01.md) 逐步补齐。
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

            mapped.Add(new DesktopContextInfo(info.Id.Value, _targets.TrustFor(info.Id.Value), mappedPages));
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
            page.IsLoading ? DesktopPageReadiness.Loading : DesktopPageReadiness.Unknown));
    }
    private static Uri? ParseUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? null : parsed;
}