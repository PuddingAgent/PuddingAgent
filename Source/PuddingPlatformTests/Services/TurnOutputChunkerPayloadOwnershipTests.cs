using System.Text.Json;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class TurnOutputChunkerPayloadOwnershipTests
{
    [TestMethod]
    public void Feed_NonDeltaEvent_OwnsPayloadAfterProducerDocumentIsDisposed()
    {
        IReadOnlyList<NewConversationEvent> events;
        using (var document = JsonDocument.Parse("""{"value":"persisted"}"""))
        {
            events = new TurnOutputChunker().Feed(
                RuntimeEvent(ConversationEventTypes.ToolCallCompleted, document.RootElement),
                "conversation",
                "workspace",
                "turn",
                "command",
                "run",
                null);
        }

        Assert.AreEqual("persisted", events.Single().Payload.GetProperty("value").GetString());
    }

    [TestMethod]
    public void Feed_DeltaFlush_OwnsGeneratedPayload()
    {
        IReadOnlyList<NewConversationEvent> events;
        using (var document = JsonDocument.Parse("""{"delta":"OK"}"""))
        {
            events = new TurnOutputChunker(maxBatchBytes: 1).Feed(
                RuntimeEvent(ConversationEventTypes.MessageContentAppended, document.RootElement),
                "conversation",
                "workspace",
                "turn",
                "command",
                "run",
                null);
        }

        Assert.AreEqual("OK", events.Single().Payload.GetProperty("delta").GetString());
    }

    [TestMethod]
    public void Feed_NonDeltaEvent_PreservesRuntimeSchemaVersion()
    {
        using var document = JsonDocument.Parse("""{"usage":{"promptTokens":42}}""");

        var events = new TurnOutputChunker().Feed(
            RuntimeEvent(
                ConversationEventTypes.UsageRecorded,
                document.RootElement,
                schemaVersion: 2),
            "conversation",
            "workspace",
            "turn",
            "command",
            "run",
            null);

        Assert.AreEqual(2, events.Single().SchemaVersion);
    }

    [TestMethod]
    public void Feed_NonDeltaEvent_CarriesTraceIdAndProducerComponent()
    {
        using var document = JsonDocument.Parse("""{"value":"persisted"}""");

        var events = new TurnOutputChunker().Feed(
            RuntimeEvent(ConversationEventTypes.ToolCallCompleted, document.RootElement),
            "conversation",
            "workspace",
            "turn",
            "command",
            "run",
            null,
            "trace-c1-0001");

        var e = events.Single();
        Assert.AreEqual("trace-c1-0001", e.TraceId);
        Assert.AreEqual("execution.journal", e.ProducerComponent);
    }

    [TestMethod]
    public void Feed_DeltaFlush_CarriesTraceIdAndProducerComponent()
    {
        using var document = JsonDocument.Parse("""{"delta":"OK"}""");

        var events = new TurnOutputChunker(maxBatchBytes: 1).Feed(
            RuntimeEvent(ConversationEventTypes.MessageContentAppended, document.RootElement),
            "conversation",
            "workspace",
            "turn",
            "command",
            "run",
            null,
            "trace-c1-0002");

        var e = events.Single();
        Assert.AreEqual("trace-c1-0002", e.TraceId);
        Assert.AreEqual("execution.journal", e.ProducerComponent);
    }

    [TestMethod]
    public void Feed_NonDeltaEvent_FlushesPendingContentBeforeToolEvent_PreservingInterleaveOrder()
    {
        // 交错保序：轮内文本先于其后的工具事件持久化。
        // 旧实现把正文留在缓冲里，跨轮文本被合并成一个排在工具之后的分块，
        // canonical sequence 丢失「文本 → 工具 → 文本」的轮次边界。
        var chunker = new TurnOutputChunker(maxBatchMs: int.MaxValue, maxBatchBytes: int.MaxValue);

        var first = chunker.Feed(
            RuntimeEvent(ConversationEventTypes.MessageContentAppended, Delta("文本1")),
            "conversation", "workspace", "turn", "command", "run", null);
        Assert.AreEqual(0, first.Count); // 低于阈值，留在缓冲

        var second = chunker.Feed(
            RuntimeEvent(ConversationEventTypes.ToolCallRequested, JsonDocument.Parse("""{"name":"shell"}""").RootElement),
            "conversation", "workspace", "turn", "command", "run", null);

        Assert.AreEqual(2, second.Count);
        Assert.AreEqual(ConversationEventTypes.MessageContentAppended, second[0].Type);
        Assert.AreEqual("文本1", second[0].Payload.GetProperty("delta").GetString());
        Assert.AreEqual(ConversationEventTypes.ToolCallRequested, second[1].Type);

        // 第二轮文本在工具之后进入新分块（不与第一轮合并）
        var third = chunker.Feed(
            RuntimeEvent(ConversationEventTypes.MessageContentAppended, Delta("文本2")),
            "conversation", "workspace", "turn", "command", "run", null);
        Assert.AreEqual(0, third.Count);
        var tail = chunker.Flush("conversation", "workspace", "turn", "command", "run", null);
        Assert.AreEqual(1, tail.Count);
        Assert.AreEqual("文本2", tail[0].Payload.GetProperty("delta").GetString());
    }

    [TestMethod]
    public void Feed_NonDeltaEvent_FlushesPendingThinkingBeforeToolEvent()
    {
        var chunker = new TurnOutputChunker(maxBatchMs: int.MaxValue, maxBatchBytes: int.MaxValue);

        chunker.Feed(
            RuntimeEvent(ConversationEventTypes.MessageThinkingSummaryAppended, Delta("推理1")),
            "conversation", "workspace", "turn", "command", "run", null);

        var events = chunker.Feed(
            RuntimeEvent(ConversationEventTypes.ToolCallRequested, JsonDocument.Parse("""{"name":"shell"}""").RootElement),
            "conversation", "workspace", "turn", "command", "run", null);

        Assert.AreEqual(2, events.Count);
        Assert.AreEqual(ConversationEventTypes.MessageThinkingSummaryAppended, events[0].Type);
        Assert.AreEqual(ConversationEventTypes.ToolCallRequested, events[1].Type);
    }

    private static JsonElement Delta(string text)
    {
        using var document = JsonDocument.Parse(
            $$"""{"delta":{{JsonSerializer.Serialize(text)}}}""");
        return document.RootElement.Clone();
    }

    private static TurnExecutionEvent RuntimeEvent(
        string type,
        JsonElement payload,
        int schemaVersion = 1) =>
        new(
            ProducerEventId: Guid.NewGuid().ToString("N"),
            Type: type,
            SchemaVersion: schemaVersion,
            Payload: payload,
            IsTerminal: false,
            TerminalInfo: null);
}
