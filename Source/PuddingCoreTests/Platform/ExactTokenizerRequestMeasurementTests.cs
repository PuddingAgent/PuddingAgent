using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCoreTests;

[TestClass]
public sealed class ExactTokenizerRequestMeasurementTests
{
    private static void RequireExactTokenizer()
    {
        if (!ContextUsageSnapshotStore.DescribeTokenization().Models.Any(m => m.IsExact))
            Assert.Inconclusive("Run with PUDDING_TOKENIZER_ASSETS pointing to the DeepSeek vocabulary directory.");
    }

    [TestMethod]
    public void ProductionCountTokens_MatchesIndependentUnicodeVectors()
    {
        RequireExactTokenizer();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "unicode-regressions.json")));
        foreach (var vector in document.RootElement.GetProperty("vectors").EnumerateArray())
            Assert.AreEqual(vector.GetProperty("ids").GetArrayLength(),
                ContextUsageSnapshotStore.CountTokens(vector.GetProperty("text").GetString(), "deepseek-flash"));
    }

    [TestMethod]
    public void WarmCompactionMeasurement_HandlesEmojiInLongContentAndReasoning()
    {
        RequireExactTokenizer();
        // Independent Rust tokenizer: five tokens per line. Newline closes each split.
        var content = string.Concat(Enumerable.Repeat("你好 world 😀\n", 4000));
        var store = new ContextUsageSnapshotStore();
        var measured = store.MeasureLlmRequest("unicode-request",
            [new ChatMessage(ChatRole.User, content),
             new ChatMessage(ChatRole.Assistant, content, ReasoningContent: content)],
            tools: null, modelId: "deepseek-flash");

        Assert.AreEqual(60008, measured.RawEstimatedTokens); // 3 x 20,000 + 2 message envelopes
        Assert.AreEqual(20000, measured.ReasoningTokens);
        Assert.AreEqual(40008, measured.ConversationTokens);
        Assert.IsFalse(store.TryGet("unicode-request", out _), "Pre-compaction measurement must not publish a candidate.");
    }
}
