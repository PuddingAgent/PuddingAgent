using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Automation;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserSnapshotArgs
{
    [ToolParam("Browser page id to inspect.")]
    public required string PageId { get; init; }
    [ToolParam("Optional browser context id.")]
    public string? ContextId { get; init; }

    /// <summary>
    /// 阅读模式（设计方案 §6.1）：<c>outline</c>（默认，可交互节点 + 标题/地标）、
    /// <c>interactive</c>（只列可交互节点）、<c>full</c>（完整 DOM + 无障碍树，排障用，需显式开启）。
    /// <c>text</c>（正文 markdown）尚未实现 —— 传它会得到明确的 unsupported，**不**静默降级成 full。
    /// </summary>
    [ToolParam("Reading mode: outline (default: interactive nodes + headings/landmarks), interactive (only interactive nodes), or full (complete DOM + accessibility tree, for troubleshooting). text is not implemented yet.")]
    public string? Mode { get; init; }

    /// <summary>
    /// 表示格式（设计方案 §151：<c>mode</c> 决定读什么，<c>format</c> 决定**表示成什么**）。
    /// 目前仅正文模式支持：<c>markdown</c>（默认）或 <c>compact_json</c>（给程序读的紧凑 JSON）。
    /// 未登记的线名 fail closed。
    /// </summary>
    [ToolParam("Representation format for mode=text: markdown (default) or compact_json.")]
    public string? Format { get; init; }

    /// <summary>输出行数上限（仅 outline/interactive 生效，默认 50）。</summary>
    [ToolParam("Maximum outline lines to return (default 50; outline/interactive only).")]
    public int? Limit { get; init; }

    /// <summary>输出字节上限（仅 outline/interactive 生效，默认 4096）。</summary>
    [ToolParam("Maximum UTF-8 bytes of outline text to return (default 4096; outline/interactive only).")]
    public int? MaxBytes { get; init; }

    /// <summary>
    /// 游标：把上一次结果里的 <c>nextCursor</c> **原样回传**即可续读。
    /// 游标里带着"由哪种表示产生"的标识，因此**跨表示误用会被明确拒绝**（而不是静默跳段）。
    /// </summary>
    [ToolParam("Opaque pagination cursor from a previous snapshot's nextCursor. Pass it back unchanged; cursors are bound to the representation that produced them.")]
    public string? Cursor { get; init; }

    /// <summary>
    /// 只返回这些输出字段（设计方案 §151 的 <c>fields</c>）：键名与输出里的 JSON 键一致。
    /// 核心字段 mode/truncated/truncationReason/nextCursor 恒在。
    /// 指定表示时**只向驱动请求那一种**（三种表示同次返回是 payload 大头）。
    /// </summary>
    [ToolParam("Only return these output fields (JSON key names: accessibilityTree, domText, html, nodeCount, returnedLines, bytes). Core fields mode/truncated/truncationReason/nextCursor are always returned. Only the requested representations are fetched.")]
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>
    /// 读取作用域（设计方案 §151 的 <c>scope</c>）：只读这个元素（含子孙）而不是整页。
    /// 用法与定位器一致：<c>{ kind: "ref", value: "p1-v2-n3" }</c> 直接用已有引用；
    /// 其它定位方式会**先解析成唯一 ref**（0 命中/多命中都明确拒绝，不猜"大概就是这个"）。
    /// </summary>
    [ToolParam("Read only this element's subtree instead of the whole page: use a ref locator to reuse an existing reference, or any other locator (resolved to a single ref first).")]
    public BrowserLocatorInput? Scope { get; init; }

    /// <summary>
    /// 与上次读取的修订标识比较（设计方案 §145）：只有相同页面版本 + 相同查询才谈得上"变化"；
    /// 不可比时返回 <c>refresh_required</c> 让调用方重新读取，**绝不伪造 diff**。
    /// </summary>
    [ToolParam("Revision from a previous result to check whether the content changed since then. Only comparable for the same page version and the same mode/scope/fields; otherwise the call fails with refresh_required.")]
    public string? SinceRevision { get; init; }

    // ── full 模式专用（outline/interactive 下被忽略，输出预算由 limit/max_bytes 决定）──
    /// <summary>显式要 DOM 文本（默认 false：§151「同次默认只返回一种表示」）。</summary>
    public bool IncludeDom { get; init; }

    /// <summary>显式要无障碍树（默认 false；**什么都不选时**默认给这一种）。</summary>
    public bool IncludeAccessibilityTree { get; init; }
    public bool IncludeHidden { get; init; }
    public bool IncludeIframes { get; init; } = true;
    public bool IncludeShadowDom { get; init; } = true;
    public bool IncludeHtml { get; init; }
    public int MaxNodes { get; init; } = 10_000;    public int MaxTextLength { get; init; } = 200_000;
    public int MaxDepth { get; init; } = 64;
}

