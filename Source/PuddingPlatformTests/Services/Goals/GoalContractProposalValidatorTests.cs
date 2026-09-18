using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Goals;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// G92-1 S1-c 片6 3a：GoalContractProposalValidator 内容级 fail-closed 门测试。
/// 覆盖：事实门（仅 turn.completed + EvidenceComplete）、受控 kind 门、自报身份/hash 拒绝
/// （D1–D3 核心防线）、requirementRefs 覆盖门（空覆盖/删义务）、payload 读取兼容性
/// （历史行无 goal_contract_proposal 键 ⇒ 无提议不抛异常）、canonical op key 稳定性。
/// </summary>
[TestClass]
public sealed class GoalContractProposalValidatorTests
{
    private const string Objective = "只输出 READY";

    private static GoalContractProposalFacts Facts(
        string? proposalJson = null,
        string terminalKind = "completed",
        bool evidenceComplete = true,
        string? objective = null) => new()
    {
        GoalRunId = "goal-1",
        ActivationEpoch = 1,
        ObjectiveVersion = 3,
        TurnId = "turn-1",
        AggregateVersion = 7,
        Objective = objective ?? Objective,
        TerminalKind = terminalKind,
        EvidenceComplete = evidenceComplete,
        ProposalJson = proposalJson ?? ValidProposalJson(),
    };

    private static string ValidProposalJson(
        string expectedText = "READY",
        int expectedVersion = 1,
        string requirementRefs = "[\"只输出 READY\"]",
        string verificationKind = "text-assertion",
        string definitionRef = "checks/text-assertion.md#equals",
        string extraFields = "")
        => $$$"""
            {"schemaVersion":1,"kind":"refine_acceptance_contract","expectedContractVersion":{{{expectedVersion}}},"criteria":[{"requirement":"最终输出必须严格等于期望文本","requirementRefs":{{{requirementRefs}}},"verification":{"kind":"{{{verificationKind}}}","definitionRef":"{{{definitionRef}}}","inputRefs":[],"expectedText":"{{{expectedText}}}"{{{extraFields}}}}}]}
            """;

    // ── 正路径：派生计划 / op key 稳定性 ──

    [TestMethod]
    public void Validate_ValidProposal_AcceptsAndDerivesPlatformOwnedPlan()
    {
        var validation = GoalContractProposalValidator.Validate(Facts());

        Assert.IsTrue(validation.IsAccepted, validation.RejectionReason);
        var plan = validation.Plan!;
        Assert.AreEqual(1, plan.ExpectedContractVersion);
        Assert.AreEqual(1, plan.Criteria.Count);
        Assert.AreEqual("objective-text-assertion:0", plan.Criteria[0].Id);
        Assert.AreEqual(GoalVerificationSpecKinds.TextAssertion, plan.Criteria[0].Kind);
        Assert.AreEqual(GoalCheckDefinitionRegistry.TextAssertionRef, plan.Criteria[0].DefinitionRef);
        Assert.AreEqual("READY", plan.Checks[0].ExpectedText);
        Assert.AreEqual(0, plan.Checks[0].InputRefs.Count);
        Assert.AreEqual(plan.Criteria[0].DefinitionHash, plan.Checks[0].DefinitionHash);

        // DefinitionHash 由 Registry 计算、期望文本参与载荷（方案 A）。
        Assert.IsTrue(GoalCheckDefinitionRegistry.TryResolve(
            GoalCheckDefinitionRegistry.TextAssertionRef, out var definition));
        Assert.AreEqual(
            GoalCheckDefinitionRegistry.ComputeDefinitionHash(definition with { ExpectedText = "READY" }),
            plan.Criteria[0].DefinitionHash);

        // operation key = sha256: + 64 hex（服务端派生，不含任何 Agent 可自报的 hash/revision 字段）。
        Assert.IsTrue(plan.OperationKey.StartsWith("sha256:", StringComparison.Ordinal));
        Assert.AreEqual(71, plan.OperationKey.Length);
    }

