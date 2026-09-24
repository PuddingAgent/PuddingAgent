namespace PuddingIndexChunking;

/// <summary>
/// Language stratum of a source file.
/// <para>
/// <see cref="Unknown"/> is the zero value and means "no language-specific rule applies": the
/// assembler then emits no outline chunks (there is no language profile to derive them from) and the
/// filter falls back to the language-neutral thresholds. Callers are expected to declare the
/// language they actually know rather than relying on this value.
/// </para>
/// </summary>
public enum SourceLanguage
{
    Unknown = 0,
    CSharp = 1,
    TypeScript = 2,
    Python = 3,
    Markdown = 4,
    PlainText = 5,
}

/// <summary>
/// Index tier of a chunk (ADR-089 C1). Values are ordered by <b>descending</b> retrieval priority:
/// <see cref="Outline"/> is P0, <see cref="DocComment"/> P1, <see cref="CodeText"/> P2.
/// <para>
/// There is deliberately no <c>Unknown</c> member. A chunk kind is never parsed from an external
/// literal — the assembler derives it from a local fact (outline symbol span / comment span / code
/// span) — so an unrecognised value cannot arise, and extending this enum is a compile-time decision
/// rather than a fail-closed runtime one. Contrast a kind that <i>is</i> decoded from user data,
/// where an <c>Unknown = 0</c> member is mandatory.
/// </para>
/// </summary>
public enum ChunkKind
{
    Outline = 0,
    DocComment = 1,
    CodeText = 2,
}

/// <summary>Priority and retrieval-side weight of a <see cref="ChunkKind"/> (ADR-089 C1).</summary>
public static class ChunkPriorities
{
    /// <summary>Highest priority: structured code outline (symbols, signatures, visibility, doc text).</summary>
    public const int P0 = 0;

    /// <summary>Second: documentation / comments.</summary>
    public const int P1 = 1;

    /// <summary>Lowest: raw code text, indexed only as a fallback and always filtered.</summary>
    public const int P2 = 2;

    /// <summary>Maps a chunk kind to its P0/P1/P2 priority.</summary>
    public static int Of(ChunkKind kind) => kind switch
    {
        ChunkKind.Outline => P0,
        ChunkKind.DocComment => P1,
        ChunkKind.CodeText => P2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unmapped chunk kind"),
    };

    /// <summary>
    /// Suggested index-side field boost for a tier. This is <b>a value the index layer consumes</b>,
    /// not a ranking decision made here: the chunking component owns "which tier this text belongs
    /// to", the retrieval side owns "how much that tier is worth". Keeping the number next to the
    /// priority mapping is what makes the weighting auditable.
    /// </summary>
    public static float BoostOf(ChunkKind kind) => kind switch
    {
        ChunkKind.Outline => 2.0f,
        ChunkKind.DocComment => 1.5f,
        ChunkKind.CodeText => 1.0f,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unmapped chunk kind"),
    };
}

/// <summary>
/// One chunk that an index may store as a single document: the text to index plus the provenance a
/// report needs (which file, which lines, which tier).
/// <para>Construction validates, because a chunk with no text or an impossible line range would
/// silently poison both the index and the line-coverage invariant.</para>
/// </summary>
public sealed record IndexChunk
{
    public IndexChunk(ChunkKind kind, string text, string sourceFile, int startLine, int endLine)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("chunk text must not be blank", nameof(text));

        if (string.IsNullOrWhiteSpace(sourceFile))
            throw new ArgumentException("chunk source file must not be blank", nameof(sourceFile));

        if (startLine < 1)
            throw new ArgumentOutOfRangeException(nameof(startLine), startLine, "line numbers are 1-based");

        if (endLine < startLine)
            throw new ArgumentOutOfRangeException(nameof(endLine), endLine, "end line must not precede start line");

        Kind = kind;
        Text = text;
        SourceFile = sourceFile;
        StartLine = startLine;
        EndLine = endLine;
    }

    /// <summary>Which tier this chunk belongs to.</summary>
    public ChunkKind Kind { get; }

    /// <summary>The exact text handed to the index (already filtered when a filter is configured).</summary>
    public string Text { get; }

    /// <summary>Absolute or relative path of the file the chunk came from.</summary>
    public string SourceFile { get; }

    /// <summary>First source line covered by this chunk (1-based, inclusive).</summary>
    public int StartLine { get; }

    /// <summary>Last source line covered by this chunk (1-based, inclusive).</summary>
    public int EndLine { get; }

    /// <summary>P0/P1/P2 as defined by ADR-089 C1.</summary>
    public int Priority => ChunkPriorities.Of(Kind);

    /// <summary>Number of source lines covered.</summary>
    public int LineCount => EndLine - StartLine + 1;
}

