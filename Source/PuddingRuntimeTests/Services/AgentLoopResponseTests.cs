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
}
