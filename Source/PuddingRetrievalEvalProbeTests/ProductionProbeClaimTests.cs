using PuddingRetrieval.Contracts;
using PuddingRetrievalEval.Contracts;
using PuddingRetrievalEvalProbe;

namespace PuddingRetrievalEvalProbeTests;

/// <summary>
/// R0-02 gate 1 — <b>claim 透传</b>: a zero-hit call must reach the evaluation with the port's
/// machine-checkable claim intact.
/// <para>
/// Why it matters: the acceptance line says a lexical-only miss must never be reported as "it does not
/// exist". That property lives or dies in this adapter — if it drops <see cref="SearchProbeOutcome.Claim"/>
/// (or invents one), every downstream report silently loses the ability to tell the two apart, and no
/// metric would notice.
/// </para>
/// <para>
/// Non-vacuity: the canned port returns a real <see cref="RetrievalClaim"/> derived by the production
/// policy (<see cref="RetrievalClaim.ClassifyForEmpty"/>), so removing the mapping from the adapter
/// makes the assertions fail rather than pass trivially.
/// </para>
/// </summary>
[TestClass]
public sealed class ProductionProbeClaimTests
{
    /// <summary>An Auto, non-anchored zero-hit query — the exact shape of the concept counter-examples.</summary>
    private const string ConceptQuery = "签发符号引用的令牌";

    [TestMethod]
    public async Task ZeroHitOutcomeCarriesThePortsClaimVerbatim()
    {
        var probe = new ProductionRetrievalProbe(new CannedEmptyPort(), "default");

        var outcome = await probe.SearchAsync(new SearchProbeRequest(
            ConceptQuery,
            new SearchScope(Path.GetTempPath())));

        Assert.IsTrue(outcome.Success, $"the probe must not fail a normal empty answer: {outcome.Error}");
        Assert.AreEqual(0, outcome.Hits.Count);
        Assert.IsNotNull(
            outcome.Claim,
            "a zero-hit call reached the evaluation without a claim — a lexical-only miss would then read as 'does not exist'.");

        var claim = outcome.Claim!;
        Assert.AreEqual(RetrievalClaimKind.None.ToString(), claim.Kind);
        CollectionAssert.Contains(claim.Searched.ToList(), RetrievalClaim.SurfaceLexical);
        CollectionAssert.Contains(
            claim.Unsearched.ToList(),
            RetrievalClaim.SurfaceVector,
            "the unsearched face list is the reason a negative conclusion is forbidden; it must survive the adapter.");
        CollectionAssert.Contains(claim.NextStepKinds.ToList(), RetrievalNextStepKind.UseAlternativeFace.ToString());
    }

    [TestMethod]
    public async Task ObservedClaimIsRetainedForTheReport()
    {
        var probe = new ProductionRetrievalProbe(new CannedEmptyPort(), "default");

        await probe.SearchAsync(new SearchProbeRequest(ConceptQuery, new SearchScope(Path.GetTempPath())));

        Assert.AreEqual(1, probe.ObservedClaims.Count);
        Assert.IsTrue(probe.ObservedClaims.TryGetValue(ConceptQuery, out var recorded));
        Assert.AreEqual(RetrievalClaimKind.None.ToString(), recorded!.Kind);
        Assert.AreEqual(0, probe.ZeroHitCallsWithoutClaim);
    }

    [TestMethod]
    public async Task EngineFailureIsNotTurnedIntoAnEmptyAnswer()
    {
        var probe = new ProductionRetrievalProbe(new ThrowingPort(), "default");

        var outcome = await probe.SearchAsync(new SearchProbeRequest(ConceptQuery, new SearchScope(Path.GetTempPath())));

        Assert.IsFalse(outcome.Success, "an engine exception must surface as a failed call, never as 'no hits'.");
        Assert.IsNull(outcome.Claim);
        StringAssert.Contains(outcome.Error, nameof(InvalidOperationException));
    }

    /// <summary>Port double: answers "nothing, and here is what that means" exactly like the production port.</summary>
    private sealed class CannedEmptyPort : IRetrievalPort
    {
        public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(RetrievalIntent.Auto, request.Intent, "the adapter must not smuggle in an explicit intent.");

            return Task.FromResult(RetrievalResult.Empty(
                request,
                RetrievalEmptyReason.GenuinelyAbsent,
                RetrievalNextStep.UseAlternativeFace(
                    "词面面没有匹配；语义面未接入，换用精确符号名或路径再查。",
                    "use an exact symbol name or a file path"),
                "canned-index-v1",
                claim: RetrievalClaim.ClassifyForEmpty(request, RetrievalEmptyReason.GenuinelyAbsent)));
        }
    }

    /// <summary>Port double: the engine blew up. The adapter must not report that as an empty result.</summary>
    private sealed class ThrowingPort : IRetrievalPort
    {
        public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("database is locked");
    }
}