/// <summary>
/// Kind of an outline symbol. The chunking component keeps its own enum on purpose: reusing the code
/// intelligence types would create a dependency from this leaf back to the language-parsing layer,
/// which is exactly the direction the boundary forbids.
/// </summary>
public enum OutlineSymbolKind
{
    Unknown = 0,
    Namespace = 1,
    Type = 2,
    Method = 3,
    Property = 4,
    Field = 5,
    Event = 6,
    Constructor = 7,
    Indexer = 8,
    Delegate = 9,
    EnumMember = 10,
}

/// <summary>
/// One symbol of a file's outline, as supplied by an <see cref="IOutlineSource"/> adapter.
/// </summary>
/// <param name="Name">Simple name (e.g. <c>CodeIndexMaintenanceService</c>).</param>
/// <param name="Kind">Symbol kind; drives the wording of the outline chunk text.</param>
/// <param name="StartLine">First line of the declaration (1-based).</param>
/// <param name="EndLine">Last line of the symbol (1-based, inclusive).</param>
/// <param name="Signature">Parameters / return type / base list, already formatted by the adapter.</param>
/// <param name="Modifiers">Visibility and other modifiers (e.g. <c>public static</c>).</param>
/// <param name="Container">Enclosing type or namespace name, when the adapter knows it.</param>
/// <param name="Documentation">Documentation summary text, when the adapter extracted one.</param>
public sealed record OutlineSymbol(
    string Name,
    OutlineSymbolKind Kind,
    int StartLine,
    int EndLine,
    string? Signature = null,
    string? Modifiers = null,
    string? Container = null,
    string? Documentation = null);

/// <summary>
/// Outline of one file. <see cref="Error"/> is non-null when the adapter could not produce an
/// outline (unsupported language, parse failure). An error is <b>data</b>, not an exception: the
/// assembler then falls back to the language-neutral tiers instead of failing the whole file.
/// </summary>
public sealed record OutlineSymbolSet(string FilePath, IReadOnlyList<OutlineSymbol> Symbols, string? Error = null)
{
    /// <summary>An outline with no symbols and no error (e.g. an empty file).</summary>
    public static OutlineSymbolSet Empty(string filePath) => new(filePath, []);

    /// <summary>True when the set carries neither symbols nor an error.</summary>
    public bool IsEmpty => Symbols.Count == 0;
}

/// <summary>
/// The single port through which the chunking component obtains a code outline.
/// <para>
/// This is the component's key design decision: outline extraction needs Roslyn / the language
/// outliners, which live above this component. Depending on them would make the leaf testable only
/// with the heavy parser present. Instead an <b>external adapter</b> (production wiring, or the
/// retrieval-evaluation probe) implements this port, so the component stays a leaf and its tests can
/// feed a stand-in outline — while the expensive truth stays available where it belongs.
/// </para>
/// </summary>
public interface IOutlineSource
{
    /// <summary>
    /// Returns the outline of one file. Implementations must be side-effect free with respect to the
    /// file (they receive its text) and must not throw for "no symbols" — see
    /// <see cref="OutlineSymbolSet.Error"/>.
    /// </summary>
    Task<OutlineSymbolSet> GetOutlineAsync(
        string filePath,
        string sourceText,
        SourceLanguage language,
        CancellationToken cancellationToken = default);
}

/// <summary>The result of assembling one file: its chunks plus the facts a report/assertion needs.</summary>
/// <param name="OutlineError">
/// Non-null when the <see cref="IOutlineSource"/> adapter reported a failure for this file. It is
/// carried out of the assembler rather than swallowed, so a corpus build can report "N files had no
/// outline because of X" instead of silently degrading to the language-neutral tiers.
/// </param>
public sealed record ChunkAssembly(
    string FilePath,
    SourceLanguage Language,
    int LineCount,
    int OutlineSymbolCount,
    IReadOnlyList<IndexChunk> Chunks,
    string? OutlineError = null)
{
    /// <summary>Number of chunks of one tier.</summary>
    public int CountOf(ChunkKind kind) => Chunks.Count(chunk => chunk.Kind == kind);

    /// <summary>Total characters of chunk text (the payload an index would actually store).</summary>
    public int TotalTextChars => Chunks.Sum(chunk => chunk.Text.Length);
}
