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
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserWaitForArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (args.TimeoutMs is < 1 or > 120_000
            || (string.IsNullOrWhiteSpace(args.Selector)
                && string.IsNullOrWhiteSpace(args.SelectorToHide)
                && string.IsNullOrWhiteSpace(args.UrlPattern)))
            return BrowserToolResponse.Failure("browser_invalid_arguments", "wait condition or timeout is invalid");
        try
        {
            if (callContexts.TryCreate() is not { } call)
            {
                return BrowserToolResponse.Failure(
                    "browser_not_connected", "No Desktop is connected for browser capabilities");
            }

            // 调用方给了上下文就直接用，不要先去列清单（宿主集成测试按调用序列断言会抓到）。
            var contextId = string.IsNullOrWhiteSpace(args.ContextId) ? null : args.ContextId!.Trim();
            if (contextId is null)
            {
                var contexts = await surface.GetContextsAsync(call, ct);
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

    /// <summary>三个可选条件 → 契约的单一条件；优先级与运行时字段顺序一致（选择器 → 消失 → URL）。</summary>
    private static DesktopWaitCondition BuildCondition(BrowserWaitForArgs args) =>
        !string.IsNullOrWhiteSpace(args.Selector)
            ? new DesktopWaitCondition(DesktopWaitConditionKind.Selector, args.Selector!)
            : !string.IsNullOrWhiteSpace(args.SelectorToHide)
                ? new DesktopWaitCondition(DesktopWaitConditionKind.SelectorHidden, args.SelectorToHide!)
                : new DesktopWaitCondition(DesktopWaitConditionKind.UrlPattern, args.UrlPattern!);

    private static ToolExecutionResult Failure(DesktopCapabilityError error) =>
        BrowserCapabilityFailure.From(error, "browser_wait_for_failed", "stale_page_version");
}
