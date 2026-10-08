using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserLocateArgs
{
    /// <summary>定位默认返回条数上限（设计方案 §6.2：locate 默认 limit=10）。</summary>
    public const int DefaultLimit = 10;

    [ToolParam("Browser page id to search.")]
    public required string PageId { get; init; }
    public string? ContextId { get; init; }
    public required BrowserLocatorInput Locator { get; init; }

    /// <summary>返回条数上限（默认 10，最多 100）。低于设计默认值是有意的：定位结果通常只需前几条。</summary>
    [ToolParam("Maximum matches to return (default 10, max 100).")]
    public int? Limit { get; init; }

    /// <summary>
    /// 是否返回表单当前值（默认 <c>false</c>，见设计方案 §6.2）。
    /// 需要确认「fill 到底写进去了没有」时显式打开——这是唯一的可靠回执，
    /// 因为 <c>text</c> 不含表单值、URL 也推不出填了什么。
    /// </summary>
    [ToolParam("Include the current form value/selected values for each match (default false; use it to confirm a fill actually wrote).")]
    public bool IncludeValue { get; init; }

    /// <summary>是否返回勾选态（默认 <c>false</c>；同样属「可显式请求」的状态，见 §6.2）。</summary>
    [ToolParam("Include the checked state for each match (default false).")]
    public bool IncludeChecked { get; init; }
}

[Tool(
    id: BrowserAgentToolIds.Locate,
    name: "Browser locate",
    description: "Resolve a ref, CSS, XPath, text, role, label, placeholder, alt text, title, or test-id locator in a Desktop browser tab.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class BrowserLocateTool(
    IDesktopBrowserCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts,
    IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserLocateArgs>(originAccessor)
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserLocateArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        try
        {
            var locator = BrowserLocatorInputMapper.ToDesktopLocator(args.Locator);

            // 越界不是运行时异常，而是调用方参数错（fail closed，且错误码可读）。
            if (args.Limit is { } limit and (< 1 or > BrowserLocateRequest.MaxMaxResults))
            {
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments",
                    $"locator limit must be between 1 and {BrowserLocateRequest.MaxMaxResults}");
            }

            if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } call)
            {
                return BrowserToolResponse.Failure(
                    "browser_not_connected", "No Desktop is connected for browser capabilities");
            }

            // 调用方给了上下文就直接用，**不要**先去列清单：那是多余且更重的一跳
            // （宿主集成测试按调用序列断言，正好抓出了这一点）。
            var contextId = string.IsNullOrWhiteSpace(args.ContextId) ? null : args.ContextId!.Trim();
            if (contextId is null)
            {
                // 只有省略上下文时，才沿用「用第一个可用上下文」的既有语义。
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

            // Ref 定位必须带上引用来源的版本（引用随版本失效）。工具入参里没有版本，
            // 因此读一次当前状态作为基准——读到的是当前版本，不会自相矛盾，但引用因此有了可比较的版本。
            var expectedPageVersion = DesktopPageVersion.Unknown;
            if (locator.Kind == DesktopLocatorKind.Ref)
            {
                var state = await surface.GetPageStateAsync(target, call, ct);
                if (state.IsFailure)
                {
                    return Failure(state.Error!);
                }

                expectedPageVersion = state.Value.Version;
            }

            var located = await surface.LocateAsync(
                new BrowserLocateRequest(target, locator, expectedPageVersion, args.Limit ?? BrowserLocateArgs.DefaultLimit), call, ct);
            if (located.IsFailure)
            {
                return Failure(located.Error!);
            }

            var elements = located.Value.Elements;
            var values = elements
                .Take(BrowserLocateRequest.MaxMaxResults)
                .Select(element => ToValue(element, args.IncludeValue, args.IncludeChecked))
                .ToArray();
            return BrowserToolResponse.Success(
                new BrowserLocateToolValue
                {
                    Count = elements.Count,
                    Elements = values
                },
                new PuddingBrowser.Abstractions.BrowserContextId(contextId),
                new PuddingBrowser.Abstractions.PageId(args.PageId.Trim()),
                located.Value.PageVersion.IsKnown ? located.Value.PageVersion.Value : null,
                located.Value.Truncated || elements.Count > values.Length
                    ? ["locator_results_truncated"]
                    : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserOperationException ex) { return BrowserToolResponse.FromException(ex); }
    }

    /// <summary>定位的版本不符语义是"引用过期"（沿用既有错误码）；映射表集中在共享辅助里。</summary>
    private static ToolExecutionResult Failure(DesktopCapabilityError error) =>
        BrowserCapabilityFailure.From(error, "browser_locate_failed", "stale_element_reference");

    /// <summary>
    /// 定位结果的展示投影（规则集中在 <see cref="BrowserToolResponse.Element(DesktopElementRef, bool, bool)"/>）：
    /// text 与 name 相同则省略、checked 显式请求、bbox 最多一位小数。
    /// 敏感值不得在此直接回显——交互回执走 <c>BrowserActionAssertion</c> 的脱敏路径。
    /// </summary>
    private static BrowserElementToolValue ToValue(
        DesktopElementRef element, bool includeValue, bool includeChecked) =>
        BrowserToolResponse.Element(element, includeValue, includeChecked);
}
