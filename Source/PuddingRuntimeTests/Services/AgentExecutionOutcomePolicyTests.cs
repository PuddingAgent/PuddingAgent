using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentExecutionOutcomePolicyTests
{
    private const string CompleteReportContainingFailureState = """
        SUMMARY:
        The delegated exploration completed and produced a concrete result for the parent Agent.
        CHANGES:
        none because the exploration was read-only.
        EVIDENCE:
        The streaming projection state machine advances through Starting, Active, Completed, and Failed states.
        RISKS:
        Runtime behavior can change when the projection implementation is modified.
        BLOCKERS:
        none because every required source artifact was available.
        """;

    [TestMethod]
    public void CompleteCanonicalReport_IsNotDowngradedByRecoverableToolFailure()
    {
        var shouldDowngrade = AgentExecutionOutcomePolicy.ShouldDowngradeSuccessfulExecution(
            currentlySuccessful: true,
            toolFailureCount: 1,
            finalReply: CompleteReportContainingFailureState,
            expectedOutputContract: CanonicalWorkReport.ExpectedOutputContract);

        Assert.IsFalse(shouldDowngrade);
    }

    [TestMethod]
    public void ExplicitFailureReply_StillDowngradesWhenNoCanonicalReportExists()
    {
        var shouldDowngrade = AgentExecutionOutcomePolicy.ShouldDowngradeSuccessfulExecution(
            currentlySuccessful: true,
            toolFailureCount: 1,
            finalReply: "Execution failed because the requested file could not be read.",
            expectedOutputContract: null);

        Assert.IsTrue(shouldDowngrade);
    }

    [TestMethod]
    public void TruncatedProviderOutput_AllowsExactlyOneBoundedRecoveryRound()
    {
        Assert.IsTrue(AgentOutputTruncationPolicy.IsTruncated("length"));
        Assert.IsTrue(AgentOutputTruncationPolicy.IsTruncated("incomplete"));
        Assert.IsTrue(AgentOutputTruncationPolicy.ShouldRetry(0, round: 0, maxRounds: 25));
        Assert.IsFalse(AgentOutputTruncationPolicy.ShouldRetry(1, round: 1, maxRounds: 25));
        Assert.IsFalse(AgentOutputTruncationPolicy.ShouldRetry(0, round: 0, maxRounds: 1));
    }

    [TestMethod]
    public void TruncationRecoveryPrompt_RequiresImmediateActionWithoutReasoningReplay()
    {
        var prompt = AgentOutputTruncationPolicy.RecoveryPrompt(hadDisplayableContent: false);

        StringAssert.Contains(prompt, "single best next tool");
        StringAssert.Contains(prompt, "Do not continue or restate");
    }
}
