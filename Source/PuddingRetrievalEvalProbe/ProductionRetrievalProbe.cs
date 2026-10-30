using System.Diagnostics;
using PuddingRetrieval.Contracts;
using PuddingRetrievalEval.Contracts;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// Real retrieval surface #2: the <b>production</b> structured retrieval port
/// (<see cref="IRetrievalPort"/>, ADR-089), driven verbatim — the same object <c>workspace_search</c>
/// calls in a live session.
/// <para>
/// <b>Why this probe exists.</b> Every "it works" claim about the production port (10-02 onward) came
/// from a human poking a tool and reading the answer. That is not evidence: it cannot be re-run
/// against the same frozen set and the same index bytes. This adapter turns the port itself into the
/// engine observed by <c>PuddingRetrievalEval</c>, which keeps the evaluation component a leaf
/// (it only sees <see cref="ISearchProbe"/>) while making the real thing measurable.
/// </para>
/// <para>
/// <b>Thin on purpose.</b> The adapter maps request → <see cref="RetrievalRequest"/> and
/// hit → <see cref="SearchProbeHit"/>; it adds no filtering, no re-ranking and no fallback. Anything
/// added here would be harness behaviour wearing engine clothes. The one non-obvious mapping is the
/// empty-result <see cref="SearchProbeClaim"/>: it is passed through <b>verbatim</b>
/// (<see cref="RetrievalClaim.Kind"/> + searched/unsearched surfaces + next-step kinds), because
/// "I searched these faces and found nothing" must stay distinguishable from "no candidates" once it
/// reaches the report.
/// </para>
/// <para>
/// <b>Read-only.</b> The probe never issues a write to the store; the whole production mode reads a
/// point-in-time snapshot (see <c>ProductionRun</c>).
/// </para>
/// </summary>
internal sealed class ProductionRetrievalProbe : ISearchProbe
{
    private readonly IRetrievalPort _port;
    private readonly string _scopeId;
    private readonly RetrievalIntent _intent;

    /// <summary>First claim seen per query — the report quotes what the run actually observed.</summary>
    private readonly Dictionary<string, SearchProbeClaim?> _firstClaim = new(StringComparer.Ordinal);

    /// <summary>Queries whose claim changed between repetitions (a determinism defect, surfaced not hidden).</summary>
    private readonly HashSet<string> _claimVaried = new(StringComparer.Ordinal);

    /// <summary>Zero-hit calls that arrived without a claim — a gap in the port, reported not papered over.</summary>
    private int _zeroHitWithoutClaim;

    public ProductionRetrievalProbe(
        IRetrievalPort port,
        string scopeId,
        RetrievalIntent intent = RetrievalIntent.Auto)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));

        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("scope id is required (production scope id is the workspace id)", nameof(scopeId));

        _scopeId = scopeId.Trim();
        _intent = intent;
    }

    /// <inheritdoc />
    public string Name => "production-" + _intent.ToString().ToLowerInvariant();

    /// <summary>
    /// Whether this build can execute the vector (semantic) face at all. Structural fact, not a
    /// measurement: the production port has no <c>IEmbeddingProvider</c> wiring until R5, so the probe
    /// must never let a report claim the face ran.
    /// </summary>
    public bool SupportsVectorFace => false;

    /// <summary>
    /// Claims observed during this run, keyed by query (first observation wins). Only non-null claims
    /// are returned: a query that produced hits has nothing to claim about its own emptiness.
    /// </summary>
    public IReadOnlyDictionary<string, SearchProbeClaim> ObservedClaims =>
        _firstClaim.Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);

    /// <summary>Queries whose claim was not stable across the repetitions of one run.</summary>
    public IReadOnlyCollection<string> ClaimVariationQueries => _claimVaried;

    /// <summary>Zero-hit calls that came back with a null claim (should stay 0; a non-zero value is a port gap).</summary>
    public int ZeroHitCallsWithoutClaim => _zeroHitWithoutClaim;

    /// <inheritdoc />
    public async Task<SearchProbeOutcome> SearchAsync(
        SearchProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();

        RetrievalRequest retrieval;
        try
        {
            retrieval = new RetrievalRequest(
                query: request.Query,
                scopeId: _scopeId,
                intent: _intent,
                pageSize: Math.Clamp(request.MaxResults, 1, RetrievalBudget.MaxPageSize));
        }
        catch (ArgumentException ex)
        {
            // A malformed request is a harness bug, not an engine miss: fail the call, do not return [].
            stopwatch.Stop();
            return SearchProbeOutcome.Failure(
                stopwatch.Elapsed.TotalMilliseconds,
                $"request rejected by the production contract: {ex.Message}");
        }

        try
        {
            var result = await _port.RetrieveAsync(retrieval, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var claim = MapClaim(result);
            Observe(request.Query, claim, hitCount: result.Hits.Count);

            var hits = result.Hits
                .Select(hit => new SearchProbeHit(hit.Evidence.FilePath, hit.SymbolName, hit.Evidence.Line))
                .ToArray();

            return new SearchProbeOutcome(hits, stopwatch.Elapsed.TotalMilliseconds, Claim: claim);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex)
        {
            // Never swallow an engine failure into "no hits": that is exactly the fake zero this
            // whole task exists to eliminate.
            stopwatch.Stop();
            return SearchProbeOutcome.Failure(
                stopwatch.Elapsed.TotalMilliseconds,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Maps the port's machine-checkable claim onto the port-agnostic contract. Returns <c>null</c>
    /// when the port gave no claim — the adapter must not invent one.
    /// </summary>
    private static SearchProbeClaim? MapClaim(RetrievalResult result)
    {
        var claim = result.Claim;
        if (claim is null)
            return null;

        return new SearchProbeClaim(
            Kind: claim.Kind.ToString(),
            Searched: claim.SearchedSurfaces.ToArray(),
            Unsearched: claim.UnsearchedSurfaces.ToArray(),
            NextStepKinds: result.NextSteps.Select(step => step.Kind.ToString()).ToArray());
    }

    private void Observe(string query, SearchProbeClaim? claim, int hitCount)
    {
        if (hitCount == 0 && claim is null)
            _zeroHitWithoutClaim++;

        if (!_firstClaim.TryGetValue(query, out var first))
        {
            _firstClaim[query] = claim;
            return;
        }

        if (!ClaimsEqual(first, claim))
            _claimVaried.Add(query);
    }

    private static bool ClaimsEqual(SearchProbeClaim? left, SearchProbeClaim? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is null || right is null)
            return false;

        return string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
            && SequenceEqual(left.Searched, right.Searched)
            && SequenceEqual(left.Unsearched, right.Unsearched)
            && SequenceEqual(left.NextStepKinds, right.NextStepKinds);
    }

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