[Tool(
    id: BrowserAgentToolIds.Snapshot,
    name: "Browser snapshot",
    description: "Read a bounded DOM and accessibility snapshot from a visible Desktop browser tab. Interactive nodes include reusable versioned refs.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class BrowserSnapshotTool(
    IDesktopBrowserCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts,
    IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserSnapshotArgs>(originAccessor)
{
    /// <summary>
    /// **full 模式的输出预算**（设计 §6.1 模式表："默认 16 KiB、硬上限 64 KiB，分页/截断声明"）。
    ///
    /// 与 <c>incomplete_scan</c> 严格分开：扫描侧缺内容报 <c>incomplete_scan</c>，
    /// 只是输出收敛报 <c>output_budget</c>；两者同时发生时报 <c>incomplete_scan+output_budget</c>。
    /// </summary>
    public const int FullModeDefaultMaxBytes = 16 * 1024;

    /// <summary>full 模式的**硬上限**：请求更大一律按参数错误拒绝（不静默夹到上限）。</summary>
    public const int FullModeMaxBytes = 64 * 1024;

    /// <summary>
    /// 把 full 模式的载荷收敛到 <paramref name="budget"/> 字节以内（**按序列化后的 UTF-8 字节**计量）。
    ///
    /// 做法是**缩减内容字段**（每次把最长的一路砍半，直到满足预算或三路都空），
    /// 而不是砍 JSON 尾巴 —— §6.1 明确禁止后者（那样结果不可解析）。
    /// 返回是否真的发生了输出收敛（调用方据此把原因写成 <c>output_budget</c>）。
    /// </summary>
    private static (BrowserSnapshotToolValue Value, bool OutputCut) ApplyFullModeBudget(
        BrowserSnapshotToolValue value, int budget)
    {
        var outputCut = false;
        var current = value;

        while (MeasureJsonBytes(current) > budget)
        {
            var longest = Longest(current);
            switch (longest)
            {
                case "domText" when !string.IsNullOrEmpty(current.DomText):
                    current = current with { DomText = Shrink(current.DomText) };
                    break;
                case "accessibilityTree" when !string.IsNullOrEmpty(current.AccessibilityTree):
                    current = current with { AccessibilityTree = Shrink(current.AccessibilityTree) };
                    break;
                case "html" when !string.IsNullOrEmpty(current.Html):
                    current = current with { Html = Shrink(current.Html) };
                    break;
                default:
                    // 三路都空还超预算 ⇒ 元数据本身超出（极小预算）：停止，由调用方如实标注截断。
                    return (current, true);
            }

            outputCut = true;
        }

        return (current, outputCut);

        static string? Longest(BrowserSnapshotToolValue candidate)
        {
            var lengths = new (string Name, int Length)[]
            {
                ("domText", candidate.DomText?.Length ?? 0),
                ("accessibilityTree", candidate.AccessibilityTree?.Length ?? 0),
                ("html", candidate.Html?.Length ?? 0),
            };

            var maximum = lengths.Max(entry => entry.Length);
            return maximum == 0 ? null : lengths.First(entry => entry.Length == maximum).Name;
        }

        static string Shrink(string text) => text.Length <= 256 ? string.Empty : text[..(text.Length / 2)];
    }

    /// <summary>用与工具结果一致的默认序列化选项计量 JSON 的 UTF-8 字节数。</summary>
    private static int MeasureJsonBytes(BrowserSnapshotToolValue value) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value).Length;
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserSnapshotArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (!TryParseMode(args.Mode, out var mode))
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments",
                "snapshot mode must be one of outline, interactive, text, or full");
        }

        if (mode == BrowserReadingMode.Full
            && (args.MaxNodes is < 1 or > 10_000 || args.MaxTextLength is < 256 or > 500_000
                || args.MaxDepth is < 1 or > 64))
        {
            return BrowserToolResponse.Failure("browser_invalid_arguments", "snapshot budgets are outside allowed ranges");
        }

        // full 模式的**输出**预算（§6.1）：默认 16 KiB，硬上限 64 KiB；超上限明确拒绝而不是静默夹住。
        if (mode == BrowserReadingMode.Full && args.MaxBytes is { } fullBudget
            && (fullBudget < 1024 || fullBudget > FullModeMaxBytes))
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments",
                $"full mode max_bytes must be in [1024, {FullModeMaxBytes}] (default {FullModeDefaultMaxBytes})");
        }

        if (!TryBuildReadingRequest(args, mode, out var reading, out var readingError))
        {
            return BrowserToolResponse.Failure("browser_invalid_arguments", readingError!);
        }

        // format（§151）：未登记的线名 fail closed；目前只有正文模式有第二种表示。
        if (!string.IsNullOrWhiteSpace(args.Format))
        {
            var format = args.Format.Trim();
            var known = format.Equals("markdown", StringComparison.OrdinalIgnoreCase)
                || format.Equals("compact_json", StringComparison.OrdinalIgnoreCase);
            if (!known)
            {
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments", "format must be one of markdown or compact_json");
            }

            if (mode != BrowserReadingMode.Text)
            {
                // 别假装支持：outline/interactive 只有一种文本表示，full 回的是原始表示而不是格式。
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments",
                    "format applies to mode=text only (outline/interactive have a single text representation)");
            }
        }

        // 输出字段白名单：未知名字明确拒绝（静默忽略会让调用方以为字段真的没了）。
        if (!BrowserSnapshotFields.TryParse(args.Fields, out var fields, out var unknownField))
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments",
                $"unknown snapshot field '{unknownField}'; valid fields: {BrowserSnapshotFields.SelectableList}"
                + $" (always returned: {string.Join(", ", BrowserSnapshotFields.Core)})");
        }

        // 正文模式的 `fields`（§151）：正文没有"表示类"字段（DOM/无障碍树/HTML 都不在输出里），
        // 因此那些名字**明确拒绝**而不是静默忽略（静默忽略会让调用方以为字段被应用了）。
        if (mode == BrowserReadingMode.Text)
        {
            foreach (var name in new[] { "accessibilityTree", "domText", "html" })
            {
                if (fields.IsExplicit && fields.Includes(name))
                {
                    return BrowserToolResponse.Failure(
                        "browser_invalid_arguments",
                        $"field '{name}' does not apply to mode=text;"
                        + " text returns content/bytes/returnedLines/nextCursor (plus revision facts)");
                }
            }
        }

        // 「模式与互斥字段组合非法时返回参数错误」（§151）：full 模式一个表示都没选就没法读。
        if (mode == BrowserReadingMode.Full && SelectedRepresentations(args, fields).Count == 0)
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments",
                "full mode must select at least one representation (accessibilityTree, domText or html)");
        }

        // 游标（§6.1）：不透明、**绑定产生它的表示**。跨表示误用必须明确拒绝 ——
        // 两种正文表示的页边界不同，拿错游标会**静默跳过**若干块（最难发现的一类数据丢失）。
        var cursor = 0;
        string? cursorPageIdentity = null;
        if (!string.IsNullOrWhiteSpace(args.Cursor))
        {
            if (!ArticleCursor.TryParse(args.Cursor, out var cursorKind, out var cursorIndex, out var cursorIdentity))
            {
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments",
                    "cursor is not a valid snapshot cursor; pass back the nextCursor from a previous snapshot unchanged");
            }

            var expectedKind = mode == BrowserReadingMode.Text
                ? (string.Equals(args.Format?.Trim(), "compact_json", StringComparison.OrdinalIgnoreCase)
                    ? ArticleCursor.CompactJsonKind
                    : ArticleCursor.MarkdownKind)
                : ArticleCursor.LinesKind;

            if (cursorKind != expectedKind)
            {
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments",
                    $"cursor belongs to a different representation ('{cursorKind}');"
                    + $" re-read with format={ArticleCursor.FormatOfKind(cursorKind)}"
                    + " (or omit the cursor to start from the beginning)");
            }

            // 页面/版本身份在**拿到快照之后**核对（那时才知道当前版本）——见下面的检查。
            cursorPageIdentity = cursorIdentity;
            cursor = cursorIndex;
        }

        try
        {
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

            // 作用域（§151）：ref 直接用；其它定位方式先解析成**唯一** ref——
            // 0 命中/多命中都明确拒绝（"大概就是这个"是错位读取的常见来源）。
            string? scopeRef = null;
            if (args.Scope is { } scope)
            {
                var scopeLocator = BrowserLocatorInputMapper.ToDesktopLocator(scope);
                if (scopeLocator.Kind == DesktopLocatorKind.Ref)
                {
                    scopeRef = scopeLocator.Value;
                }
                else
                {
                    var resolved = await surface.LocateAsync(
                        new BrowserLocateRequest(target, scopeLocator, DesktopPageVersion.Unknown, maxResults: 2),
                        call,
                        ct);
                    if (resolved.IsFailure)
                    {
                        return Failure(resolved.Error!);
                    }

                    if (resolved.Value.Elements.Count == 0)
                    {
                        return BrowserToolResponse.Failure(
                            "browser_element_not_found", $"snapshot scope {scopeLocator} matched no element");
                    }

                    if (resolved.Value.Elements.Count > 1)
                    {
                        return BrowserToolResponse.Failure(
                            "browser_locator_ambiguous",
                            $"snapshot scope {scopeLocator} matched more than one element; narrow it down first");
                    }

                    scopeRef = resolved.Value.Elements[0].Reference;
                }
            }

            var snapshot = await surface.SnapshotAsync(
                new BrowserSnapshotRequest(
                    target,
                    // 快照不钉版本：「当前版本即可」由 Unknown 表达（与迁移前"直接用当前页"一致）。
                    DesktopPageVersion.Unknown,
                    BuildOptions(args, mode, fields, scopeRef, args.SinceRevision)),
                call,
                ct);
            if (snapshot.IsFailure)
            {
                return Failure(snapshot.Error!);
            }

            // 游标的页面/版本身份在这里核对：只有拿到快照才知道**当前**版本。
            // 不符就明确拒绝（换页面或文档被改过后，同一个索引指向的是另一份内容——
            // 照读会"看起来读到了"，实际接不上，这种静默错位必须挡住）。
            if (cursorPageIdentity is { Length: > 0 }
                && snapshot.Value.PageVersion.IsKnown
                && !ArticleCursor.MatchesPage(
                    cursorPageIdentity, args.PageId.Trim(), snapshot.Value.PageVersion.Value))
            {
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments",
                    "cursor belongs to another page or an older version of this page;"
                    + " re-read from the beginning (cursors are bound to the page and version that produced them)");
            }

            if (mode == BrowserReadingMode.Full)
            {
                // 表示集合是**唯一判定**：抓取与输出用同一个列表，不会出现"抓了却没说"。
                var representations = SelectedRepresentations(args, fields);

                // §6.1 模式表：full 的输出预算是 16 KiB（默认）/ 64 KiB（硬上限）；超出就**收敛内容**
                // 而不是砍 JSON 尾巴（那样结果会不可解析，§6.1 line 161 明确禁止）。
                var outputBudget = args.MaxBytes ?? FullModeDefaultMaxBytes;
                var scanIncomplete = snapshot.Value.Truncated;
                var value = new BrowserSnapshotToolValue
                {
                    Mode = "full",
                    Representation = string.Join('+', representations),
                    DomText = representations.Contains("domText") ? snapshot.Value.DomText : null,
                    AccessibilityTree = representations.Contains("accessibilityTree") ? snapshot.Value.AccessibilityTree : null,
                    Html = representations.Contains("html") ? snapshot.Value.Html : null,
                    Truncated = scanIncomplete,
                    TruncationReason = scanIncomplete ? "incomplete_scan" : "none",
                    NodeCount = fields.Includes("nodeCount") ? snapshot.Value.NodeCount : 0,
                    Revision = snapshot.Value.Revision,
                    Changed = snapshot.Value.Changed,
                    Diff = BrowserDiffToolValue.From(snapshot.Value.Diff)
                };

                var (bounded, outputCut) = ApplyFullModeBudget(value, outputBudget);
                value = bounded with
                {
                    Truncated = scanIncomplete || outputCut,
                    TruncationReason = (scanIncomplete, outputCut) switch
                    {
                        (true, true) => "incomplete_scan+output_budget",
                        (true, false) => "incomplete_scan",
                        (false, true) => "output_budget",
                        (false, false) => "none",
                    }
                };

                return BrowserToolResponse.Success(
                    value,
                    new BrowserContextId(contextId),
                    new PageId(args.PageId.Trim()),
                    snapshot.Value.PageVersion.IsKnown ? snapshot.Value.PageVersion.Value : null,
                    (scanIncomplete, outputCut) switch
                    {
                        (true, true) => ["snapshot_truncated", "output_budget"],
                        (true, false) => ["snapshot_truncated"],
                        (false, true) => ["output_budget"],
                        (false, false) => null,
                    });
            }

            // 正文 markdown（§6.1 mode=text）：驱动只给**语义块**，组装在这里做——
            // 结构、预算与分页都是纯函数，因此可被完整验证（ArticleMarkdownTests）。
            if (mode == BrowserReadingMode.Text)
            {
                // §151：format 决定表示；两种表示的成本不同 ⇒ 页边界不同 ⇒ 游标**绑定表示**。
                var compactJson = string.Equals(args.Format?.Trim(), "compact_json", StringComparison.OrdinalIgnoreCase);
                var kind = compactJson ? ArticleCursor.CompactJsonKind : ArticleCursor.MarkdownKind;
                var article = compactJson
                    ? ArticleCompactJson.Compose(
                        snapshot.Value.ContentBlocks,
                        args.MaxBytes ?? ArticleCompactJson.DefaultMaxBytes,
                        cursor)
                    : ArticleMarkdown.Compose(
                        snapshot.Value.ContentBlocks,
                        args.MaxBytes ?? ArticleMarkdown.DefaultMaxBytes,
                        cursor);

                return BrowserToolResponse.Success(
                    new BrowserSnapshotToolValue
                    {
                        Mode = BrowserReadingFormatWire.NameOf(mode),
                        Representation = compactJson ? "compact_json" : "markdown",
                        Content = article.Text,
                        Truncated = article.Truncated,
                        TruncationReason = BrowserReadingFormatWire.TruncationNameOf(article.Truncation),
                        NextCursor = article.NextCursor is { } next && snapshot.Value.PageVersion.IsKnown
                            ? ArticleCursor.Encode(kind, next, ArticleCursor.PageIdentityOf(args.PageId.Trim(), snapshot.Value.PageVersion.Value))
                            : null,
                        ReturnedLines = fields.Includes("returnedLines") ? article.ReturnedLines : null,
                        Bytes = fields.Includes("bytes") ? article.Bytes : null,
                        NodeCount = fields.Includes("nodeCount") ? snapshot.Value.NodeCount : 0,
                        Revision = snapshot.Value.Revision,
                        Changed = snapshot.Value.Changed,
                        Diff = BrowserDiffToolValue.From(snapshot.Value.Diff),
                        PageEvents = null
                    },
                    new BrowserContextId(contextId),
                    new PageId(args.PageId.Trim()),
                    snapshot.Value.PageVersion.IsKnown ? snapshot.Value.PageVersion.Value : null,
                    MergeWarnings(BuildArticleWarnings(article), snapshot.Value.PageEvents));
            }

            // outline / interactive：把驱动给的无障碍树按模式过滤并按输出预算收敛。
            // 扫描预算与输出预算分开：扫描被截断时报 incomplete_scan（可能缺整片区域），
            // 只是输出收敛时报 output_budget（可用 nextCursor 续读）。
            var composed = BrowserReadingFormat.Compose(
                snapshot.Value.AccessibilityTree, reading!, snapshot.Value.Truncated);

            return BrowserToolResponse.Success(
                new BrowserSnapshotToolValue
                {
                    Mode = BrowserReadingFormatWire.NameOf(mode),
                    Representation = BrowserReadingFormatWire.NameOf(mode),
                    AccessibilityTree = fields.Includes("accessibilityTree") ? composed.Text : null,
                    Truncated = composed.Truncated,
                    TruncationReason = BrowserReadingFormatWire.TruncationNameOf(composed.Truncation),
                    NextCursor = composed.NextCursor is { } lineNext && snapshot.Value.PageVersion.IsKnown
                        ? ArticleCursor.Encode(
                            ArticleCursor.LinesKind, lineNext,
                            ArticleCursor.PageIdentityOf(args.PageId.Trim(), snapshot.Value.PageVersion.Value))
                        : null,
                    ReturnedLines = fields.Includes("returnedLines") ? composed.ReturnedLines : null,
                    Bytes = fields.Includes("bytes") ? composed.Bytes : null,
                    NodeCount = fields.Includes("nodeCount") ? snapshot.Value.NodeCount : 0,
                    // 修订与差异**由宿主持有基线的一侧判定**（工具不自己算，避免两份判定漂移）。
                    Revision = snapshot.Value.Revision,
                    Changed = snapshot.Value.Changed,
                    Diff = BrowserDiffToolValue.From(snapshot.Value.Diff),
                    PageEvents = PageEventsOf(snapshot.Value.PageEvents)
                },
                new BrowserContextId(contextId),
                new PageId(args.PageId.Trim()),
                snapshot.Value.PageVersion.IsKnown ? snapshot.Value.PageVersion.Value : null,
                MergeWarnings(BuildWarnings(composed), snapshot.Value.PageEvents));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserOperationException ex) { return BrowserToolResponse.FromException(ex); }
    }

    /// <summary>
    /// 页面事件事实的**输出投影**：只回最近几条（有界日志本身已有界，这里再兜一层，防止输出膨胀）。
    /// </summary>
    private static IReadOnlyList<string>? PageEventsOf(IReadOnlyList<string>? events)
    {
        if (events is not { Count: > 0 })
        {
            return null;
        }

        return events.Count <= 4 ? events : events.TakeLast(4).ToArray();
    }

    /// <summary>
    /// 把页面事件并进 <c>warnings</c>：**Agent 必须能自己看到"刚才发生了什么"**，
    /// 否则页面不卡了却没人知道为什么（这轮补的就是这一半价值）。
    /// </summary>
    private static IReadOnlyList<string>? MergeWarnings(
        IReadOnlyList<string>? warnings, IReadOnlyList<string>? pageEvents)
    {
        if (pageEvents is not { Count: > 0 })
        {
            return warnings;
        }

        var merged = new List<string>();
        if (warnings is { Count: > 0 })
        {
            merged.AddRange(warnings);
        }

        merged.AddRange(PageEventsOf(pageEvents)!.Select(summary => $"page_event: {summary}"));
        return merged;
    }

    private static bool TryParseMode(string? mode, out BrowserReadingMode parsed)
    {
        parsed = BrowserReadingMode.Outline;
        if (string.IsNullOrWhiteSpace(mode))
        {
            return true;
        }

        return Enum.TryParse(mode.Trim(), ignoreCase: true, out parsed);
    }

    private static bool TryBuildReadingRequest(
        BrowserSnapshotArgs args, BrowserReadingMode mode, out BrowserReadingRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (mode is BrowserReadingMode.Full or BrowserReadingMode.Text)
        {
            return true;
        }

        var limit = args.Limit ?? BrowserReadingFormat.DefaultLineLimit;
        var maxBytes = args.MaxBytes ?? BrowserReadingFormat.DefaultMaxBytes;

        if (limit is < 1 or > 500)
        {
            error = "snapshot limit must be between 1 and 500 lines";
            return false;
        }

        if (maxBytes is < 64 or > 500_000)
        {
            error = "snapshot max_bytes must be between 64 and 500000";
            return false;
        }

        // 游标在这里不解释（它由外层按模式/表示解析），只需要"给了就是合法的"。
        request = new BrowserReadingRequest(mode, limit, maxBytes, 0);
        return true;
    }

    /// <summary>
    /// 请求选项：outline/interactive 只要无障碍树（DOM 树是体积大头），并使用**内部扫描预算**
    /// （10k 节点 / 深度 64 / 200k 文本）；full 模式沿用调用方给出的完整预算。
    ///
    /// <paramref name="fields"/> 指定表示时**只取那一种**：设计要求「同次默认只返回一种表示」，
    /// 三种表示一起抓下来再丢掉是纯浪费（大页面上三种表示都是 MB 级）。
    /// </summary>
    private static DesktopSnapshotOptions BuildOptions(
        BrowserSnapshotArgs args, BrowserReadingMode mode, OutputFieldSet fields, string? scopeRef, string? sinceRevision) =>
        mode == BrowserReadingMode.Full
            ? new DesktopSnapshotOptions(
                // 表示集合由 SelectedRepresentations 唯一决定（默认只一种，§151）。
                includeDom: SelectedRepresentations(args, fields).Contains("domText"),
                includeAccessibilityTree: SelectedRepresentations(args, fields).Contains("accessibilityTree"),
                includeHtml: SelectedRepresentations(args, fields).Contains("html"),
                maxNodes: args.MaxNodes,
                maxTextLength: args.MaxTextLength,
                includeHidden: args.IncludeHidden,
                includeIframes: args.IncludeIframes,
                includeShadowDom: args.IncludeShadowDom,
                maxDepth: args.MaxDepth,
                scopeRef: scopeRef,
                sinceRevision: sinceRevision)
            : mode == BrowserReadingMode.Text
                // 正文模式只要**语义块**（DOM/无障碍树都不抓：那才是体积大头），
                // markdown 由 ArticleMarkdown 在本地组装。
                ? new DesktopSnapshotOptions(
                    includeDom: false,
                    includeAccessibilityTree: false,
                    includeHtml: false,
                    maxNodes: BrowserReadingFormat.ScanMaxNodes,
                    maxTextLength: BrowserReadingFormat.ScanMaxTextLength,
                    includeHidden: args.IncludeHidden,
                    includeIframes: args.IncludeIframes,
                    includeShadowDom: args.IncludeShadowDom,
                    maxDepth: BrowserReadingFormat.ScanMaxDepth,
                    scopeRef: scopeRef,
                    sinceRevision: sinceRevision,
                    includeContentBlocks: true)
            : new DesktopSnapshotOptions(
                includeDom: false,
                includeAccessibilityTree: true,
                includeHtml: false,
                maxNodes: BrowserReadingFormat.ScanMaxNodes,
                maxTextLength: BrowserReadingFormat.ScanMaxTextLength,
                includeHidden: args.IncludeHidden,
                includeIframes: args.IncludeIframes,
                includeShadowDom: args.IncludeShadowDom,
                maxDepth: BrowserReadingFormat.ScanMaxDepth,
                scopeRef: scopeRef,
                sinceRevision: sinceRevision);

    /// <summary>
    /// 本次 full 模式要抓/要回的表示（设计方案 §151：「**同次默认只返回一种表示**」）。
    /// 规则：调用方显式勾选 `include_*` 或给了 `fields` 白名单 ⇒ 以它为准；
    /// **什么都没说** ⇒ 只给一种（无障碍树，调试时最结构化），不再默默把 DOM 一起倒出来。
    /// </summary>
    private static IReadOnlyList<string> SelectedRepresentations(BrowserSnapshotArgs args, OutputFieldSet fields)
    {
        var selected = new List<string>(3);
        if (fields.IsExplicit)
        {
            // 白名单是**权威**（与 locate 的 fields 语义一致）：列了哪几种就回哪几种。
            if (fields.Includes("accessibilityTree"))
            {
                selected.Add("accessibilityTree");
            }

            if (fields.Includes("domText"))
            {
                selected.Add("domText");
            }

            if (fields.Includes("html"))
            {
                selected.Add("html");
            }

            return selected;
        }

        if (args.IncludeAccessibilityTree)
        {
            selected.Add("accessibilityTree");
        }

        if (args.IncludeDom)
        {
            selected.Add("domText");
        }

        if (args.IncludeHtml)
        {
            selected.Add("html");
        }

        if (selected.Count == 0)
        {
            // **同次默认只返回一种表示**（§151）：不再默默把 DOM 一起倒出来。
            selected.Add("accessibilityTree");
        }

        return selected;
    }

    /// <summary>正文读取的告警：丢弃块与截断都要看得见。</summary>
    private static IReadOnlyList<string>? BuildArticleWarnings(BrowserReadingResult article)
    {
        var warnings = new List<string>();
        if (article.SkippedBlocks > 0)
        {
            warnings.Add($"snapshot_blocks_skipped:{article.SkippedBlocks}");
        }

        if (article.Truncation == BrowserReadingTruncation.OutputBudget)
        {
            warnings.Add("snapshot_output_budget");
        }

        return warnings.Count == 0 ? null : warnings;
    }

    private static IReadOnlyList<string>? BuildWarnings(BrowserReadingResult composed) => composed.Truncation switch
    {
        BrowserReadingTruncation.IncompleteScan => ["snapshot_scan_incomplete"],
        BrowserReadingTruncation.OutputBudget => ["snapshot_output_budget"],
        _ => null,
    };

    /// <summary>快照的版本不符语义是"页面已变"，不是"引用过期"。</summary>
    private static ToolExecutionResult Failure(DesktopCapabilityError error) =>
        BrowserCapabilityFailure.From(error, "browser_snapshot_failed", "stale_page_version");
}
