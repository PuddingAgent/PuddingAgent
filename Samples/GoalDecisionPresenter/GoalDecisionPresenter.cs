using System.Collections.Immutable;
using Pudding.Api.Contracts;

namespace Pudding.Samples.GoalDecisionPresenter;

/// <summary>
/// Pure presentation function for the read-only goal decision snapshot. It only formats the frozen facts it
/// receives — it never writes a GoalPhase, never infers or suggests a next state, never parses model natural
/// language and never appends an "accepted/completed" conclusion. Unknown reason codes stay verbatim and an
/// absent next action becomes the fixed placeholder.
/// </summary>
public static class GoalDecisionPresenter
{
    public static string Format(GoalDecisionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var lines = new[]
        {
            $"Phase={snapshot.Phase}",
            "ReasonCode=" + FormatReasonCode(snapshot.ReasonCode),
            "UnmetCriteria=" + FormatUnmetCriteria(snapshot.UnmetCriteria),
            "NextAction=" + FormatNextAction(snapshot.NextAction),
            "EvidenceRefs=" + FormatEvidenceRefs(snapshot.EvidenceRefs)
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatReasonCode(string? reasonCode) =>
        string.IsNullOrWhiteSpace(reasonCode) ? "未提供" : reasonCode;

    private static string FormatNextAction(string? nextAction) =>
        string.IsNullOrWhiteSpace(nextAction) ? "未提供下一步" : nextAction;

    private static string FormatUnmetCriteria(ImmutableArray<GoalDecisionCriterion> unmetCriteria) =>
        unmetCriteria.IsDefaultOrEmpty
            ? "无"
            : string.Join("; ", unmetCriteria.Select(criterion => $"{criterion.CriterionId}:{criterion.Outcome}"));

    private static string FormatEvidenceRefs(ImmutableArray<string> evidenceRefs) =>
        evidenceRefs.IsDefaultOrEmpty ? "无" : string.Join(", ", evidenceRefs);
}
