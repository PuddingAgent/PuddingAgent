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
    /// 上一次定位返回的分页游标（<c>nextCursor</c>）：原样回传即可取下一页。
    /// **对模型不透明**——不要拼装或修改；页面变化或换了定位条件后旧游标会过期
    /// （工具会明确返回 <c>cursor_expired</c>，此时重新定位即可）。
    /// </summary>
    [ToolParam("Opaque pagination cursor from a previous locate's nextCursor. Pass it back unchanged to get the next page; it expires when the page or the locator changes.")]
    public string? Cursor { get; init; }

    /// <summary>
    /// 只返回这些字段（设计方案 §6.2 的 <c>fields</c>）：键名与输出里的 JSON 键一致。
    /// 给了就是**权威白名单**（列了 <c>value</c> 就等于要值）；核心字段 ref/tag/visible/enabled 恒在。
    /// 未知字段名明确拒绝，不静默忽略。
    /// </summary>
    [ToolParam("Only return these element fields (JSON key names, e.g. ref, name, text, boundingBox). Core fields ref/tag/visible/enabled are always returned. Unknown names are rejected.")]
    public IReadOnlyList<string>? Fields { get; init; }

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
    protected override async Task<ToolExecutionResult> ExecuteBrowserCoreAsync(
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

            // 字段白名单：未知名字必须**明确拒绝**（静默忽略会让调用方以为字段真的没了/真的在）。
            if (!BrowserElementFields.TryParse(args.Fields, out var fields, out var unknownField))
            {
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments",
                    $"unknown element field '{unknownField}'; valid fields: {BrowserElementFields.SelectableList}"
                    + $" (always returned: {string.Join(", ", BrowserElementFields.Core)})");
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
                new BrowserLocateRequest(
                    target,
                    locator,
                    expectedPageVersion,
                    args.Limit ?? BrowserLocateArgs.DefaultLimit,
                    args.Cursor),
                call, ct);
            if (located.IsFailure)
            {
                return Failure(located.Error!);
            }

            var elements = located.Value.Elements;
            var values = elements
                .Take(BrowserLocateRequest.MaxMaxResults)
                .Select(element => ToValue(element, args.IncludeValue, args.IncludeChecked, fields))
                .ToArray();
            return BrowserToolResponse.Success(
                new BrowserLocateToolValue
                {
                    Count = elements.Count,
                    Elements = values,
                    // 分页游标：原样回传宿主的令牌（模型不解释它），取完即 null。
                    NextCursor = located.Value.NextCursor
                },
                new PuddingBrowser.Abstractions.BrowserContextId(contextId),
                new PuddingBrowser.Abstractions.PageId(args.PageId.Trim()),
                located.Value.PageVersion.IsKnown ? located.Value.PageVersion.Value : null,
                located.Value.Truncated || elements.Count > values.Length
                    ? ["locator_results_truncated"]
                    : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserOperationException ex)
        {
            // 定位是只读的，没有"是否已派发"的语义（因此不带执行回执）；
            // 但**最小修复提示**必须带出来：零命中 与 "命中了但全被隐藏/视口筛掉" 的下一步完全不同。
            var (_, hint) = BrowserOperationFailure.Map(ex.Code, ex.Message);
            return BrowserToolResponse.Failure(
                ex.Code,
                ex.Message,
                warnings: hint is null ? null : [hint]);
        }
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
        DesktopElementRef element, bool includeValue, bool includeChecked, OutputFieldSet fields) =>
        BrowserToolResponse.Element(element, includeValue, includeChecked, fields);
}
