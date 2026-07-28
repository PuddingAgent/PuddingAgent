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
        Assert.AreEqual(report, parsed.Message);
    }
}
