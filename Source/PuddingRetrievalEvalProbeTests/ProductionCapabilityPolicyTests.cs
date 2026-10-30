using PuddingCodeIndex.Contracts.Retrieval;
using PuddingRetrievalEval.Contracts;
using PuddingRetrievalEvalProbe;

namespace PuddingRetrievalEvalProbeTests;

/// <summary>
/// R0-02 gate 2 — <b>未执行不得伪造</b>: a face that did not run must never be reported as
/// <c>executed</c>.
/// <para>
/// Why this is the dangerous one: a report that says "vector: executed" without any embedding route
/// wired is exactly how a fake vector number gets into a baseline, and every later comparison inherits
/// it. The verdict is therefore computed by <see cref="ProductionCapabilityPolicy"/> from observed
/// facts (successful calls, the claims the port produced, whether an anchored relations request really
/// ran, whether the build can drive the semantic face) — not written next to the JSON writer.
/// </para>
/// </summary>
[TestClass]
public sealed class ProductionCapabilityPolicyTests
{
    private static SearchProbeClaim NoneClaim() => new(
        RetrievalClaimKind.None.ToString(),
        [RetrievalClaim.SurfaceLexical],
        [RetrievalClaim.SurfaceVector],
        [RetrievalNextStepKind.UseAlternativeFace.ToString()]);

    [TestMethod]
    public void VectorFaceThatDidNotRunIsNeverReportedExecuted()
    {
        var capabilities = ProductionCapabilityPolicy.Build(new ProbeCapabilityEvidence(
            SuccessfulCalls: 4,
            Claims: [NoneClaim()],
            RelationsFaceExecuted: false,
            VectorFaceAvailable: false));

        Assert.AreNotEqual(ProductionCapabilityPolicy.Executed, capabilities.Vector.State);
        Assert.AreEqual(ProductionCapabilityPolicy.NotExecuted, capabilities.Vector.State);
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(capabilities.Vector.Reason),
            "a not-executed face must say why — an unexplained gap is not evidence.");
    }

    [TestMethod]
    public void VectorFaceStaysNotExecutedEvenWhenTheBuildCouldDriveIt()
    {
        // Available but not driven: still not "executed". Availability is not execution.
        var capabilities = ProductionCapabilityPolicy.Build(new ProbeCapabilityEvidence(
            SuccessfulCalls: 4,
            Claims: [NoneClaim()],
            RelationsFaceExecuted: false,
            VectorFaceAvailable: true));

        Assert.AreNotEqual(ProductionCapabilityPolicy.Executed, capabilities.Vector.State);
        Assert.AreEqual(ProductionCapabilityPolicy.NotExecuted, capabilities.Vector.State);
    }

    [TestMethod]
    public void RelationsFaceIsOnlyExecutedWhenAnAnchoredRequestReallyRan()
    {
        var withoutAnchorProbe = ProductionCapabilityPolicy.Build(new ProbeCapabilityEvidence(
            SuccessfulCalls: 4,
            Claims: [NoneClaim()],
            RelationsFaceExecuted: false,
            VectorFaceAvailable: false));

        Assert.AreEqual(ProductionCapabilityPolicy.NotExecuted, withoutAnchorProbe.Relations.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(withoutAnchorProbe.Relations.Reason));

        var withAnchorProbe = ProductionCapabilityPolicy.Build(new ProbeCapabilityEvidence(
            SuccessfulCalls: 4,
            Claims: [NoneClaim()],
            RelationsFaceExecuted: true,
            VectorFaceAvailable: false));

        Assert.AreEqual(ProductionCapabilityPolicy.Executed, withAnchorProbe.Relations.State);
        Assert.IsNull(withAnchorProbe.Relations.Reason);
    }

    [TestMethod]
    public void LexicalFaceNeedsProofOfExecution()
    {
        var nothingRan = ProductionCapabilityPolicy.Build(new ProbeCapabilityEvidence(
            SuccessfulCalls: 0,
            Claims: [],
            RelationsFaceExecuted: false,
            VectorFaceAvailable: false));

        Assert.AreEqual(ProductionCapabilityPolicy.NotExecuted, nothingRan.Lexical.State);

        var ranAndSaidSo = ProductionCapabilityPolicy.Build(new ProbeCapabilityEvidence(
            SuccessfulCalls: 2,
            Claims: [NoneClaim()],
            RelationsFaceExecuted: false,
            VectorFaceAvailable: false));

        Assert.AreEqual(ProductionCapabilityPolicy.Executed, ranAndSaidSo.Lexical.State);

        // A claim that omits the lexical surface is evidence of a gap, not licence to write "executed".
        var claimWithoutLexical = new SearchProbeClaim(
            RetrievalClaimKind.None.ToString(),
            [RetrievalClaim.SurfaceRelations],
            [RetrievalClaim.SurfaceVector],
            []);

        var contradictory = ProductionCapabilityPolicy.Build(new ProbeCapabilityEvidence(
            SuccessfulCalls: 2,
            Claims: [claimWithoutLexical],
            RelationsFaceExecuted: false,
            VectorFaceAvailable: false));

        Assert.AreEqual(ProductionCapabilityPolicy.NotExecuted, contradictory.Lexical.State);
    }
}
