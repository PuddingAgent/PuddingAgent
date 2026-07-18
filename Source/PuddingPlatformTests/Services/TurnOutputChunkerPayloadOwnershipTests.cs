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
