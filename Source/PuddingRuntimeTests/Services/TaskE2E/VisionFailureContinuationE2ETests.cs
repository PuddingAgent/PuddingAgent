using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntimeTests.Services.TaskE2E;

[TestClass]
public sealed class VisionFailureContinuationE2ETests
{
    [TestMethod]
    [DataRow(false, 601)]
    [DataRow(true, 601)]
    [DataRow(false, 1)]
    [DataRow(true, 1)]
    public async Task ImageFailureDoesNotFuseAndSameSessionCanContinueText(bool streaming, int images)
    {
        using var harness = new TaskE2EHarness(useRuntimeControl: true);
        await harness.InitializeAsync();
        harness.Llm.Enqueue("""{"status":"DONE","message":"text continuation succeeded"}""");
        var received = new List<IReadOnlyList<ChatMessage>>();
        harness.Llm.ValidateRequest = messages =>
        {
            received.Add(messages);
            // Exercise both real count preflight and an uncompressible historical image.
            _ = VisualInputRequestBudget.ForMessages(null, messages);
            if (messages.Any(m => ChatMessageMultimodalNormalizer.GetImageParts(m).Count > 0))
                throw new VisionPipelineException(VisionErrorCodes.RequestLimitExceeded, "Image cannot fit after preprocessing.");
        };
        var request = harness.CreateDispatchRequest($"vision-recovery-{streaming}-{images}", null) with
        {
            MessageId = "first",
            MessageText = "inspect images",
            ContentParts = Enumerable.Range(0, images).Select(i => (LlmContentPart)new LlmImagePart($"vision-{i:d32}")).ToArray(),
            LlmConfig = new LlmConfig { ModelId = "test-model", MaxContextTokens = 1_000_000 },
        };
        if (streaming)
        {
            var errors = new List<string>();
            await foreach (var frame in harness.ExecutionService.ExecuteStreamAsync(request))
                if (frame.Event == "error") errors.Add(frame.Data);
            Assert.IsTrue(errors.Any(e => e.Contains(VisionErrorCodes.RequestLimitExceeded)));
        }
        else
        {
            var failed = await harness.ExecutionService.ExecuteAsync(request);
            Assert.AreEqual(AgentExecutionState.Failed, failed.ExecutionState);
            StringAssert.Contains(failed.ErrorMessage!, "仍可继续发送文字");
        }
        Assert.AreNotEqual(SessionState.Faulted, harness.RuntimeControl.GetStatus(request.SessionId).Session?.State);
        Assert.AreEqual(0, harness.RuntimeControl.GetStatus(request.SessionId).Session?.WindowErrorCount ?? 0);

        var textRequest = request with { MessageId = "second", MessageText = "continue with text", ContentParts = null };
        if (streaming)
        {
            var frames = new List<string>();
            await foreach (var frame in harness.ExecutionService.ExecuteStreamAsync(textRequest))
            {
                Assert.AreNotEqual("error", frame.Event, frame.Data);
                frames.Add(frame.Data);
            }
            Assert.IsTrue(frames.Any(f => f.Contains("text continuation succeeded")));
        }
        else
        {
            var success = await harness.ExecutionService.ExecuteAsync(textRequest);
            Assert.AreEqual(AgentExecutionState.Completed, success.ExecutionState, success.ErrorMessage);
        }
        Assert.IsTrue(received.Last().Any(m => m.Content?.Contains("未发送像素") == true));
        Assert.IsFalse(received.Last().Any(m => ChatMessageMultimodalNormalizer.GetImageParts(m).Count > 0));
        Assert.AreEqual(1, harness.Llm.CallCount);
    }
}