    [TestMethod]
    public void Validate_SameContentDifferentRawWhitespace_SameOperationKey()
    {
        var compact = GoalContractProposalValidator.Validate(Facts()).Plan!.OperationKey;
        var spaced = "{\"schemaVersion\" : 1, \"kind\" : \"refine_acceptance_contract\", "
            + "\"expectedContractVersion\" : 1, \"criteria\" : [ { \"requirement\" : "
            + "\"最终输出必须严格等于期望文本\", \"requirementRefs\" : [ \"只输出 READY\" ], "
            + "\"verification\" : { \"kind\" : \"text-assertion\", \"definitionRef\" : "
            + "\"checks/text-assertion.md#equals\", \"inputRefs\" : [ ], \"expectedText\" : \"READY\" } } ] }";

        var validation = GoalContractProposalValidator.Validate(Facts(spaced));

        Assert.IsTrue(validation.IsAccepted, validation.RejectionReason);
        Assert.AreEqual(compact, validation.Plan!.OperationKey);
    }

    // ── 事实门：只绑定 canonical turn.completed + EvidenceComplete ──

    [TestMethod]
    public void Validate_TerminalKindNotCompleted_IsRejected()
    {
        var validation = GoalContractProposalValidator.Validate(Facts(terminalKind: "failed"));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(validation.RejectionReason!.Contains("turn.completed", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_EvidenceIncomplete_IsRejected()
    {
        var validation = GoalContractProposalValidator.Validate(Facts(evidenceComplete: false));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(validation.RejectionReason!.Contains("EvidenceComplete", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_NoProposalPayload_IsRejected()
    {
        // 空串/空白 = payload 无提议（与历史行无键同语义），验证器 fail-closed 拒绝。
        var validation = GoalContractProposalValidator.Validate(Facts(proposalJson: ""));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(validation.RejectionReason!.Contains("no goal_contract_proposal", StringComparison.Ordinal));
    }

    // ── 自报身份/hash 拒绝（D1–D3 核心防线，解析层 fail-closed）──

    [TestMethod]
    public void Validate_SelfReportedGoalRunId_IsRejected()
    {
        var json = "{\"schemaVersion\":1,\"kind\":\"refine_acceptance_contract\","
            + "\"expectedContractVersion\":1,\"goalRunId\":\"goal-evil\","
            + "\"criteria\":[{\"requirement\":\"r\",\"requirementRefs\":[\"只输出 READY\"],"
            + "\"verification\":{\"kind\":\"text-assertion\",\"definitionRef\":"
            + "\"checks/text-assertion.md#equals\",\"inputRefs\":[],\"expectedText\":\"READY\"}}]}";

        var validation = GoalContractProposalValidator.Validate(Facts(json));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(validation.RejectionReason!.Contains("agent-forbidden field 'goalRunId'", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_SelfReportedDefinitionHash_IsRejected()
    {
        var json = ValidProposalJson(extraFields: ",\"definitionHash\":\"sha256:evil\"");

        var validation = GoalContractProposalValidator.Validate(Facts(json));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(validation.RejectionReason!.Contains("agent-forbidden field 'definitionHash'", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_SelfReportedCriterionRevision_IsRejected()
    {
        var json = "{\"schemaVersion\":1,\"kind\":\"refine_acceptance_contract\","
            + "\"expectedContractVersion\":1,"
            + "\"criteria\":[{\"requirement\":\"r\",\"criterionRevision\":4,"
            + "\"requirementRefs\":[\"只输出 READY\"],"
            + "\"verification\":{\"kind\":\"text-assertion\",\"definitionRef\":"
            + "\"checks/text-assertion.md#equals\",\"inputRefs\":[],\"expectedText\":\"READY\"}}]}";

        var validation = GoalContractProposalValidator.Validate(Facts(json));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(validation.RejectionReason!.Contains("agent-forbidden field 'criterionRevision'", StringComparison.Ordinal));
    }

    // ── 受控 kind 门：仅 text-assertion + 已登记定义 ──

    [TestMethod]
    public void Validate_NonTextAssertionKind_IsRejected()
    {
        var json = ValidProposalJson(verificationKind: "semantic");

        var validation = GoalContractProposalValidator.Validate(Facts(json));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(validation.RejectionReason!.Contains("must be 'text-assertion'", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_UnregisteredDefinitionRef_IsRejected()
    {
        var json = ValidProposalJson(definitionRef: "sh -c 'rm -rf /'");

        var validation = GoalContractProposalValidator.Validate(Facts(json));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(validation.RejectionReason!.Contains("registered", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_MissingExpectedText_IsRejected()
    {
        var json = "{\"schemaVersion\":1,\"kind\":\"refine_acceptance_contract\","
            + "\"expectedContractVersion\":1,"
            + "\"criteria\":[{\"requirement\":\"r\",\"requirementRefs\":[\"只输出 READY\"],"
            + "\"verification\":{\"kind\":\"text-assertion\",\"definitionRef\":"
            + "\"checks/text-assertion.md#equals\",\"inputRefs\":[]}}]}";

        var validation = GoalContractProposalValidator.Validate(Facts(json));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(validation.RejectionReason!.Contains("expectedText", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_TextAssertionWithInputRefs_IsRejected()
    {
        var json = "{\"schemaVersion\":1,\"kind\":\"refine_acceptance_contract\","
            + "\"expectedContractVersion\":1,"
            + "\"criteria\":[{\"requirement\":\"r\",\"requirementRefs\":[\"只输出 READY\"],"
            + "\"verification\":{\"kind\":\"text-assertion\",\"definitionRef\":"
            + "\"checks/text-assertion.md#equals\",\"inputRefs\":[\"Source/A.csproj\"],"
            + "\"expectedText\":\"READY\"}}]}";

        var validation = GoalContractProposalValidator.Validate(Facts(json));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(validation.RejectionReason!.Contains("inputRefs must be empty", StringComparison.Ordinal));
    }

    // ── 覆盖门：空覆盖 / 无关引用 / 删义务 ──

    [TestMethod]
    public void Validate_RefNotGroundedInObjective_IsRejected()
    {
        var json = ValidProposalJson(requirementRefs: "[\"完全无关的任意内容\"]");

        var validation = GoalContractProposalValidator.Validate(Facts(json));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(
            validation.RejectionReason!.Contains("does not reference the frozen objective", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_RefsDoNotCoverWholeObjective_IsRejected()
    {
        // objective 有三个词元（输出/READY/文件），refs 只覆盖 READY ⇒ 删义务，fail-closed。
        var json = ValidProposalJson(requirementRefs: "[\"READY\"]");

        var validation = GoalContractProposalValidator.Validate(Facts(json, objective: "输出 READY 文件"));

        Assert.IsFalse(validation.IsAccepted);
        Assert.IsTrue(
            validation.RejectionReason!.Contains("do not cover the whole objective", StringComparison.Ordinal));
    }

    // ── payload 读取兼容性（历史行无键 ⇒ 无提议，不抛异常）──

    [TestMethod]
    public void ReadProposal_MissingKey_TreatedAsNoProposalWithoutThrowing()
    {
        // 段2 之前的历史 turn.completed payload：没有 goal_contract_proposal 键。
        using var document = JsonDocument.Parse(
            """{"kind":"completed","errorCode":null,"errorMessage":null,"reply":"完成"}""");

        var read = GoalContractProposalValidator.ReadProposalFromTurnCompletedPayload(document.RootElement);

        Assert.IsFalse(read.HasProposal);
        Assert.IsNull(read.ProposalJson);
        Assert.IsNull(read.RejectionReason);
    }

    [TestMethod]
    public void ReadProposal_ExplicitNullKey_TreatedAsNoProposal()
    {
        using var document = JsonDocument.Parse(
            """{"kind":"completed","reply":"ok","goal_contract_proposal":null}""");

        var read = GoalContractProposalValidator.ReadProposalFromTurnCompletedPayload(document.RootElement);

        Assert.IsFalse(read.HasProposal);
        Assert.IsNull(read.RejectionReason);
    }

    [TestMethod]
    public void ReadProposal_NonObjectKey_FailClosedWithReason()
    {
        using var document = JsonDocument.Parse(
            """{"kind":"completed","reply":"ok","goal_contract_proposal":"{\"schemaVersion\":1}"}""");

        var read = GoalContractProposalValidator.ReadProposalFromTurnCompletedPayload(document.RootElement);

        Assert.IsFalse(read.HasProposal);
        Assert.IsNotNull(read.RejectionReason);
        Assert.IsTrue(read.RejectionReason!.Contains("json object", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReadProposal_ObjectKey_ReturnsRawJsonForParser()
    {
        using var document = JsonDocument.Parse(
            """{"kind":"completed","reply":"ok","goal_contract_proposal":{"schemaVersion":1,"kind":"refine_acceptance_contract"}}""");

        var read = GoalContractProposalValidator.ReadProposalFromTurnCompletedPayload(document.RootElement);

        Assert.IsTrue(read.HasProposal);
        Assert.IsNull(read.RejectionReason);
        Assert.IsTrue(read.ProposalJson!.Contains("refine_acceptance_contract", StringComparison.Ordinal));
    }
}
