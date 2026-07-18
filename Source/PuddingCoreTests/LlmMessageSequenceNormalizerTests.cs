using PuddingCode.Models;

namespace PuddingCoreTests;

[TestClass]
public sealed class LlmMessageSequenceNormalizerTests
{
    [TestMethod]
    public void Normalize_PreservesCompleteToolRound()
    {
        var source = new List<ChatMessage>
        {
            new(ChatRole.User, "inspect"),
            new(
                ChatRole.Assistant,
                null,
                ToolCalls:
                [
                    new ToolCall("call-1", "first", "{}"),
                    new ToolCall("call-2", "second", "{}"),
                ]),
            new(ChatRole.Tool, "one", ToolCallId: "call-1"),
            new(ChatRole.Tool, "two", ToolCallId: "call-2"),
            new(ChatRole.Assistant, "done"),
        };

        var result = LlmMessageSequenceNormalizer.Normalize(source);

        Assert.IsFalse(result.Changed);
        CollectionAssert.AreEqual(source, result.Messages.ToList());
    }

    [TestMethod]
    public void Normalize_DowngradesAssistantAndDropsPartialResults_WhenToolRoundIsIncomplete()
    {
        var result = LlmMessageSequenceNormalizer.Normalize(
        [
            new ChatMessage(ChatRole.User, "inspect"),
            new ChatMessage(
                ChatRole.Assistant,
                "I will inspect.",
                ToolCalls:
                [
                    new ToolCall("call-1", "first", "{}"),
                    new ToolCall("call-2", "second", "{}"),
                ]),
            new ChatMessage(ChatRole.Tool, "one", ToolCallId: "call-1"),
            new ChatMessage(ChatRole.User, "next"),
        ]);

        Assert.IsTrue(result.Changed);
        Assert.AreEqual(1, result.RepairedIncompleteToolRounds);
        Assert.AreEqual(1, result.DowngradedAssistantMessages);
        Assert.AreEqual(0, result.Messages.Count(message => message.Role == ChatRole.Tool));

        var assistant = result.Messages.Single(message => message.Role == ChatRole.Assistant);
        Assert.AreEqual("I will inspect.", assistant.Content);
        Assert.IsNull(assistant.ToolCalls);
    }

    [TestMethod]
    public void Normalize_RemovesEmptyIncompleteAssistantAndOrphanTools()
    {
        var result = LlmMessageSequenceNormalizer.Normalize(
        [
            new ChatMessage(
                ChatRole.Assistant,
                null,
                ToolCalls: [new ToolCall("call-1", "first", "{}")]),
            new ChatMessage(ChatRole.Tool, "wrong", ToolCallId: "call-other"),
            new ChatMessage(ChatRole.Tool, "orphan", ToolCallId: "call-orphan"),
            new ChatMessage(ChatRole.User, "safe"),
        ]);

        Assert.IsTrue(result.Changed);
        Assert.AreEqual(1, result.RepairedIncompleteToolRounds);
        Assert.AreEqual(2, result.DroppedOrphanToolMessages);
        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual("safe", result.Messages[0].Content);
    }
}
