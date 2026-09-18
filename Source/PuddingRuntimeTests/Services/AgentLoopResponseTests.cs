using System.Text.Json;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentLoopResponseTests
{
    [TestMethod]
    public void Parse_ExtractsDoneEnvelopeFromJsonFenceAfterProsePrefix()
    {
        const string report = """
            SUMMARY:
            The planner produced a complete implementation-ready plan with verified scope and acceptance criteria.
            CHANGES:
            none because planning is read-only.
            EVIDENCE:
            Verified Source/PuddingRuntime/Services/AgentLoop/AgentLoopResponse.cs and its parser behavior.
            RISKS:
            Provider formatting may include prose before the JSON envelope.
            BLOCKERS:
            none because the required source and runtime evidence were available.
            """;
        var envelope = JsonSerializer.Serialize(new
        {
            status = "DONE",
            message = report,
            tool = (object?)null,
        });
        var providerOutput = $"""
            Now I have sufficient evidence. Let me compile the final report.

            ```json
            {envelope}
            ```
            """;

        var parsed = AgentLoopResponse.Parse(providerOutput);

        Assert.IsTrue(parsed.IsDone);
        Assert.IsTrue(parsed.IsStructured);
        Assert.AreEqual(report, parsed.Message);
    }

    [TestMethod]
    public void Parse_PlainCanonicalReport_RemainsUnstructuredContinueForContractPolicy()
    {
        const string providerOutput = """
            SUMMARY: Completed the requested delegated implementation with verified behavior.
            CHANGES: Updated the runtime completion policy and focused regression tests.
            EVIDENCE: The canonical report contains enough concrete source and test evidence.
            RISKS: Deployment still requires an external process restart.
            BLOCKERS: none because the scoped implementation is complete.
            """;

        var parsed = AgentLoopResponse.Parse(providerOutput);

        Assert.IsFalse(parsed.IsStructured);
        Assert.IsFalse(parsed.IsDone);
        Assert.AreEqual("CONTINUE", parsed.Status);
        Assert.AreEqual(providerOutput, parsed.Message);
    }

    // ── A1（G92-1 S1-c 片6 段2）：hidden meta.goal_contract_proposal 的 fail-closed 解析 ──

    private const string LegalProposalJson = """
        {"schemaVersion":1,"kind":"refine_acceptance_contract","expectedContractVersion":3,
         "criteria":[{"requirement":"只输出 READY","requirementRefs":["objective:line-1"],
           "verification":{"kind":"text-assertion","definitionRef":"checks/text-assertion.md#equals",
             "inputRefs":["reply"],"expectedText":"READY"}}]}
        """;

    [TestMethod]
    public void Parse_LegalGoalContractProposal_YieldsTypedProposal()
    {
        var envelope = JsonSerializer.Serialize(new
        {
            status = "DONE",
            message = "done",
            meta = new
            {
                reason = "settle",
                confidence = 0.9,
                goal_contract_proposal = JsonDocument.Parse(LegalProposalJson).RootElement.Clone(),
            },
        });

        var parsed = AgentLoopResponse.Parse(envelope);

        Assert.IsTrue(parsed.IsStructured);
        Assert.IsNotNull(parsed.Meta?.GoalContractProposal);
        Assert.IsNull(parsed.Meta?.GoalContractProposalRejectionReason);
        var proposal = parsed.Meta!.GoalContractProposal!;
        Assert.AreEqual(1, proposal.SchemaVersion);
        Assert.AreEqual("refine_acceptance_contract", proposal.Kind);
        Assert.AreEqual(3, proposal.ExpectedContractVersion);
        Assert.HasCount(1, proposal.Criteria);
        Assert.AreEqual("READY", proposal.Criteria[0].Verification.ExpectedText);
    }

    [TestMethod]
    public void Parse_WithoutGoalContractProposal_KeepsNullProposal()
    {
        const string envelope = """{"status":"CONTINUE","message":"working","meta":{"reason":"iterating"}}""";

        var parsed = AgentLoopResponse.Parse(envelope);

        Assert.IsTrue(parsed.IsStructured);
        Assert.IsNotNull(parsed.Meta);
        Assert.IsNull(parsed.Meta!.GoalContractProposal);
        Assert.IsNull(parsed.Meta.GoalContractProposalRejectionReason);
    }

    [TestMethod]
    public void Parse_UnknownProposalField_FailsClosedWithReason()
    {
        var rogue = LegalProposalJson.Replace(
            "\"criteria\":", "\"rogue\":true,\"criteria\":", StringComparison.Ordinal);
        var envelope = JsonSerializer.Serialize(new
        {
            status = "DONE",
            message = "done",
            meta = new { goal_contract_proposal = JsonDocument.Parse(rogue).RootElement.Clone() },
        });

        var parsed = AgentLoopResponse.Parse(envelope);

        Assert.IsNull(parsed.Meta?.GoalContractProposal);
        Assert.IsNotNull(parsed.Meta?.GoalContractProposalRejectionReason);
        Assert.IsTrue(parsed.Meta!.GoalContractProposalRejectionReason!.Contains(
            "unknown field", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Parse_AgentForbiddenProposalField_FailsClosedWithReason()
    {
        var smuggled = LegalProposalJson.Replace(
            "\"criteria\":", "\"goalRunId\":\"g1\",\"criteria\":", StringComparison.Ordinal);
        var envelope = JsonSerializer.Serialize(new
        {
            status = "DONE",
            message = "done",
            meta = new { goal_contract_proposal = JsonDocument.Parse(smuggled).RootElement.Clone() },
        });

        var parsed = AgentLoopResponse.Parse(envelope);

        Assert.IsNull(parsed.Meta?.GoalContractProposal);
        Assert.IsTrue(parsed.Meta!.GoalContractProposalRejectionReason!.Contains(
            "agent-forbidden", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Parse_NonObjectProposal_FailsClosedWithoutThrowing()
    {
        const string envelope = """{"status":"DONE","message":"done","meta":{"goal_contract_proposal":"{\"schemaVersion\":1}"}}""";

        var parsed = AgentLoopResponse.Parse(envelope);

        Assert.IsNull(parsed.Meta?.GoalContractProposal);
        Assert.IsTrue(parsed.Meta!.GoalContractProposalRejectionReason!.Contains(
            "must be a json object", StringComparison.Ordinal));
    }
}
