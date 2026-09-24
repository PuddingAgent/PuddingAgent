namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 能力面（ADR-089 §4 / §5.1）：能力矩阵要回答"这门语言 / 这个面支持到哪一级"。
/// </summary>
public enum RetrievalCapabilityFace
{
    /// <summary>文本面（字面量 / 正则）。</summary>
    TextSearch = 0,

    /// <summary>结构提取（outliner 级）。</summary>
    SymbolExtraction = 1,

    /// <summary>符号入库（可被结构面检索）。</summary>
    SymbolIndexing = 2,

    /// <summary>关系图（Calls / Inherits …）。</summary>
    RelationGraph = 3,

    /// <summary>引用关系（<c>References</c>；本设计的主要新增）。</summary>
    References = 4,

    /// <summary>文档注释（支撑 <see cref="RetrievalIntent.Comment"/> 与"文档引用了它"）。</summary>
    DocComments = 5,
}

/// <summary>能力级别：<see cref="None"/> = 明确"未实现"，绝不假装支持。</summary>
public enum RetrievalCapabilityLevel
{
    /// <summary>不支持（必须附替代方案）。</summary>
    None = 0,

    /// <summary>词法级（如 outliner，置信度低于语义级）。</summary>
    Lexical = 1,

    /// <summary>语义级（如 Roslyn）。</summary>
    Semantic = 2,
}

/// <summary>
/// 一门语言在一个面上的能力（ADR-089 §4 的能力矩阵条目 + §5.2 / §8.3）。
/// <para>
/// 硬不变量：<b>不支持 ⇔ 必须给出替代方案</b>（<see cref="Alternative"/> 非空）。
/// 于是"静默返回空 / 假装支持"在结构上不可表示 —— 这正是 A6 要钉住的东西。
/// 支持的面上用 <see cref="Note"/> 写备注，不占用 <see cref="Alternative"/>。
/// </para>
/// </summary>
public sealed record LanguageCapability
{
    private LanguageCapability(
        string languageId,
        RetrievalCapabilityFace face,
        RetrievalCapabilityLevel level,
        string? alternative,
        string? note)
    {
        LanguageId = languageId;
        Face = face;
        Level = level;
        Alternative = alternative;
        Note = note;
    }

    /// <summary>语言标识（如 <c>csharp</c> / <c>go</c>；与 <see cref="RetrievalPathFacts.LanguageOf"/> 同一套写法）。</summary>
    public string LanguageId { get; }

    /// <summary>能力面。</summary>
    public RetrievalCapabilityFace Face { get; }

    /// <summary>能力级别。</summary>
    public RetrievalCapabilityLevel Level { get; }

    /// <summary>替代方案（不支持时必填）。</summary>
    public string? Alternative { get; }

    /// <summary>备注（支持时的可选说明）。</summary>
    public string? Note { get; }

    /// <summary>是否支持该面。</summary>
    public bool IsSupported => Level != RetrievalCapabilityLevel.None;

    /// <summary>声明"支持该面"（<paramref name="level"/> 不得为 <see cref="RetrievalCapabilityLevel.None"/>）。</summary>
    public static LanguageCapability Supported(
        string languageId,
        RetrievalCapabilityFace face,
        RetrievalCapabilityLevel level,
        string? note = null)
    {
        if (level == RetrievalCapabilityLevel.None)
            throw new ArgumentException("要声明\"不支持\"请用 LanguageCapability.Unsupported（它强制给出替代方案）。", nameof(level));

        return new LanguageCapability(
            RequireLanguageId(languageId),
            face,
            level,
            null,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim());
    }

    /// <summary>声明"不支持该面"，<b>必须</b>给出替代方案（§5.2）。</summary>
    public static LanguageCapability Unsupported(
        string languageId,
        RetrievalCapabilityFace face,
        string alternative)
    {
        if (string.IsNullOrWhiteSpace(alternative))
            throw new ArgumentException(
                "声明不支持时必须给出替代方案（例如\"用文本面检索 SymbolName\"）—— 不允许\"假装支持\"或静默返回空。",
                nameof(alternative));

        return new LanguageCapability(
            RequireLanguageId(languageId),
            face,
            RetrievalCapabilityLevel.None,
            alternative.Trim(),
            null);
    }

    private static string RequireLanguageId(string languageId) =>
        string.IsNullOrWhiteSpace(languageId)
            ? throw new ArgumentException("能力矩阵条目必须有语言标识。", nameof(languageId))
            : languageId.Trim().ToLowerInvariant();
}

/// <summary>
/// 语言能力矩阵端口（ADR-089 §4）：回答"这门语言 / 这个面支持到哪一级"，
/// 并让工具能对 Agent **说"不支持"并给替代**（§5.2：不是静默降级）。
/// <para>本刀只冻结端口与条目的诚实性，<b>不实现</b>具体语言数据（那是 U4-4）。</para>
/// </summary>
public interface ILanguageCapabilityMatrix
{
    /// <summary>查询某语言在某面上的能力（未知语言 ⇒ <see cref="LanguageCapability.Unsupported"/> + 替代方案）。</summary>
    LanguageCapability Resolve(string languageId, RetrievalCapabilityFace face);

    /// <summary>列出某语言各面的能力（用于对 Agent 声明能力）。</summary>
    IReadOnlyList<LanguageCapability> Describe(string languageId);
}
