using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ExpectedOutputCandidateTrackerTests
{
    private const string CompleteReport = """
        SUMMARY: found the requested runtime and sub-agent behavior with enough detail.
        CHANGES: none because this was a read-only exploration.
        EVIDENCE:
          The implementation was inspected in AgentExecutionService.cs and SmartWorkflowToolBase.cs.
          The recorded event sequence proves the complete report existed before the DONE envelope.
        RISKS: line numbers may move after later edits.
        BLOCKERS: none because all required artifacts were available.
        """;

    [TestMethod]
    public void RestoresCompleteReport_WhenDoneMessageRegressesToStatusSummary()
    {
        var tracker = new ExpectedOutputCandidateTracker(
            CanonicalWorkReport.ExpectedOutputContract);

        Assert.IsTrue(tracker.Observe(CompleteReport));

        var finalMessage = "Exploration completed. The report above contains all details.";
        var restored = tracker.RestoreIfFinalIsIncomplete(ref finalMessage);

        Assert.IsTrue(restored);
        Assert.AreEqual(CompleteReport.Trim(), finalMessage);
    }

    [TestMethod]
    public void KeepsCurrentDoneMessage_WhenItContainsCompleteReport()
    {
        var tracker = new ExpectedOutputCandidateTracker(
            CanonicalWorkReport.ExpectedOutputContract);
        Assert.IsTrue(tracker.Observe(CompleteReport));

        var newerCompleteReport = CompleteReport.Replace(
            "requested runtime",
            "newer requested runtime",
            StringComparison.Ordinal);
        Assert.IsTrue(tracker.Observe(newerCompleteReport));

        var restored = tracker.RestoreIfFinalIsIncomplete(ref newerCompleteReport);

        Assert.IsFalse(restored);
        StringAssert.Contains(newerCompleteReport, "newer requested runtime");
    }

    [TestMethod]
    public void DoesNotRestore_WhenExpectedContractIsNotCanonical()
    {
        var tracker = new ExpectedOutputCandidateTracker("Return a short sentence.");
        Assert.IsFalse(tracker.Observe(CompleteReport));

        var finalMessage = "Done.";
        var restored = tracker.RestoreIfFinalIsIncomplete(ref finalMessage);

        Assert.IsFalse(restored);
        Assert.AreEqual("Done.", finalMessage);
    }

    [TestMethod]
    public void RejectsActuallyIncompleteCanonicalReport()
    {
        Assert.IsFalse(CanonicalWorkReport.TryValidate(
            "SUMMARY: done\nCHANGES: none\nEVIDENCE: none\nRISKS: none\nBLOCKERS: none",
            out var error));
        StringAssert.Contains(error, "report is too short");
    }

    [TestMethod]
    public void AutoCompletes_UnstructuredContractCompleteResponse()
    {
        var tracker = new ExpectedOutputCandidateTracker(
            CanonicalWorkReport.ExpectedOutputContract);
        var response = AgentLoopResponse.Parse(CompleteReport);

        Assert.IsTrue(tracker.ShouldAutoComplete(response, response.Message));
    }

    [TestMethod]
    public void DoesNotOverride_ExplicitStructuredContinue()
    {
        var tracker = new ExpectedOutputCandidateTracker(
            CanonicalWorkReport.ExpectedOutputContract);
        var response = AgentLoopResponse.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            status = "CONTINUE",
            message = CompleteReport,
            tool = (object?)null,
        }));

        Assert.IsTrue(response.IsStructured);
        Assert.IsFalse(tracker.ShouldAutoComplete(response, response.Message));
    }

    [TestMethod]
    public void ToolLoopPromptRequiresCompleteDeliverableInsideDoneEnvelope()
    {
        var prompt = ToolLoopInstructionBuilder.BuildFromDescriptors([]);

        StringAssert.Contains(prompt, "Runtime control envelope");
        StringAssert.Contains(prompt, "COMPLETE requested deliverable");
        StringAssert.Contains(prompt, "must never be only a status sentence");
        StringAssert.Contains(prompt, "`search_grep` is the rg-like content-search tool");
    }
}
