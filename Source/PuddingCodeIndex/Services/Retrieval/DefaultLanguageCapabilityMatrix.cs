using PuddingRetrieval.Contracts;

namespace PuddingCodeIndex.Services.Retrieval;

/// <summary>
/// 默认能力矩阵（ADR-089 §4 / §5.2，施工计划 R0-03）。
/// <para>
/// <b>填写原则：只声明有实测证据的「支持」</b>，其余一律
/// <see cref="LanguageCapability.Unsupported"/> 并给出替代方案。
/// 宁可把「其实能做但没测过」写成不支持，也不能反过来 ——
/// 前者只是保守，后者会让 Agent 在错误的面上反复空转。
/// </para>
/// <para>
/// 各条的证据来源记在 <c>note</c> 里；没有证据的面不写 note（因为它是 Unsupported）。
/// </para>
/// </summary>
public sealed class DefaultLanguageCapabilityMatrix : ILanguageCapabilityMatrix
{
    /// <summary>C# 标识（与 <c>RetrievalPathFacts.LanguageOf</c> 同一套写法）。</summary>
    public const string CSharp = "csharp";

    /// <summary>TypeScript/TSX 标识。</summary>
    public const string TypeScript = "typescript";

    /// <summary>Markdown 标识。</summary>
    public const string Markdown = "markdown";

    private static readonly Dictionary<(string Language, RetrievalCapabilityFace Face), LanguageCapability> Entries =
        BuildEntries();

    /// <summary>未知语言的统一替代方案：不假装知道，直接指向能用的面。</summary>
    public const string UnknownLanguageAlternative =
        "该语言未建立能力矩阵；请改用 search_grep（按行匹配）或 workspace_search 的词面面检索，"
        + "不要假设结构面/关系面可用。";

    private static Dictionary<(string, RetrievalCapabilityFace), LanguageCapability> BuildEntries()
    {
        var entries = new Dictionary<(string, RetrievalCapabilityFace), LanguageCapability>
        {
            // ── C#：Roslyn 支撑 Semantic，六个面里五个有实测证据 ──────────────────
            [LanguageEntry(CSharp, RetrievalCapabilityFace.TextSearch)] =
                LanguageCapability.Supported(CSharp, RetrievalCapabilityFace.TextSearch,
                    RetrievalCapabilityLevel.Lexical,
                    "全文索引按行匹配；不区分声明与调用点"),

            [LanguageEntry(CSharp, RetrievalCapabilityFace.SymbolExtraction)] =
                LanguageCapability.Supported(CSharp, RetrievalCapabilityFace.SymbolExtraction,
                    RetrievalCapabilityLevel.Semantic,
                    "Roslyn 语义抽取；跨度取声明体（EndLine 修复后为真实范围）"),

            [LanguageEntry(CSharp, RetrievalCapabilityFace.SymbolIndexing)] =
                LanguageCapability.Supported(CSharp, RetrievalCapabilityFace.SymbolIndexing,
                    RetrievalCapabilityLevel.Semantic,
                    "CodeSymbols 落库，可被类型/成员面检索"),

            [LanguageEntry(CSharp, RetrievalCapabilityFace.RelationGraph)] =
                LanguageCapability.Supported(CSharp, RetrievalCapabilityFace.RelationGraph,
                    RetrievalCapabilityLevel.Semantic,
                    "Calls/Inherits 等边来自编译器；实测 callers 可返回带行号的调用点"),

            [LanguageEntry(CSharp, RetrievalCapabilityFace.References)] =
                LanguageCapability.Supported(CSharp, RetrievalCapabilityFace.References,
                    RetrievalCapabilityLevel.Semantic,
                    "references 面实测可用（成员锚点）"),

            // 注释面：索引器能产，但检索侧当前不可用（R1-A：scope→目录映射错位）。
            // 按"可用性"而非"索引器能力"声明 —— Agent 关心的是能不能查到。
            [LanguageEntry(CSharp, RetrievalCapabilityFace.DocComments)] =
                LanguageCapability.Unsupported(CSharp, RetrievalCapabilityFace.DocComments,
                    "注释文本面当前不可检索（scope 与全文索引目录不一致，见 R1 诊断）；"
                    + "改用 search_grep 按行匹配注释原文"),

            // ── TypeScript：只有 outliner 级结构提取，无静态绑定 ──────────────────
            [LanguageEntry(TypeScript, RetrievalCapabilityFace.SymbolExtraction)] =
                LanguageCapability.Supported(TypeScript, RetrievalCapabilityFace.SymbolExtraction,
                    RetrievalCapabilityLevel.Lexical,
                    "outliner 级提取；无静态绑定，同名的不同符号无法区分"),

            [LanguageEntry(TypeScript, RetrievalCapabilityFace.TextSearch)] =
                LanguageCapability.Supported(TypeScript, RetrievalCapabilityFace.TextSearch,
                    RetrievalCapabilityLevel.Lexical,
                    "全文索引按行匹配"),

            // ── Markdown：只做文本面 ────────────────────────────────────────────
            [LanguageEntry(Markdown, RetrievalCapabilityFace.TextSearch)] =
                LanguageCapability.Supported(Markdown, RetrievalCapabilityFace.TextSearch,
                    RetrievalCapabilityLevel.Lexical,
                    "按行匹配；无结构"),
        };

        return entries;

        static (string, RetrievalCapabilityFace) LanguageEntry(string language, RetrievalCapabilityFace face) =>
            (language, face);
    }

    /// <inheritdoc />
    public LanguageCapability Resolve(string languageId, RetrievalCapabilityFace face)
    {
        if (string.IsNullOrWhiteSpace(languageId))
            return LanguageCapability.Unsupported("unknown", face, UnknownLanguageAlternative);

        var normalized = languageId.Trim().ToLowerInvariant();

        // 未知语言必须显式说"不支持 + 怎么办"，不能返回 Supported(None) —— 后者会被读成"支持但空"。
        return Entries.TryGetValue((normalized, face), out var capability)
            ? capability
            : LanguageCapability.Unsupported(
                normalized,
                face,
                $"语言 '{normalized}' 在面 {face} 上没有已声明能力；{UnknownLanguageAlternative}");
    }

    /// <inheritdoc />
    public IReadOnlyList<LanguageCapability> Describe(string languageId)
    {
        var capabilities = new List<LanguageCapability>();
        foreach (RetrievalCapabilityFace face in Enum.GetValues<RetrievalCapabilityFace>())
            capabilities.Add(Resolve(languageId, face));

        return capabilities;
    }

    /// <summary>已写入矩阵的语言（用于输出覆盖矩阵，而不是靠遍历猜测）。</summary>
    public static IReadOnlyList<string> DeclaredLanguages { get; } =
        Entries.Keys.Select(key => key.Language).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
