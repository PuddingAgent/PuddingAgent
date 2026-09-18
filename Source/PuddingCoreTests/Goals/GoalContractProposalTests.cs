using PuddingCode.Goals;

namespace PuddingCoreTests.Goals;

[TestClass]
public sealed class GoalContractProposalTests
{
    private const string ValidProposalJson = """
        {
          "schemaVersion": 1,
          "kind": "refine_acceptance_contract",
          "expectedContractVersion": 3,
          "criteria": [
            {
              "requirement": "最终回复严格等于 READY",
              "requirementRefs": ["objective:line:1"],
              "verification": {
                "kind": "text-assertion",
                "definitionRef": "checks/text-assertion.md#equals",
                "inputRefs": ["assistant-output"],
                "expectedText": "READY"
              }
            }
          ]
        }
        """;

    [TestMethod]
    public void Valid_Payload_Parses_Into_Typed_Proposal()
    {
        var result = GoalContractProposalParser.Parse(ValidProposalJson);

        Assert.IsTrue(result.IsAccepted, result.RejectionReason ?? "rejected without reason");
        Assert.IsNotNull(result.Proposal);
        Assert.AreEqual(1, result.Proposal.SchemaVersion);
        Assert.AreEqual("refine_acceptance_contract", result.Proposal.Kind);
        Assert.AreEqual(3, result.Proposal.ExpectedContractVersion);
        Assert.AreEqual(1, result.Proposal.Criteria.Count);

        var criterion = result.Proposal.Criteria[0];
        Assert.AreEqual("最终回复严格等于 READY", criterion.Requirement);
        CollectionAssert.AreEqual(
            new[] { "objective:line:1" },
            criterion.RequirementRefs.ToArray());

        var verification = criterion.Verification;
        Assert.AreEqual("text-assertion", verification.Kind);
        Assert.AreEqual("checks/text-assertion.md#equals", verification.DefinitionRef);
        CollectionAssert.AreEqual(
            new[] { "assistant-output" },
            verification.InputRefs.ToArray());
        Assert.AreEqual("READY", verification.ExpectedText);
    }

    [TestMethod]
    public void Unknown_TopLevel_Field_Is_Rejected()
    {
        var json = ValidProposalJson.Replace(
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 1,\n  \"proposalId\": \"p-1\"");

        var result = GoalContractProposalParser.Parse(json);

        Assert.IsFalse(result.IsAccepted);
        Assert.IsTrue(
            result.RejectionReason!.Contains("unknown field 'proposalId'"),
            result.RejectionReason);
    }

    [TestMethod]
    public void Unknown_Criterion_Field_Is_Rejected()
    {
        var json = ValidProposalJson.Replace(
            "\"requirement\": \"最终回复严格等于 READY\"",
            "\"requirement\": \"最终回复严格等于 READY\",\n      \"priority\": 1");

        var result = GoalContractProposalParser.Parse(json);

        Assert.IsFalse(result.IsAccepted);
        Assert.IsTrue(
            result.RejectionReason!.Contains("unknown field 'priority'"),
            result.RejectionReason);
    }

    [TestMethod]
    public void Unknown_Verification_Field_Is_Rejected()
    {
        var json = ValidProposalJson.Replace(
            "\"expectedText\": \"READY\"",
            "\"expectedText\": \"READY\",\n        \"confidence\": 0.9");

        var result = GoalContractProposalParser.Parse(json);

        Assert.IsFalse(result.IsAccepted);
        Assert.IsTrue(
            result.RejectionReason!.Contains("unknown field 'confidence'"),
            result.RejectionReason);
    }

    [TestMethod]
    public void Agent_Self_Reported_Identity_Fields_Are_Rejected_At_Root()
    {
        foreach (var forbidden in new[] { "goalRunId", "activationEpoch", "turnId", "source" })
        {
            var json = ValidProposalJson.Replace(
                "\"schemaVersion\": 1",
                $"\"schemaVersion\": 1,\n  \"{forbidden}\": \"x\"");

            var result = GoalContractProposalParser.Parse(json);

            Assert.IsFalse(result.IsAccepted, $"root '{forbidden}' must be rejected");
            Assert.IsTrue(
                result.RejectionReason!.Contains($"agent-forbidden field '{forbidden}'"),
                result.RejectionReason);
        }
    }

