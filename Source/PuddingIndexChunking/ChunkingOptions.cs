namespace PuddingIndexChunking;

/// <summary>
/// Filter rules applied to chunk text before it is handed to an index (ADR-089 C2).
/// <para>
/// Every rule is switchable, because the U4-1a experiment has to answer <b>what each rule
/// contributes</b>: "the index got smaller" is only meaningful if the two rules can be turned off
/// independently and the numbers compared.
/// </para>
/// </summary>
public sealed record ChunkFilterOptions
{
    /// <summary>Drop tokens that are keywords of the chunk's programming language (e.g. <c>if</c>, <c>else</c>, <c>public</c>).</summary>
    public bool RemoveStopWords { get; init; } = true;

    /// <summary>Drop tokens shorter than the language's minimum token length.</summary>
    public bool RemoveShortTokens { get; init; } = true;

    /// <summary>
    /// Overrides the per-language minimum token length when set. <c>null</c> keeps the language
    /// default from <see cref="ChunkFilterRules.DefaultMinimumTokenLengthFor"/>.
    /// </summary>
    public int? MinimumTokenLength { get; init; }

    /// <summary>
    /// Match stop words case-insensitively, so <c>IF</c> is recognised as the keyword <c>if</c>.
    /// Turning this off makes the rule literal-case — which is exactly the对照 the tests use to prove
    /// the rule is actually consulted.
    /// </summary>
    public bool IgnoreCase { get; init; } = true;

    /// <summary>True when neither rule is enabled (text passes through unchanged).</summary>
    public bool IsNoOp => !RemoveStopWords && !RemoveShortTokens;
}

/// <summary>What a filter run removed, per rule. Evidence for ADR-089 C2 at corpus scale.</summary>
/// <param name="TokensIn">Tokens seen in the input.</param>
/// <param name="TokensKept">Tokens surviving both rules.</param>
/// <param name="RemovedAsShort">Tokens dropped by the minimum-length rule (checked first).</param>
/// <param name="RemovedAsStopWord">Tokens dropped by the keyword rule.</param>
public sealed record FilterReport(int TokensIn, int TokensKept, int RemovedAsShort, int RemovedAsStopWord)
{
    /// <summary>Aggregate of several reports (per-corpus totals).</summary>
    public static FilterReport operator +(FilterReport left, FilterReport right) => new(
        left.TokensIn + right.TokensIn,
        left.TokensKept + right.TokensKept,
        left.RemovedAsShort + right.RemovedAsShort,
        left.RemovedAsStopWord + right.RemovedAsStopWord);

    /// <summary>Empty total, used as the seed when aggregating a corpus.</summary>
    public static FilterReport Zero { get; } = new(0, 0, 0, 0);
}

/// <summary>Filtered text plus the per-rule counts that produced it.</summary>
public sealed record FilteredChunkText(string Text, FilterReport Report);

/// <summary>
/// Assembler configuration: which tiers are emitted, and how their text is filtered.
/// </summary>
public sealed record ChunkingOptions
{
    /// <summary>Emit one P0 chunk per outline symbol.</summary>
    public bool EmitOutlineChunks { get; init; } = true;

    /// <summary>Emit P1 chunks for comment blocks (or prose blocks in non-code files).</summary>
    public bool EmitDocCommentChunks { get; init; } = true;

    /// <summary>Emit P2 chunks for the remaining code text (the fallback tier of ADR-089 C1).</summary>
    public bool EmitCodeTextChunks { get; init; } = true;

    /// <summary>A contiguous code run longer than this is split into several P2 chunks.</summary>
    public int MaxCodeLinesPerChunk { get; init; } = 40;

    /// <summary>
    /// Append the symbol's documentation summary to its P0 chunk. ADR-089 C1 lists 文档注释 as part of
    /// the P0 payload; when this is on, the comment block that precedes such a symbol is <b>not</b>
    /// re-emitted as a P1 chunk (no line is indexed twice).
    /// </summary>
    public bool IncludeDocumentationInOutlineChunk { get; init; } = true;

    /// <summary>The C2 filter configuration applied to every tier.</summary>
    public ChunkFilterOptions Filter { get; init; } = new();
}
