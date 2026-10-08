namespace PuddingBrowser.Automation;

/// <summary>
/// 快照阅读模式（设计方案 §6.1）。默认 <see cref="Outline"/> —— 「先看到能点什么、能填什么」，
/// 而不是把整棵 DOM 与无障碍树一起倒给模型。
/// </summary>
public enum BrowserReadingMode
{
    /// <summary>可交互节点 + 标题/地标结构行（默认）。</summary>
    Outline,

    /// <summary>只保留可交互节点（带 ref 的行）。</summary>
    Interactive,

    /// <summary>正文 markdown。<b>尚未实现</b>：需要驱动侧正文提取，工具层不做伪造。</summary>
    Text,

    /// <summary>完整 DOM + 无障碍树（排障用，必须显式开启）。</summary>
    Full,
}

/// <summary>截断原因。区分「扫描不完整」与「输出超预算」是关键：前者意味着**有节点从未被看到**。</summary>
public enum BrowserReadingTruncation
{
    None,

    /// <summary>扫描侧就被截断（节点/深度/文本上限）⇒ 结果可能缺整片区域。</summary>
    IncompleteScan,

    /// <summary>扫描完整，只是输出按行数/字节数收敛 ⇒ 可用 cursor 继续读。</summary>
    OutputBudget,
}

/// <summary>阅读请求：输出预算与游标（与**内部扫描预算**分开，见设计方案 §6.1）。</summary>
public sealed record BrowserReadingRequest(
    BrowserReadingMode Mode = BrowserReadingMode.Outline,
    int Limit = BrowserReadingFormat.DefaultLineLimit,
    int MaxBytes = BrowserReadingFormat.DefaultMaxBytes,
    int Cursor = 0);

/// <summary>阅读结果：投影文本 + 如实标注的截断状态与游标。</summary>
public sealed record BrowserReadingResult(
    string? Text,
    int SourceLines,
    int ReturnedLines,
    int Bytes,
    bool Truncated,
    BrowserReadingTruncation Truncation,
    int? NextCursor)
{
    /// <summary>
    /// 组装时被**如实计数**的丢弃块（非法行、空块、超出块数上限）。
    /// 「无丢段」是可审计的：这个数不为 0 就说明有内容没进结果，而不是装作完整。
    /// </summary>
    public int SkippedBlocks { get; init; }
}

/// <summary>阅读模式与截断原因 ↔ 线名（fail closed：未登记值取红）。</summary>
public static class BrowserReadingFormatWire
{
    public static string NameOf(BrowserReadingMode mode) => mode switch
    {
        BrowserReadingMode.Outline => "outline",
        BrowserReadingMode.Interactive => "interactive",
        BrowserReadingMode.Text => "text",
        BrowserReadingMode.Full => "full",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Reading mode is not registered."),
    };

    public static bool TryParse(string? name, out BrowserReadingMode mode)
    {
        foreach (var candidate in Enum.GetValues<BrowserReadingMode>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.OrdinalIgnoreCase))
            {
                mode = candidate;
                return true;
            }
        }

        mode = default;
        return false;
    }

    public static string TruncationNameOf(BrowserReadingTruncation truncation) => truncation switch
    {
        BrowserReadingTruncation.None => "none",
        BrowserReadingTruncation.IncompleteScan => "incomplete_scan",
        BrowserReadingTruncation.OutputBudget => "output_budget",
        _ => throw new ArgumentOutOfRangeException(
            nameof(truncation), truncation, "Reading truncation is not registered."),
    };
}

/// <summary>
/// 阅读面收敛（设计方案 §6.1/§6.2）：把驱动给出的无障碍树**按模式过滤**并按输出预算收敛。
///
/// 为什么在工具/组件层而不是驱动里做：驱动负责采集，格式化是可独立测试的纯逻辑
/// （设计 §2「新组件公开 normalized reading 输入模型，DOM 采集留驱动侧」）。
///
/// 三条硬规则：
/// ① **只做投影，不编造**：只保留驱动自己标记过的行（带 <c>ref=</c> 的行是驱动对"可交互"的判定），
///    以及角色为标题/地标的行；不新增、不改写内容；
/// ② 输出预算（行数/UTF-8 字节）与**内部扫描预算**分开：扫描截断时报
///    <see cref="BrowserReadingTruncation.IncompleteScan"/>（可能缺整片区域），
///    只是输出收敛时报 <see cref="BrowserReadingTruncation.OutputBudget"/>（可用 cursor 续读）；
/// ③ 截断必须显式标注并给出 <see cref="BrowserReadingResult.NextCursor"/>，不静默丢内容。
/// </summary>
public static class BrowserReadingFormat
{
    /// <summary>默认输出行数上限（设计方案 §6.1：50 行）。</summary>
    public const int DefaultLineLimit = 50;

