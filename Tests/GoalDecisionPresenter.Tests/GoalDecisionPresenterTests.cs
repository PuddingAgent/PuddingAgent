using System.Collections.Immutable;
using Pudding.Api.Contracts;
using Presenter = Pudding.Samples.GoalDecisionPresenter.GoalDecisionPresenter;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace GoalDecisionPresenter.Tests;

[TestClass]
public sealed class GoalDecisionPresenterTests
{
    private static readonly DateTimeOffset DecidedAt = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Format_CompletedWithEvidence_MatchesGolden()
    {
        var snapshot = Snapshot(
            decisionId: "decision-1",
            phase: "Completed",
            reasonCode: "goal.completed",
            unmetCriteria: ImmutableArray<GoalDecisionCriterion>.Empty,
            nextAction: null,
            evidenceRefs: ImmutableArray.Create("check://build/exit-0", "review://review-7"));

        var output = Presenter.Format(snapshot);

        Assert.AreEqual(
            Lines(
                "Phase=Completed",
                "ReasonCode=goal.completed",
                "UnmetCriteria=无",
                "NextAction=未提供下一步",
                "EvidenceRefs=check://build/exit-0, review://review-7"),
            output);
        AssertFiveLineOutput(output);
        Assert.IsFalse(output.Contains("已验收", StringComparison.Ordinal),
            "The presenter must not append an acceptance conclusion.");
    }

    [TestMethod]
    public void Format_BlockedWithUnmetCriteria_MatchesGolden()
    {
        var snapshot = Snapshot(
            decisionId: "decision-2",
            phase: "Blocked",
            reasonCode: "goal.review_needs_user",
            unmetCriteria: ImmutableArray.Create(
                new GoalDecisionCriterion("criterion-1", "NotSupported", ImmutableArray.Create("review://review-9")),
                new GoalDecisionCriterion("criterion-2", "Unknown", ImmutableArray<string>.Empty)),
            nextAction: "await-user-decision",
            evidenceRefs: ImmutableArray.Create("review://review-9"));

        var output = Presenter.Format(snapshot);

        Assert.AreEqual(
            Lines(
                "Phase=Blocked",
                "ReasonCode=goal.review_needs_user",
                "UnmetCriteria=criterion-1:NotSupported; criterion-2:Unknown",
                "NextAction=await-user-decision",
                "EvidenceRefs=review://review-9"),
            output);
        AssertFiveLineOutput(output);
    }

    [TestMethod]
    public void Format_BudgetExhaustedEmpty_MatchesGolden()
    {
        var snapshot = Snapshot(
            decisionId: "decision-3",
            phase: "BudgetExhausted",
            reasonCode: "goal.budget_exhausted",
            unmetCriteria: ImmutableArray<GoalDecisionCriterion>.Empty,
            nextAction: "   ",
            evidenceRefs: ImmutableArray<string>.Empty);

        var output = Presenter.Format(snapshot);

        Assert.AreEqual(
            Lines(
                "Phase=BudgetExhausted",
                "ReasonCode=goal.budget_exhausted",
                "UnmetCriteria=无",
                "NextAction=未提供下一步",
                "EvidenceRefs=无"),
            output);
        AssertFiveLineOutput(output);
    }

