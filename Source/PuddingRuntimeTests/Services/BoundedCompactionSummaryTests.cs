using System.Text;
using System.Text.Json;
using PuddingCode.Models;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class BoundedCompactionSummaryTests
{
    [TestMethod]
    public async Task LargeSource_IsBounded_AndAllSegmentsAreMergedBeforeSuccess()
    {
        var source = new[] { new ChatMessage(ChatRole.User, new string('x', 1_450_000)) };
        var calls = new List<IReadOnlyList<ChatMessage>>();
        var result = await BoundedCompactionSummary.GenerateAsync(source, (messages, index) =>
        {
            calls.Add(messages);
            Assert.IsTrue(messages.Sum(m => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(m.Content)))
                <= BoundedCompactionSummary.PartTextBytes);
            return Task.FromResult(Success($"checkpoint {index}: pending work"));
        }, CancellationToken.None);
        Assert.IsTrue(result.Success);
        Assert.IsTrue(calls.Count > 2);
        var finalInput = string.Join("\n", calls[^1].Select(m => m.Content));
        for (var i = 1; i < calls.Count; i++)
            StringAssert.Contains(finalInput, $"checkpoint {i}: pending work");
    }

    [TestMethod]
    public void SourceSplitting_PreservesUnicodeToolResultsAndImages()
    {
        var content = string.Concat(Enumerable.Repeat("中文🙂\\\"", 12000));
        var source = new ChatMessage(ChatRole.Tool, content, "call-1", "read_file",
            ContentParts: [new LlmTextPart(content), new LlmImagePart("image-1", "high")]);
        var parts = BoundedCompactionSummary.CreateParts([source]);
        Assert.IsTrue(parts.Count > 1);
        var pieces = parts.SelectMany(p => p.Skip(1)).ToArray();
        var reconstructed = string.Concat(pieces.Select(m => m.Content![(m.Content!.IndexOf('\n') + 1)..]));
        using var json = JsonDocument.Parse(reconstructed);
        Assert.AreEqual(content + "\n[attached image: image-1]", json.RootElement.GetProperty("content").GetString());
        Assert.AreEqual("call-1", json.RootElement.GetProperty("ToolCallId").GetString());
        Assert.AreEqual(1, pieces.Sum(m => ChatMessageMultimodalNormalizer.GetImageParts(m).Count));
        Assert.AreEqual("high", ChatMessageMultimodalNormalizer.GetImageParts(pieces[0]).Single().Detail);
        Assert.AreEqual(content, source.Content, "不得改动原历史");
    }

    [TestMethod]
    [DataRow("truncated")]
    [DataRow("failed")]
    [DataRow("empty")]
    public async Task AnySegmentFailure_DoesNotReturnPartialSummary(string failure)
    {
        var calls = 0;
        var result = await BoundedCompactionSummary.GenerateAsync(
            [new ChatMessage(ChatRole.User, new string('x', 500_000))], (_, _) =>
            {
                calls++;
                return Task.FromResult(calls == 1 ? Success("good first segment") : failure switch
                {
                    "failed" => AgentExecutionLlmInvocationResult.CreateFatal("upload reset"),
                    "empty" => Success(""),
                    _ => AgentExecutionLlmInvocationResult.CreateSuccess(new LlmResponse("partial", null) { FinishReason = "length" }, null),
                });
            }, CancellationToken.None);
        Assert.IsFalse(result.Success);
        Assert.IsNull(result.Response);
        if (failure == "truncated")
        {
            Assert.IsTrue(calls > 2 && calls <= BoundedCompactionSummary.MaxCalls);
            Assert.AreEqual("length", result.Attempts.Last().FinishReason);
            StringAssert.Contains(result.FailureCode!, "exhausted");
        }
        else Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task Cancellation_StopsBeforeCallingProvider()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => BoundedCompactionSummary.GenerateAsync(
            [new ChatMessage(ChatRole.User, "source")], (_, _) => throw new AssertFailedException("must not invoke"), cancellation.Token));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedSummary_RegeneratesFromSameSourceOnce_WithoutTruncation(bool alwaysOversized)
    {
        var source = new[] { new ChatMessage(ChatRole.Assistant, "original complete source; preserve this constraint") };
        var calls = 0;
        var result = await BoundedCompactionSummary.GenerateAsync(source, (messages, _) =>
        {
            calls++;
            StringAssert.Contains(string.Join("\n", messages.Select(m => m.Content)), "original complete source");
            return Task.FromResult(Success(calls == 1 || alwaysOversized ? new string('中', 200) : "concise complete checkpoint"));
        }, CancellationToken.None, allowance: new PuddingContextPolicy.ContextSummaryAllowance(100, 120, 0));
        Assert.AreEqual(2, calls);
        Assert.AreEqual(!alwaysOversized, result.Success);
        if (alwaysOversized) Assert.IsNull(result.Response);
        else Assert.AreEqual("concise complete checkpoint", result.Response!.Content);
    }

    [TestMethod]
    public async Task TruncatedThenComplete_RegeneratesOriginalSource_AndKeepsAttemptEvidence()
    {
        var calls = 0;
        var result = await BoundedCompactionSummary.GenerateAsync(
            [new ChatMessage(ChatRole.User, "COMPLETE ORIGINAL SOURCE")], (messages, _) =>
            {
                calls++;
                var input = string.Join("\n", messages.Select(m => m.Content));
                StringAssert.Contains(input, "COMPLETE ORIGINAL SOURCE");
                Assert.IsFalse(input.Contains("DISCARDED PARTIAL"));
                return Task.FromResult(calls == 1 ? AgentExecutionLlmInvocationResult.CreateSuccess(
                    new LlmResponse("DISCARDED PARTIAL", null, Usage: new TokenUsageDto
                    { PromptTokens = 30292, CompletionTokens = 4005, ReasoningTokens = 0 }) { FinishReason = "length" }, null)
                    : Success("complete checkpoint"));
            }, CancellationToken.None, allowance: new(4005, 108734, 90533));
        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, calls);
        Assert.AreEqual("output_truncated", result.Attempts[0].Decision);
        Assert.AreEqual(4005, result.Attempts[0].CompletionTokens);
        Assert.AreEqual(0, result.Attempts[0].ReasoningTokens);
        Assert.AreEqual("accepted", result.Attempts[1].Decision);
    }

    [TestMethod]
    public async Task DenseSource_SplitsAfterTwoRejections_AndMergesEveryCompleteChild()
    {
        var calls = 0;
        var result = await BoundedCompactionSummary.GenerateAsync(
            [new ChatMessage(ChatRole.User, new string('a', 9000)), new ChatMessage(ChatRole.Tool, new string('b', 9000))], (messages, _) =>
            {
                calls++;
                var input = string.Join("\n", messages.Select(m => m.Content));
                Assert.IsFalse(input.Contains("DISCARDED"));
                if (calls <= 2) return Task.FromResult(AgentExecutionLlmInvocationResult.CreateSuccess(
                    new LlmResponse("DISCARDED", null) { FinishReason = "length" }, null));
                if (input.Contains("child-a") || input.Contains("child-b"))
                {
                    StringAssert.Contains(input, "child-a");
                    StringAssert.Contains(input, "child-b");
                    return Task.FromResult(Success("merged checkpoint"));
                }
                return Task.FromResult(Success(input.Contains("aaaa") ? "child-a" : "child-b"));
            }, CancellationToken.None, allowance: new(100, 2000, 0));
        Assert.IsTrue(result.Success);
        Assert.AreEqual("merged checkpoint", result.Response!.Content);
        Assert.AreEqual(5, calls);
        CollectionAssert.AreEqual(new[] { "map", "map", "split", "split", "merge" }, result.Attempts.Select(a => a.Stage).ToArray());
    }

    private static AgentExecutionLlmInvocationResult Success(string text)
        => AgentExecutionLlmInvocationResult.CreateSuccess(new LlmResponse(text, null) { FinishReason = "stop" }, null);
}