    [TestMethod]
    public void Agent_Self_Reported_State_Fields_Are_Rejected_At_Criterion_And_Verification()
    {
        var criterionJson = ValidProposalJson.Replace(
            "\"requirement\": \"最终回复严格等于 READY\"",
            "\"requirement\": \"最终回复严格等于 READY\",\n      \"criterionRevision\": 2,\n      \"passed\": true");

        var criterionResult = GoalContractProposalParser.Parse(criterionJson);
        Assert.IsFalse(criterionResult.IsAccepted, "criterion-level self-report must be rejected");
        Assert.IsTrue(
            criterionResult.RejectionReason!.Contains("agent-forbidden field 'criterionRevision'")
                || criterionResult.RejectionReason.Contains("agent-forbidden field 'passed'"),
            criterionResult.RejectionReason);

        var verificationJson = ValidProposalJson.Replace(
            "\"expectedText\": \"READY\"",
            "\"definitionHash\": \"abc\",\n        \"status\": \"passed\"");

        var verificationResult = GoalContractProposalParser.Parse(verificationJson);
        Assert.IsFalse(verificationResult.IsAccepted, "verification-level self-report must be rejected");
        Assert.IsTrue(
            verificationResult.RejectionReason!.Contains("agent-forbidden field 'definitionHash'")
                || verificationResult.RejectionReason.Contains("agent-forbidden field 'status'"),
            verificationResult.RejectionReason);
    }

    [TestMethod]
    public void Missing_Verification_Kind_Is_Rejected()
    {
        var json = ValidProposalJson.Replace("\"kind\": \"text-assertion\",", string.Empty);

        var result = GoalContractProposalParser.Parse(json);

        Assert.IsFalse(result.IsAccepted);
        Assert.IsTrue(
            result.RejectionReason!.Contains("'verification.kind' is required"),
            result.RejectionReason);
    }

    [TestMethod]
    public void Missing_Verification_DefinitionRef_Is_Rejected()
    {
        var json = ValidProposalJson.Replace(
            "\"definitionRef\": \"checks/text-assertion.md#equals\",",
            string.Empty);

        var result = GoalContractProposalParser.Parse(json);

        Assert.IsFalse(result.IsAccepted);
        Assert.IsTrue(
            result.RejectionReason!.Contains("'verification.definitionRef' is required"),
            result.RejectionReason);
    }

    [TestMethod]
    public void ExpectedText_Is_Optional()
    {
        var json = ValidProposalJson.Replace(
            ",\n        \"expectedText\": \"READY\"",
            string.Empty);

        var result = GoalContractProposalParser.Parse(json);

        Assert.IsTrue(result.IsAccepted, result.RejectionReason ?? "rejected");
        Assert.IsNull(result.Proposal!.Criteria[0].Verification.ExpectedText);
    }

    [TestMethod]
    public void AgentRefined_Source_Value_Is_Registered_And_Shorter_Than_Entity_MaxLength()
    {
        Assert.AreEqual("agent_refined", GoalAcceptanceContractSources.AgentRefined);
        Assert.IsTrue(
            GoalAcceptanceContractSources.AgentRefined.Length < 32,
            $"source 值长度 {GoalAcceptanceContractSources.AgentRefined.Length} 必须 < MaxLength(32)");
    }

    [TestMethod]
    public void ContractRefined_Event_Type_Is_Registered()
    {
        Assert.AreEqual("goal.contract_refined", GoalEventTypes.ContractRefined);
    }

    [TestMethod]
    public void Malformed_Envelopes_Are_Rejected_Fail_Closed()
    {
        var cases = new Dictionary<string, string>
        {
            ["not json"] = "{ not json",
            ["root not object"] = "[1, 2, 3]",
            ["wrong schemaVersion"] =
                "{\"schemaVersion\":2,\"kind\":\"refine_acceptance_contract\",\"expectedContractVersion\":3,\"criteria\":[]}",
            ["wrong kind"] =
                "{\"schemaVersion\":1,\"kind\":\"plan.proposal\",\"expectedContractVersion\":3,\"criteria\":[]}",
            ["empty criteria"] =
                "{\"schemaVersion\":1,\"kind\":\"refine_acceptance_contract\",\"expectedContractVersion\":3,\"criteria\":[]}",
            ["missing expectedContractVersion"] =
                "{\"schemaVersion\":1,\"kind\":\"refine_acceptance_contract\",\"criteria\":[]}",
        };

        foreach (var (name, json) in cases)
        {
            var result = GoalContractProposalParser.Parse(json);
            Assert.IsFalse(result.IsAccepted, $"case '{name}' must be rejected");
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.RejectionReason), name);
        }
    }

    [TestMethod]
    public void Duplicate_Allowed_Field_Is_Rejected()
    {
        var json = ValidProposalJson.Replace(
            "\"expectedContractVersion\": 3",
            "\"expectedContractVersion\": 3,\n  \"expectedContractVersion\": 4");

        var result = GoalContractProposalParser.Parse(json);

        Assert.IsFalse(result.IsAccepted);
        Assert.IsTrue(
            result.RejectionReason!.Contains("duplicate field 'expectedContractVersion'"),
            result.RejectionReason);
    }
}
