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
/// The machine-checkable claim a probe observed for a call that produced no hits (ADR-089 §8.x).
/// <para>
/// Why it is a port-level type and not an engine type: the evaluation must be able to tell
/// "I searched these faces and found nothing" apart from "the semantic face was never wired"
/// without referencing any engine. Every field is therefore a plain string — an adapter maps the
/// engine's claim into this shape, and the engine type never crosses the component boundary
/// (this project stays a leaf: zero ProjectReference).
/// </para>
/// <para>
/// <see cref="Kind"/> is passed through verbatim (<c>None</c> / <c>NoCandidates</c> /
/// <c>NoMatchInScope</c>); <see cref="Unsearched"/> lists the faces known to exist but not run —
/// their mere presence is the reason a negative conclusion is not allowed.
/// </para>
/// </summary>
/// <param name="Kind">Negative type reported by the engine, verbatim.</param>
/// <param name="Searched">Faces the probe actually executed for this call.</param>
/// <param name="Unsearched">Faces known to exist but not executed (e.g. <c>vector</c>).</param>
/// <param name="NextStepKinds">Kinds of the next-step suggestions the engine returned.</param>
public sealed record SearchProbeClaim(
    string Kind,
    IReadOnlyList<string> Searched,
    IReadOnlyList<string> Unsearched,
    IReadOnlyList<string> NextStepKinds);

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
/// <para>
/// <see cref="Claim"/> is the optional machine-checkable negative claim (see
/// <see cref="SearchProbeClaim"/>). It is optional and appended last so every existing construction
/// site keeps compiling and keeps its meaning; a probe that cannot observe claims leaves it null
/// rather than inventing one.
/// </para>
/// </summary>
public sealed record SearchProbeOutcome(
    IReadOnlyList<SearchProbeHit> Hits,
    double ElapsedMs,
    bool Success = true,
    string? Error = null,
    SearchProbeClaim? Claim = null)
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
