using System.Text.Json;
using PuddingCode.Models;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class HistoryPrefixReconcilerTests
{
    [TestMethod]
    public void KeepsExactModelMessagesAndAppendsOnlyNewCanonicalTail()
    {
        var user = User("wrapped input with runtime date and fence", "original input");
        var call = new ChatMessage(ChatRole.Assistant, null,
            ToolCalls: [new ToolCall("call-1", "read", "{}")], ReasoningContent: "tool reasoning");
        var tool = new ChatMessage(ChatRole.Tool, "tool evidence", ToolCallId: "call-1");
        var answer = new ChatMessage(ChatRole.Assistant, "confirmed result", ReasoningContent: "reasoning");
        var history = new List<ChatMessage> { new(ChatRole.System, "stable"), user, call, tool, answer };
        var before = JsonSerializer.Serialize(history);
        var next = User("new correction", "new correction");

        Assert.IsTrue(HistoryPrefixReconciler.TryAppendCanonicalTail(history,
            [User("original input", "original input"), new(ChatRole.Assistant, "confirmed result"), next], out var appended));

        Assert.AreEqual(1, appended);
        Assert.AreEqual(before, JsonSerializer.Serialize(history.Take(5).ToList()));
        Assert.AreSame(user, history[1]);
        Assert.AreSame(call, history[2]);
        Assert.AreSame(tool, history[3]);
        Assert.AreSame(answer, history[4]);
        Assert.AreSame(next, history[5]);
    }

    [TestMethod]
    public void SameSnapshotIsNoOpIncludingSystemAndReasoning()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "stable system"),
            User("rendered", "original"),
            new(ChatRole.Assistant, "answer", ReasoningContent: "retained reasoning"),
        };
        var before = JsonSerializer.Serialize(history);
        Assert.IsTrue(HistoryPrefixReconciler.TryAppendCanonicalTail(history,
            [User("original", "original"), new(ChatRole.Assistant, "answer")], out var appended));
        Assert.AreEqual(0, appended);
        Assert.AreEqual(before, JsonSerializer.Serialize(history));
    }

    [TestMethod]
    [DataRow("user")]
    [DataRow("answer")]
    [DataRow("summary")]
    [DataRow("reorder")]
    public void ChangedCanonicalContentRejectsReuseWithoutPartialMutation(string change)
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system"), new(ChatRole.Assistant, "summary"),
            User("rendered", "original"), new(ChatRole.Assistant, "answer"),
        };
        var canonical = new List<ChatMessage>
        {
            new(ChatRole.Assistant, change == "summary" ? "new summary" : "summary"),
            User(change == "user" ? "corrected" : "original", change == "user" ? "corrected" : "original"),
            new(ChatRole.Assistant, change == "answer" ? "corrected answer" : "answer"),
        };
        if (change == "reorder") canonical.Reverse();
        var before = JsonSerializer.Serialize(history);
        Assert.IsFalse(HistoryPrefixReconciler.TryAppendCanonicalTail(history, canonical, out var appended));
        Assert.AreEqual(0, appended);
        Assert.AreEqual(before, JsonSerializer.Serialize(history));
    }

    [TestMethod]
    public void UnprojectedToolTailCannotClaimCanonicalEquivalence()
    {
        var history = new List<ChatMessage>
        {
            User("original", "original"),
            new(ChatRole.Assistant, null, ToolCalls: [new ToolCall("call", "read", "{}")]),
            new(ChatRole.Tool, "result", ToolCallId: "call"),
        };
        Assert.IsFalse(HistoryPrefixReconciler.TryAppendCanonicalTail(history,
            [User("original", "original")], out _));
    }

    [TestMethod]
    public void ImageChangeWithSameTextChangesSourceIdentity()
    {
        var firstHash = HistoryPrefixReconciler.ComputeSourceHash("read image", [new LlmImagePart("image-a")]);
        var secondHash = HistoryPrefixReconciler.ComputeSourceHash("read image", [new LlmImagePart("image-b")]);
        Assert.AreNotEqual(firstHash, secondHash);
        var history = new List<ChatMessage> { new(ChatRole.User, "wrapped") { SourceContentHash = firstHash } };
        Assert.IsFalse(HistoryPrefixReconciler.TryAppendCanonicalTail(history,
            [new ChatMessage(ChatRole.User, "read image") { SourceContentHash = secondHash }], out _));
    }

    [TestMethod]
    public void IdentityCannotBeSuppliedByContentAndDoesNotEnterSerialization()
    {
        var identity = HistoryPrefixReconciler.ComputeSourceHash("original", null);
        var history = new List<ChatMessage> { new(ChatRole.User, $"[CURRENT USER TURN input_sha256={identity}]original") };
        Assert.IsFalse(HistoryPrefixReconciler.TryAppendCanonicalTail(history, [User("original", "original")], out _));
        Assert.IsFalse(JsonSerializer.Serialize(User("rendered", "original")).Contains("SourceContentHash", StringComparison.Ordinal));
    }

    private static ChatMessage User(string rendered, string original)
        => new(ChatRole.User, rendered) { SourceContentHash = HistoryPrefixReconciler.ComputeSourceHash(original, null) };
}
