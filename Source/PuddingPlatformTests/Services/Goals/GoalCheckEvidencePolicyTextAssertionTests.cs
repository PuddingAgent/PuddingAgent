using PuddingCode.Goals;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// G92-1 S1-c（片 4）：EvidencePolicy 的 text-assertion 放行与证据要求。
/// 锁定：合规 assistant-output 证据（前缀正确且 turnId 与 context 来源一致）⇒ 放行为可信证据；
/// 缺 assistant-output: 前缀 / turnId 不一致（含前缀混淆与缺 @sequence 段）/
/// FinalAssistantReply 不可得（未提供参考 turnId）⇒ failed(evidence_missing)；
/// 未知 kind 仍落 unsupported_check_kind（fail-closed 回归锁）。
/// text-assertion 非 executed kind：不要求 InvocationId/ReportRef/ExitCode。
/// </summary>
[TestClass]
public sealed class GoalCheckEvidencePolicyTextAssertionTests
{
    private const string TurnId = "turn-1";

    /// <summary>检查规格：定义与 hash 都来自注册表真实值（非自造），与片 2/3 用例同构。</summary>
    private static GoalCheckSpec TextSpec(string checkId, string fingerprint = "fp-1") => new()
    {
        CheckId = checkId,
        CriterionId = "criterion-1",
        CriterionRevision = 1,
        Kind = GoalVerificationSpecKinds.TextAssertion,
        DefinitionRef = GoalCheckDefinitionRegistry.TextAssertionRef,
        DefinitionHash = GoalCheckDefinitionRegistry.TryGetDefinitionHash(
            GoalCheckDefinitionRegistry.TextAssertionRef, out var hash) ? hash : string.Empty,
        InputFingerprint = fingerprint,
        ExecutorRole = "core",
        ExpectedText = "READY",
    };

    /// <summary>已通过报告：身份/版本/新鲜度字段与 spec 一致；不携带 InvocationId/ReportRef/ExitCode。</summary>
    private static GoalCheckReport PassedReport(string checkId, string[] evidenceRefs) => new()
    {
        CheckId = checkId,
        CriterionId = "criterion-1",
        CriterionRevision = 1,
        Status = GoalCriterionResultStatuses.Passed,
        EvidenceRefs = evidenceRefs,
        InputFingerprint = "fp-1",
        DefinitionHash = GoalCheckDefinitionRegistry.TryGetDefinitionHash(
            GoalCheckDefinitionRegistry.TextAssertionRef, out var hash) ? hash : string.Empty,
        RunnerId = "goal-check-runner",
    };

    [TestMethod]
    public void TextAssertion_CompliantAssistantOutputEvidence_IsAccepted()
    {
        var check = TextSpec("check-1");
        var report = PassedReport("check-1", [$"assistant-output:{TurnId}@7"]);

        // EvaluateOne：合规证据放行；报告无 InvocationId/ReportRef/ExitCode 也放行（非 executed kind）。
        var single = GoalCheckEvidencePolicy.EvaluateOne(check, report, TurnId);
        Assert.AreEqual(GoalCriterionResultStatuses.Passed, single.Status);
        Assert.IsNull(single.FailureCode);

        // Evaluate：聚合入口同样放行。
        var evaluated = GoalCheckEvidencePolicy.Evaluate([check], [report], TurnId);
        Assert.AreEqual(1, evaluated.Count);
        Assert.AreEqual(GoalCriterionResultStatuses.Passed, evaluated[0].Status);
        Assert.IsNull(evaluated[0].FailureCode);
    }

    [TestMethod]
    public void TextAssertion_MissingAssistantOutputPrefix_IsEvidenceMissing()
    {
        var check = TextSpec("check-1");
        var report = PassedReport("check-1", ["file:src/ready.md"]);

        var result = GoalCheckEvidencePolicy.EvaluateOne(check, report, TurnId);

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.EvidenceMissing, result.FailureCode);
    }

    [TestMethod]
    public void TextAssertion_TurnIdMismatchOrMalformed_IsEvidenceMissing()
    {
        var check = TextSpec("check-1");
        string[] badRefs =
        [
            "assistant-output:other-turn@7",  // turnId 不一致
            "assistant-output:turn-10@7",     // 前缀混淆：turn-10 ≠ turn-1（ordinal 精确比较）
            "assistant-output:turn-1x@7",     // 后缀混淆
            $"assistant-output:{TurnId}",     // 缺 @sequence 段
            "assistant-output:@7",            // 空 turnId 段
        ];

        foreach (var evidenceRef in badRefs)
        {
            var report = PassedReport("check-1", [evidenceRef]);

            var result = GoalCheckEvidencePolicy.EvaluateOne(check, report, TurnId);

            Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status, evidenceRef);
            Assert.AreEqual(GoalCheckEvidencePolicy.EvidenceMissing, result.FailureCode, evidenceRef);
        }
    }

    [TestMethod]
    public void TextAssertion_WithoutContextTurnId_FailsClosed()
    {
        var check = TextSpec("check-1");
        var report = PassedReport("check-1", [$"assistant-output:{TurnId}@7"]);

        // FinalAssistantReply 不可得 ⇒ 不传/传空参考 turnId，任何 assistant-output 证据都不得放行。
        foreach (var missingTurnId in new string?[] { null, "" })
        {
            var single = GoalCheckEvidencePolicy.EvaluateOne(check, report, missingTurnId);
            Assert.AreEqual(GoalCriterionResultStatuses.Failed, single.Status);
            Assert.AreEqual(GoalCheckEvidencePolicy.EvidenceMissing, single.FailureCode);

            var evaluated = GoalCheckEvidencePolicy.Evaluate([check], [report], missingTurnId);
            Assert.AreEqual(1, evaluated.Count);
            Assert.AreEqual(GoalCriterionResultStatuses.Failed, evaluated[0].Status);
            Assert.AreEqual(GoalCheckEvidencePolicy.EvidenceMissing, evaluated[0].FailureCode);
        }
    }

    [TestMethod]
    public void UnknownKind_StillUnsupportedCheckKind_RegressionLock()
    {
        var check = TextSpec("check-1") with { Kind = "numerology" };
        var report = PassedReport("check-1", [$"assistant-output:{TurnId}@7"]);

        var result = GoalCheckEvidencePolicy.EvaluateOne(check, report, TurnId);

        Assert.AreEqual(GoalCriterionResultStatuses.Failed, result.Status);
        Assert.AreEqual(GoalCheckEvidencePolicy.UnsupportedCheckKind, result.FailureCode);
    }
}