    [TestMethod]
    public void Format_UnknownReasonCode_IsPreservedVerbatim()
    {
        var snapshot = Snapshot(
            phase: "Blocked",
            reasonCode: "goal.custom_unknown.reason",
            unmetCriteria: ImmutableArray<GoalDecisionCriterion>.Empty,
            nextAction: null,
            evidenceRefs: ImmutableArray<string>.Empty);

        var output = Presenter.Format(snapshot);

        Assert.AreEqual(
            Lines(
                "Phase=Blocked",
                "ReasonCode=goal.custom_unknown.reason",
                "UnmetCriteria=无",
                "NextAction=未提供下一步",
                "EvidenceRefs=无"),
            output);
        AssertFiveLineOutput(output);
        Assert.IsFalse(output.Contains("已验收", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Format_BlankReasonCode_WritesNotProvided()
    {
        var snapshot = Snapshot(phase: "Failed", reasonCode: "   ", nextAction: "noop");

        var output = Presenter.Format(snapshot);

        Assert.AreEqual("ReasonCode=未提供", output.Split(Environment.NewLine, StringSplitOptions.None)[1]);
    }

    [TestMethod]
    public void Format_EmptyUnmetCriteriaAndEvidenceRefs_WritesNone()
    {
        var snapshot = Snapshot(
            phase: "Active",
            reasonCode: "goal.running",
            unmetCriteria: ImmutableArray<GoalDecisionCriterion>.Empty,
            nextAction: "continue",
            evidenceRefs: ImmutableArray<string>.Empty);

        var output = Presenter.Format(snapshot);

        Assert.AreEqual(
            Lines(
                "Phase=Active",
                "ReasonCode=goal.running",
                "UnmetCriteria=无",
                "NextAction=continue",
                "EvidenceRefs=无"),
            output);
        AssertFiveLineOutput(output);
    }

    [TestMethod]
    public void Format_ChineseAndEscapedCharacters_ArePreservedVerbatim()
    {
        const string reasonCode = "goal.自定义.Reason-中文";
        const string nextAction = "等待用户：\"确认\"";
        const string escapedEvidenceRef = "check://路径\\一";
        var snapshot = Snapshot(
            phase: "Blocked",
            reasonCode: reasonCode,
            unmetCriteria: ImmutableArray.Create(
                new GoalDecisionCriterion("条件-甲", "未支持", ImmutableArray.Create("证据://一"))),
            nextAction: nextAction,
            evidenceRefs: ImmutableArray.Create("证据://构建/一", escapedEvidenceRef));

        var output = Presenter.Format(snapshot);

        Assert.AreEqual(
            Lines(
                "Phase=Blocked",
                $"ReasonCode={reasonCode}",
                "UnmetCriteria=条件-甲:未支持",
                $"NextAction={nextAction}",
                $"EvidenceRefs=证据://构建/一, {escapedEvidenceRef}"),
            output);
        AssertFiveLineOutput(output);
        Assert.IsTrue(output.Contains("\\", StringComparison.Ordinal), "The escaped character must stay verbatim.");
    }

    [TestMethod]
    public void Format_DoesNotModifyInput()
    {
        var criterion = new GoalDecisionCriterion("criterion-1", "NotSupported",
            ImmutableArray.Create("review://review-9"));
        var criteria = ImmutableArray.Create(criterion);
        var evidenceRefs = ImmutableArray.Create("review://review-9");
        var snapshot = Snapshot(
            decisionId: "decision-2",
            phase: "Blocked",
            reasonCode: "goal.review_needs_user",
            unmetCriteria: criteria,
            nextAction: "await-user-decision",
            evidenceRefs: evidenceRefs);

        Presenter.Format(snapshot);

        Assert.AreEqual("decision-2", snapshot.DecisionId);
        Assert.AreEqual("Blocked", snapshot.Phase);
        Assert.AreEqual("goal.review_needs_user", snapshot.ReasonCode);
        Assert.HasCount(1, snapshot.UnmetCriteria);
        Assert.AreSame(criterion, snapshot.UnmetCriteria[0]);
        Assert.AreEqual("criterion-1", snapshot.UnmetCriteria[0].CriterionId);
        Assert.AreEqual("NotSupported", snapshot.UnmetCriteria[0].Outcome);
        Assert.AreEqual("review://review-9", snapshot.UnmetCriteria[0].EvidenceRefs[0]);
        Assert.HasCount(1, snapshot.EvidenceRefs);
        Assert.AreEqual("review://review-9", snapshot.EvidenceRefs[0]);
        Assert.AreEqual("await-user-decision", snapshot.NextAction);
        Assert.AreEqual("objective-v1", snapshot.ObjectiveVersion);
        Assert.AreEqual("acceptance-v1", snapshot.AcceptanceContractVersion);
        Assert.AreEqual("budget-root-1", snapshot.RootBudgetId);
        Assert.AreEqual(12L, snapshot.BudgetRevision);
        Assert.AreEqual(3L, snapshot.ActivationEpoch);
        Assert.AreEqual(2, snapshot.IterationNo);
        Assert.AreEqual(DecidedAt, snapshot.DecidedAt);
        Assert.IsTrue(criteria.Equals(snapshot.UnmetCriteria), "The unmet-criteria array must be unchanged.");
        Assert.IsTrue(evidenceRefs.Equals(snapshot.EvidenceRefs), "The evidence-ref array must be unchanged.");
    }

    [TestMethod]
    public void Format_NullSnapshot_ThrowsArgumentNullException()
    {
        var exception = Assert.ThrowsExactly<ArgumentNullException>(() => Presenter.Format(null!));
        Assert.AreEqual("snapshot", exception.ParamName);
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);

    private static void AssertFiveLineOutput(string output)
    {
        var lines = output.Split(Environment.NewLine, StringSplitOptions.None);
        Assert.HasCount(5, lines, "Output must be exactly five lines.");
        Assert.IsFalse(output.StartsWith(Environment.NewLine, StringComparison.Ordinal), "No leading blank line.");
        Assert.IsFalse(output.EndsWith(Environment.NewLine, StringComparison.Ordinal), "No trailing blank line.");
    }

    private static GoalDecisionSnapshot Snapshot(
        string decisionId = "decision-1",
        string phase = "Completed",
        string reasonCode = "goal.completed",
        ImmutableArray<GoalDecisionCriterion>? unmetCriteria = null,
        string? nextAction = null,
        ImmutableArray<string>? evidenceRefs = null) =>
        new(
            decisionId,
            phase,
            reasonCode,
            unmetCriteria ?? ImmutableArray<GoalDecisionCriterion>.Empty,
            nextAction,
            evidenceRefs ?? ImmutableArray<string>.Empty,
            "objective-v1",
            "acceptance-v1",
            "budget-root-1",
            12,
            3,
            2,
            DecidedAt);
}
