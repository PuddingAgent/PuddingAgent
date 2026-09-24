namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// "下一步"的种类（ADR-089 §2.1 第 3/7 条 + §2.2 C）：
/// 空结果与过载都<b>必须</b>给出恰好一条可执行建议 —— 给菜单等于让 Agent 打转。
/// </summary>
public enum RetrievalNextStepKind
{
    /// <summary>收窄查询（过载 / 泛匹配时用）。</summary>
    NarrowQuery = 0,

    /// <summary>放宽过滤面（过滤性空结果时用）。</summary>
    RelaxFilter = 1,

    /// <summary>重建/修复索引（索引缺失或损坏时用）。</summary>
    RebuildIndex = 2,

    /// <summary>换一个面（语言/意图不支持时给替代方案）。</summary>
    UseAlternativeFace = 3,

    /// <summary>接受缺失（过滤面干净时用：确实不存在）。</summary>
    AcceptAbsence = 4,
}

/// <summary>
/// 收窄手段（ADR-089 §2.2 C）：**恰好四类**，不许自由文本乱写。
/// <para>顺序即优选顺序：目录限制 → 文件类型 → 组合条件（多词/符号限定）→ 显式 intent。</para>
/// </summary>
public enum RetrievalSuggestionKind
{
    /// <summary>目录 / 子目录限制。</summary>
    DirectoryLimit = 0,

    /// <summary>文件类型过滤。</summary>
    FileTypeFilter = 1,

    /// <summary>组合条件（多词 / 符号限定）。</summary>
    CompositeFilter = 2,

    /// <summary>显式 intent。</summary>
    ExplicitIntent = 3,
}

/// <summary>
/// 一条"下一步"（ADR-089 §2.1 第 3/7 条）：结果里<b>至多一条</b>（由 <see cref="RetrievalResult"/> 强制）。
/// <para>
/// 载荷与种类必须一致（<c>NarrowQuery ⇔ Narrowing</c>、<c>RelaxFilter ⇔ Relaxing</c>、
/// <c>UseAlternativeFace ⇔ Alternative</c>），所以"说要收窄却不说收窄哪一类"
/// 这种空洞建议在结构上不可表示。
/// </para>
/// </summary>
public sealed record RetrievalNextStep
{
    private RetrievalNextStep(
        RetrievalNextStepKind kind,
        string text,
        RetrievalSuggestionKind? narrowing,
        RetrievalFacet? relaxing,
        string? alternative,
        string? argument)
    {
        Kind = kind;
        Text = text;
        Narrowing = narrowing;
        Relaxing = relaxing;
        Alternative = alternative;
        Argument = argument;
    }

    /// <summary>建议种类。</summary>
    public RetrievalNextStepKind Kind { get; }

    /// <summary>给 Agent 看的可执行文本（必填）。</summary>
    public string Text { get; }

    /// <summary>收窄手段（仅 <see cref="RetrievalNextStepKind.NarrowQuery"/>）。</summary>
    public RetrievalSuggestionKind? Narrowing { get; }

    /// <summary>放宽的面（仅 <see cref="RetrievalNextStepKind.RelaxFilter"/>）。</summary>
    public RetrievalFacet? Relaxing { get; }

    /// <summary>替代方案文本（仅 <see cref="RetrievalNextStepKind.UseAlternativeFace"/>）。</summary>
    public string? Alternative { get; }

    /// <summary>可选参数（例如具体目录 / 扩展名）。</summary>
    public string? Argument { get; }

    /// <summary>收窄建议（四类之一）。</summary>
    public static RetrievalNextStep Narrow(RetrievalSuggestionKind kind, string text, string? argument = null) =>
        new(RetrievalNextStepKind.NarrowQuery, RequireText(text), kind, null, null, Clean(argument));

    /// <summary>放宽某个过滤面的建议。</summary>
    public static RetrievalNextStep Relax(RetrievalFacet facet, string text) =>
        new(RetrievalNextStepKind.RelaxFilter, RequireText(text), null, facet, null, null);

    /// <summary>重建索引的建议。</summary>
    public static RetrievalNextStep RebuildIndex(string text, string? argument = null) =>
        new(RetrievalNextStepKind.RebuildIndex, RequireText(text), null, null, null, Clean(argument));

    /// <summary>替代方案（语言/意图不支持时必须给）。</summary>
    public static RetrievalNextStep UseAlternativeFace(string text, string alternative) =>
        new(RetrievalNextStepKind.UseAlternativeFace, RequireText(text), null, null, RequireText(alternative), null);

    /// <summary>接受缺失（确实不存在时用）。</summary>
    public static RetrievalNextStep AcceptAbsence(string text) =>
        new(RetrievalNextStepKind.AcceptAbsence, RequireText(text), null, null, null, null);

    private static string RequireText(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? throw new ArgumentException("建议文本不得为空。", nameof(text))
            : text.Trim();

    private static string? Clean(string? argument) =>
        string.IsNullOrWhiteSpace(argument) ? null : argument.Trim();
}
