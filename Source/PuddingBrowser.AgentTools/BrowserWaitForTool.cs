using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserWaitForArgs
{
    [ToolParam("Browser page id to wait in.")]
    public required string PageId { get; init; }
    public string? ContextId { get; init; }
    public string? Selector { get; init; }
    public string? SelectorToHide { get; init; }
    public string? UrlPattern { get; init; }

    /// <summary>
    /// 等**内容增长**：等到页面元素数**超过**这个基线（门禁 G8 的无限滚动——等新增内容/条目数，
    /// 而不是把"没加载出新内容"当成滚动失败）。基线的取法：滚动/触发加载后先快照一次，用当时的
    /// `nodeCount` 作为基线再等。
    /// </summary>
    [ToolParam("Baseline node count; wait until the page has MORE elements than this (infinite scroll).")]
    public int? ContentGrowsAboveNodes { get; init; }

    public int TimeoutMs { get; init; } = 30_000;
}

[Tool(
    id: BrowserAgentToolIds.WaitFor,
    name: "Browser wait for",
    description: "Wait for a CSS selector to appear or hide, or for a wildcard URL pattern in a Desktop browser tab.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class BrowserWaitForTool(
    IDesktopBrowserCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts,
    IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserWaitForArgs>(originAccessor)
{
    protected override async Task<ToolExecutionResult> ExecuteBrowserCoreAsync(
        BrowserWaitForArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (args.TimeoutMs is < 1 or > 120_000
            || (string.IsNullOrWhiteSpace(args.Selector)
                && string.IsNullOrWhiteSpace(args.SelectorToHide)
                && string.IsNullOrWhiteSpace(args.UrlPattern)
                && args.ContentGrowsAboveNodes is null)
            || args.ContentGrowsAboveNodes is < 0)
            return BrowserToolResponse.Failure("browser_invalid_arguments", "wait condition or timeout is invalid");
        try
        {
            if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } call)
            {
                return BrowserToolResponse.Failure(
                    "browser_not_connected", "No Desktop is connected for browser capabilities");
            }

            // 调用方给了上下文就直接用，不要先去列清单（宿主集成测试按调用序列断言会抓到）。
            var contextId = string.IsNullOrWhiteSpace(args.ContextId) ? null : args.ContextId!.Trim();
            if (contextId is null)
            {
                // 列出上下文是独立的一条能力命令：必须单独签发 operation id，
                // 否则后续等待命令会被 broker 判「同 id 不同 payload」而拒绝。
                if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } listCall)
                {
                    return BrowserToolResponse.Failure(
                        "browser_not_connected", "No Desktop is connected for browser capabilities");
                }

                var contexts = await surface.GetContextsAsync(listCall, ct);
                if (contexts.IsFailure)
                {
                    return Failure(contexts.Error!);
                }

                contextId = contexts.Value.Contexts.Count > 0 ? contexts.Value.Contexts[0].ContextId : null;
                if (string.IsNullOrWhiteSpace(contextId))
                {
                    return BrowserToolResponse.Failure("browser_context_not_found", "No browser context is available");
                }
            }

            var target = new DesktopPageTarget(contextId, args.PageId.Trim());
            var waited = await surface.WaitForAsync(
                new BrowserWaitForRequest(target, BuildCondition(args), args.TimeoutMs), call, ct);
            if (waited.IsFailure)
            {
                return Failure(waited.Error!);
            }

            var state = waited.Value.Page;
            return BrowserToolResponse.Success(
                new BrowserWaitToolValue
                {
                    // 超时由 TimedOut 表达，error 只是运行时诊断（与 Desktop 侧同一语义）。
                    TimedOut = waited.Value.TimedOut,
                    Error = waited.Value.Error,
                    Page = new BrowserTabToolValue
                    {
                        ContextId = contextId,
                        PageId = args.PageId.Trim(),
                        // 标题未知时回退空串：结果里的 Title 是必填，但"不知道"不该编一个标题。
                        Title = state.Title ?? string.Empty,
                        Url = state.Url?.AbsoluteUri ?? string.Empty,
                        PageVersion = state.Version.IsKnown ? state.Version.Value : 0,
                    },
                },
                new BrowserContextId(contextId),
                new PageId(args.PageId.Trim()),
                state.Version.IsKnown ? state.Version.Value : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserOperationException ex) { return BrowserToolResponse.FromException(ex); }
    }

    /// <summary>
    /// 四个可选条件 → 契约的单一条件；优先级：选择器 → 消失 → URL → **内容增长**。
    /// （内容增长放最后是因为它需要基线，通常与"滚一下再看"搭配使用。）
    /// </summary>
    private static DesktopWaitCondition BuildCondition(BrowserWaitForArgs args) =>
        !string.IsNullOrWhiteSpace(args.Selector)
            ? new DesktopWaitCondition(DesktopWaitConditionKind.Selector, args.Selector!)
            : !string.IsNullOrWhiteSpace(args.SelectorToHide)
                ? new DesktopWaitCondition(DesktopWaitConditionKind.SelectorHidden, args.SelectorToHide!)
                : !string.IsNullOrWhiteSpace(args.UrlPattern)
                    ? new DesktopWaitCondition(DesktopWaitConditionKind.UrlPattern, args.UrlPattern!)
                    : new DesktopWaitCondition(
                        DesktopWaitConditionKind.ContentGrows,
                        args.ContentGrowsAboveNodes!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static ToolExecutionResult Failure(DesktopCapabilityError error) =>
        BrowserCapabilityFailure.From(error, "browser_wait_for_failed", "stale_page_version");
}
