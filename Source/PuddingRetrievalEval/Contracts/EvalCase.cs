namespace PuddingRetrievalEval.Contracts;

/// <summary>
/// Kind of an annotated query. <see cref="Unknown"/> is the zero value and is never produced by
/// the loader: an unrecognised kind string is a fail-closed format error.
/// </summary>
public enum EvalKind
{
    Unknown = 0,

    /// <summary>Exact symbol/identifier lookup, e.g. <c>RetrievalMatcher</c>.</summary>
    Symbol = 1,

    /// <summary>Natural-language intent, e.g. "where is the tool approval reviewer chosen".</summary>
    Intent = 2,

    /// <summary>Cross-file reference: which files mention / use a given symbol.</summary>
    Crossref = 3,
}

/// <summary>
/// Language stratum of an annotated query. <see cref="Unknown"/> is the zero value and is never
/// produced by the loader. The U4-0 seed set must cover every stratum with at least 15 cases.
/// </summary>
public enum EvalLanguage
{
    Unknown = 0,
    CSharp = 1,

    /// <summary>Covers both <c>.ts</c> and <c>.tsx</c> (reported as one stratum).</summary>
    TypeScript = 2,
    Markdown = 3,
}

/// <summary>
/// One annotated query: a query string plus the file paths that <b>must</b> be found for it.
/// </summary>
/// <param name="Query">The query text handed to the probe verbatim.</param>
/// <param name="ExpectedHits">
/// Expected results. Each entry is a repository-relative or absolute file path, optionally suffixed
/// with <c>#SymbolName</c> (e.g. <c>Source/PuddingCore/Foo.cs#Foo</c>). Duplicates (after
/// normalisation) are rejected by the loader; the metric layer de-duplicates defensively as well.
/// </param>
/// <param name="Kind">Annotation class (symbol / intent / crossref).</param>
/// <param name="Language">Language stratum this case belongs to.</param>
public sealed record EvalCase(
    string Query,
    IReadOnlyList<string> ExpectedHits,
    EvalKind Kind,
    EvalLanguage Language)
{
    public override string ToString() => $"[{Kind}/{Language}] {Query}";
}

/// <summary>An annotated query set, loaded from a version-controlled data file.</summary>
/// <param name="Name">Set name (e.g. <c>seed-v1</c>).</param>
/// <param name="Version">Set schema/annotation version, &gt;= 1.</param>
/// <param name="Cases">Cases in file order (the report keeps that order for stable diffs).</param>
public sealed record EvalSet(string Name, int Version, IReadOnlyList<EvalCase> Cases);