    /// <summary>默认输出字节上限（设计方案 §6.1：4 KB）。</summary>
    public const int DefaultMaxBytes = 4096;

    /// <summary>内部扫描预算：与输出预算无关，避免「深层正文/结果列表因为输出太小而丢失」。</summary>
    public const int ScanMaxNodes = 10_000;

    /// <summary>内部扫描文本预算。</summary>
    public const int ScanMaxTextLength = 200_000;

    /// <summary>内部扫描深度预算。</summary>
    public const int ScanMaxDepth = 64;

    /// <summary>结构行角色：标题与地标让模型知道"页面有哪些区块"，不含内容。</summary>
    private static readonly string[] StructuralRoles =
    [
        "heading", "banner", "navigation", "main", "region", "contentinfo", "complementary",
        "form", "search", "article", "dialog", "alert", "tablist", "table", "list",
    ];

    /// <summary>
    /// 按模式与预算投影无障碍树。<paramref name="scanTruncated"/> 来自驱动（扫描侧被截断）。
    /// </summary>
    public static BrowserReadingResult Compose(
        string? accessibilityTree,
        BrowserReadingRequest request,
        bool scanTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        if (!Enum.IsDefined(request.Mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.Mode, "Reading mode is not registered.");
        }

        var allLines = SplitLines(accessibilityTree);
        var selected = Select(allLines, request.Mode);

        // Full = 全量（排障模式）：不施加输出预算。工具层不会把 full 送进来，
        // 但语义必须自洽——否则「full」会静默变成另一个有预算的模式。
        if (request.Mode == BrowserReadingMode.Full)
        {
            var fullText = selected.Count == 0 ? null : string.Join('\n', selected);
            return new BrowserReadingResult(
                fullText,
                allLines.Count,
                selected.Count,
                fullText is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(fullText),
                scanTruncated,
                scanTruncated ? BrowserReadingTruncation.IncompleteScan : BrowserReadingTruncation.None,
                null);
        }

        var from = Math.Min(request.Cursor, selected.Count);
        var taken = new List<string>(Math.Min(request.Limit, selected.Count - from));
        var bytes = 0;

        for (var index = from; index < selected.Count && taken.Count < request.Limit; index++)
        {
            var line = selected[index];
            var lineBytes = System.Text.Encoding.UTF8.GetByteCount(line) + (taken.Count == 0 ? 0 : 1);
            if (taken.Count > 0 && bytes + lineBytes > request.MaxBytes)
            {
                break;
            }

            // 单行就超预算：仍然给出第一行（否则永远读不到东西），但如实标注截断。
            taken.Add(line);
            bytes += lineBytes;
        }

        var consumedAll = from + taken.Count >= selected.Count;
        var truncated = scanTruncated || !consumedAll;
        var reason = scanTruncated
            ? BrowserReadingTruncation.IncompleteScan
            : consumedAll
                ? BrowserReadingTruncation.None
                : BrowserReadingTruncation.OutputBudget;

        return new BrowserReadingResult(
            taken.Count == 0 ? null : string.Join('\n', taken),
            allLines.Count,
            taken.Count,
            bytes,
            truncated,
            reason,
            reason == BrowserReadingTruncation.OutputBudget ? from + taken.Count : null);
    }

    private static void Validate(BrowserReadingRequest request)
    {
        if (request.Limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Limit, "Line limit must be at least 1.");
        }

        if (request.MaxBytes < 64)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.MaxBytes, "Byte budget must be at least 64.");
        }

        if (request.Cursor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Cursor, "Cursor must not be negative.");
        }
    }

    private static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static List<string> Select(List<string> lines, BrowserReadingMode mode)
    {
        if (mode == BrowserReadingMode.Full)
        {
            return lines;
        }

        var selected = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (mode == BrowserReadingMode.Interactive)
            {
                if (IsInteractive(line))
                {
                    selected.Add(line);
                }

                continue;
            }

            // outline：可交互行 + 结构行（标题/地标）。
            if (IsInteractive(line) || IsStructural(line))
            {
                selected.Add(line);
            }
        }

        return selected;
    }

    /// <summary>驱动只在可交互元素上写 <c>ref=</c>，因此这是驱动自己的判定，不是我们猜的。</summary>
    private static bool IsInteractive(string line) =>
        line.Contains("ref=", StringComparison.Ordinal);

    private static bool IsStructural(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        var end = trimmed.IndexOf(' ');
        var role = end < 0 ? trimmed : trimmed[..end];
        foreach (var candidate in StructuralRoles)
        {
            if (role.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
