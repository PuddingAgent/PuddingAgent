namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// A pre-chunked document handed to the index verbatim (U4-1a: the outline-first chunking strategy).
/// <para>
/// The full-text index normally derives its documents from a directory walk (one document per source
/// line). A chunked corpus cannot be expressed that way: its blocks come from a code outline, so their
/// text exists in no directory. This type is the shape of such a block, and
/// <c>LuceneSearchEngine.BuildChunkIndexAsync</c> is the add-only entry point that stores it.
/// </para>
/// </summary>
/// <param name="Path">Source file the chunk came from (stored, and used by the search result shape).</param>
/// <param name="StartLine">First source line of the chunk (1-based).</param>
/// <param name="EndLine">Last source line of the chunk (1-based, inclusive).</param>
/// <param name="Text">The exact text to index (already filtered by the caller).</param>
/// <param name="Kind">Tier label kept for auditing (e.g. <c>Outline</c> / <c>DocComment</c> / <c>CodeText</c>).</param>
/// <param name="Boost">
/// Ranking weight for the <c>content</c> field, supplied by the caller from the chunk's priority. The
/// index deliberately does not invent a weight here — a "short line is probably a heading" heuristic
/// would silently compete with the priority the chunking component assigned.
/// </param>
public sealed record IndexChunkDocument(
    string Path,
    int StartLine,
    int EndLine,
    string Text,
    string Kind,
    float Boost = 1.0f);

/// <summary>Result of building an index from pre-chunked documents.</summary>
/// <param name="Success">False when the build failed or was skipped for lock contention.</param>
/// <param name="DocumentCount">Lucene documents actually written.</param>
/// <param name="SourceFileCount">Distinct source files those documents came from.</param>
/// <param name="TotalTextChars">Characters of indexed text (the payload size, before Lucene analysis).</param>
/// <param name="ElapsedMs">Wall time of the build as measured inside the engine.</param>
/// <param name="Error">Failure reason; <c>null</c> on success.</param>
public sealed record ChunkIndexResult(
    bool Success,
    int DocumentCount,
    int SourceFileCount,
    long TotalTextChars,
    long ElapsedMs,
    string? Error);
