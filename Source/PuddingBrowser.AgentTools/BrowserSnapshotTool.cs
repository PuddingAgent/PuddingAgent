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

    /// <summary>输出行数上限（仅 outline/interactive 生效，默认 50）。</summary>
    [ToolParam("Maximum outline lines to return (default 50; outline/interactive only).")]
    public int? Limit { get; init; }

    /// <summary>输出字节上限（仅 outline/interactive 生效，默认 4096）。</summary>
    [ToolParam("Maximum UTF-8 bytes of outline text to return (default 4096; outline/interactive only).")]
    public int? MaxBytes { get; init; }

    /// <summary>游标：从上次结果给出的 <c>nextCursor</c> 继续读（仅 outline/interactive）。</summary>
    [ToolParam("Cursor from a previous result's nextCursor to continue reading (outline/interactive only).")]
    public int? Cursor { get; init; }

    // ── full 模式专用（outline/interactive 下被忽略，输出预算由 limit/max_bytes 决定）──
    public bool IncludeDom { get; init; } = true;
    public bool IncludeAccessibilityTree { get; init; } = true;
    public bool IncludeHidden { get; init; }
    public bool IncludeIframes { get; init; } = true;
    public bool IncludeShadowDom { get; init; } = true;
    public bool IncludeHtml { get; init; }
    public int MaxNodes { get; init; } = 5_000;
    public int MaxTextLength { get; init; } = 200_000;
    public int MaxDepth { get; init; } = 24;
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
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserSnapshotArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (!TryParseMode(args.Mode, out var mode))
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments",
                "snapshot mode must be one of outline, interactive, text, or full");
        }

        if (mode == BrowserReadingMode.Text)
        {
            // 正文 markdown 需要驱动侧正文提取（设计方案 §6.1）；工具层不做伪造，
            // 也**不**静默降级成 full —— 那会让调用方以为拿到了正文。
            return BrowserToolResponse.Failure(
                "browser_operation_not_supported",
                "snapshot mode 'text' (article markdown) is not implemented yet; use outline/interactive for actionable nodes or full for troubleshooting");
        }

        if (mode == BrowserReadingMode.Full
            && (args.MaxNodes is < 1 or > 10_000 || args.MaxTextLength is < 256 or > 500_000
                || args.MaxDepth is < 1 or > 64))
        {
            return BrowserToolResponse.Failure("browser_invalid_arguments", "snapshot budgets are outside allowed ranges");
        }

        if (!TryBuildReadingRequest(args, mode, out var reading, out var readingError))
        {
            return BrowserToolResponse.Failure("browser_invalid_arguments", readingError!);
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
            var snapshot = await surface.SnapshotAsync(
                new BrowserSnapshotRequest(
                    target,
                    // 快照不钉版本：「当前版本即可」由 Unknown 表达（与迁移前"直接用当前页"一致）。
                    DesktopPageVersion.Unknown,
                    BuildOptions(args, mode)),
                call,
                ct);
            if (snapshot.IsFailure)
            {
                return Failure(snapshot.Error!);
            }

            if (mode == BrowserReadingMode.Full)
            {
                return BrowserToolResponse.Success(
                    new BrowserSnapshotToolValue
                    {
                        Mode = "full",
                        DomText = snapshot.Value.DomText,
                        AccessibilityTree = snapshot.Value.AccessibilityTree,
                        Html = snapshot.Value.Html,
                        Truncated = snapshot.Value.Truncated,
                        TruncationReason = snapshot.Value.Truncated ? "incomplete_scan" : "none",
                        NodeCount = snapshot.Value.NodeCount
                    },
                    new BrowserContextId(contextId),
                    new PageId(args.PageId.Trim()),
                    snapshot.Value.PageVersion.IsKnown ? snapshot.Value.PageVersion.Value : null,
                    snapshot.Value.Truncated ? ["snapshot_truncated"] : null);
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
                    AccessibilityTree = composed.Text,
                    Truncated = composed.Truncated,
                    TruncationReason = BrowserReadingFormatWire.TruncationNameOf(composed.Truncation),
                    NextCursor = composed.NextCursor,
                    ReturnedLines = composed.ReturnedLines,
                    Bytes = composed.Bytes,
                    NodeCount = snapshot.Value.NodeCount
                },
                new BrowserContextId(contextId),
                new PageId(args.PageId.Trim()),
                snapshot.Value.PageVersion.IsKnown ? snapshot.Value.PageVersion.Value : null,
                BuildWarnings(composed));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserOperationException ex) { return BrowserToolResponse.FromException(ex); }
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

        if (mode == BrowserReadingMode.Full)
        {
            return true;
        }

        var limit = args.Limit ?? BrowserReadingFormat.DefaultLineLimit;
        var maxBytes = args.MaxBytes ?? BrowserReadingFormat.DefaultMaxBytes;
        var cursor = args.Cursor ?? 0;

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

        if (cursor < 0)
        {
            error = "snapshot cursor must not be negative";
            return false;
        }

        request = new BrowserReadingRequest(mode, limit, maxBytes, cursor);
        return true;
    }

    /// <summary>
    /// 请求选项：outline/interactive 只要无障碍树（DOM 树是体积大头），并使用**内部扫描预算**
    /// （10k 节点 / 深度 64 / 200k 文本）；full 模式沿用调用方给出的完整预算。
    /// </summary>
    private static DesktopSnapshotOptions BuildOptions(BrowserSnapshotArgs args, BrowserReadingMode mode) =>
        mode == BrowserReadingMode.Full
            ? new DesktopSnapshotOptions(
                includeDom: args.IncludeDom,
                includeAccessibilityTree: args.IncludeAccessibilityTree,
                includeHtml: args.IncludeHtml,
                maxNodes: args.MaxNodes,
                maxTextLength: args.MaxTextLength,
                includeHidden: args.IncludeHidden,
                includeIframes: args.IncludeIframes,
                includeShadowDom: args.IncludeShadowDom,
                maxDepth: args.MaxDepth)
            : new DesktopSnapshotOptions(
                includeDom: false,
                includeAccessibilityTree: true,
                includeHtml: false,
                maxNodes: BrowserReadingFormat.ScanMaxNodes,
                maxTextLength: BrowserReadingFormat.ScanMaxTextLength,
                includeHidden: args.IncludeHidden,
                includeIframes: args.IncludeIframes,
                includeShadowDom: args.IncludeShadowDom,
                maxDepth: BrowserReadingFormat.ScanMaxDepth);

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
