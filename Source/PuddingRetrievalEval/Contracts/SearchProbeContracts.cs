namespace PuddingRetrievalEval.Contracts;

/// <summary>
/// Retrieval scope handed to a probe: a root directory plus an optional file-type filter.
/// This mirrors the user-visible scope contract (ADR-089 R3/R4) without binding to any engine.
/// </summary>
/// <param name="RootDirectory">Absolute directory the search is confined to.</param>
/// <param name="FileExtensions">
/// Optional extension whitelist (with or without the leading dot, e.g. <c>.cs</c> / <c>cs</c>).
/// <c>null</c> or empty means "no type filter".
/// </param>
public sealed record SearchScope(string RootDirectory, IReadOnlyList<string>? FileExtensions = null);

/// <summary>
/// A single probe call. <see cref="MaxResults"/> must be &gt;= the largest k the evaluation reports
/// (<c>10</c>), otherwise <c>recall@10</c> would be structurally unmeasurable.
/// </summary>
public sealed record SearchProbeRequest(string Query, SearchScope Scope, int MaxResults = 20);

/// <summary>
/// One retrieval hit. <see cref="Path"/> is the only mandatory field.
/// </summary>
/// <param name="Path">File path (absolute or relative; the metric layer normalises it).</param>
/// <param name="Symbol">Symbol reported for the hit, when the engine can report one. <c>null</c> means
/// "this engine does not report symbols" — such a hit is matched on path alone.</param>
/// <param name="Line">Optional line number of the hit.</param>
public sealed record SearchProbeHit(string Path, string? Symbol = null, int? Line = null);

/// <summary>
/// Result of one probe call. <see cref="Hits"/> is already ranked: index 0 is the engine's top hit.
/// <para>
/// <see cref="ElapsedMs"/> is measured <b>by the probe</b> (the evaluation layer never times an engine
/// it cannot see), so the number is comparable across engines only if every probe times the same way —
/// probes must time the full call, including index open/refresh.
/// </para>
/// <para>
/// Resolution is <b>fractional milliseconds</b>, deliberately: local indexed retrieval routinely
/// answers in well under 1 ms, and integer milliseconds would truncate the whole distribution to zero
/// — which would look like a broken instrument rather than a fast engine.
/// </para>
/// </summary>
public sealed record SearchProbeOutcome(
    IReadOnlyList<SearchProbeHit> Hits,
    double ElapsedMs,
    bool Success = true,
    string? Error = null)
{
    public static SearchProbeOutcome Failure(double elapsedMs, string error) =>
        new([], elapsedMs, false, error);
}

/// <summary>
/// The single port through which the evaluation observes a retrieval engine.
/// <para>
/// This is the key design decision of U4-0: the evaluation component depends on this port and on
/// nothing else, so it stays a leaf and swapping / adding a retrieval engine never touches the
/// evaluation. A probe must be read-only with respect to the corpus it searches.
/// </para>
/// </summary>
public interface ISearchProbe
{
    /// <summary>Stable display name used in reports (e.g. <c>lucene-fulltext</c>).</summary>
    string Name { get; }

    Task<SearchProbeOutcome> SearchAsync(
        SearchProbeRequest request,
        CancellationToken cancellationToken = default);
}
