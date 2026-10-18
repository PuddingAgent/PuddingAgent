using System.Text;
using System.Text.Json;
using PuddingCode.Platform;
using PuddingConversationReadModel;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

/// <summary>阶段 C2：canonical 事件 → 读模型组件输入的映射合同（字段名来自生产写入点）。</summary>
[TestClass]
public sealed class ConversationReadModelEventMapperTests
{
    [TestMethod]
    [DataRow(ConversationEventTypes.TurnStarted, ConversationEventKind.TurnStarted)]
    [DataRow(ConversationEventTypes.TurnCompleted, ConversationEventKind.TurnCompleted)]
    [DataRow(ConversationEventTypes.TurnFailed, ConversationEventKind.TurnFailed)]
    [DataRow(ConversationEventTypes.TurnCancelled, ConversationEventKind.TurnCancelled)]
    [DataRow(ConversationEventTypes.MessageCompleted, ConversationEventKind.MessageCompleted)]
    [DataRow(ConversationEventTypes.MessageFailed, ConversationEventKind.MessageFailed)]
    [DataRow(ConversationEventTypes.ToolCallRequested, ConversationEventKind.ToolCallRequested)]
    [DataRow(ConversationEventTypes.ToolCallCompleted, ConversationEventKind.ToolCallCompleted)]
    [DataRow(ConversationEventTypes.ToolCallFailed, ConversationEventKind.ToolCallFailed)]
    [DataRow(ConversationEventTypes.SubAgentRunCreated, ConversationEventKind.SubAgentRunCreated)]
    [DataRow(ConversationEventTypes.SubAgentRunStarted, ConversationEventKind.SubAgentRunStarted)]
    [DataRow(ConversationEventTypes.SubAgentRunCompleted, ConversationEventKind.SubAgentRunCompleted)]
    [DataRow(ConversationEventTypes.SubAgentRunFailed, ConversationEventKind.SubAgentRunFailed)]
    [DataRow(ConversationEventTypes.SubAgentRunCancelled, ConversationEventKind.SubAgentRunCancelled)]
    [DataRow(ConversationEventTypes.SubAgentRunTimedOut, ConversationEventKind.SubAgentRunTimedOut)]
    [DataRow(ConversationEventTypes.SubAgentRunInterrupted, ConversationEventKind.SubAgentRunInterrupted)]
    [DataRow(ConversationEventTypes.SubAgentRunBudgetExhausted, ConversationEventKind.SubAgentRunBudgetExhausted)]
    [DataRow(ConversationEventTypes.ContextCompactionStarted, ConversationEventKind.ContextCompactionStarted)]
    [DataRow(ConversationEventTypes.ContextCompactionCompleted, ConversationEventKind.ContextCompactionCompleted)]
    [DataRow(ConversationEventTypes.ContextCompactionFailed, ConversationEventKind.ContextCompactionFailed)]
    public void MapsEventTypesToReadModelKinds(string eventType, ConversationEventKind expected)
    {
        var input = ConversationReadModelEventMapper.Map(Evt(eventType));

        Assert.AreEqual(expected, input.Kind);
    }

    [TestMethod]
    public void MapsContentAndThinkingDeltaFromProductionPayload()
    {
        var content = ConversationReadModelEventMapper.Map(Evt(
            ConversationEventTypes.MessageContentAppended,
            payloadJson: """{"delta":"hello"}"""));
        Assert.AreEqual(ConversationEventKind.MessageContentAppended, content.Kind);
        Assert.AreEqual("hello", content.TextDelta);

        var legacyText = ConversationReadModelEventMapper.Map(Evt(
            ConversationEventTypes.MessageContentAppended,
            payloadJson: """{"text":"legacy"}"""));
        Assert.AreEqual("legacy", legacyText.TextDelta);

        var thinking = ConversationReadModelEventMapper.Map(Evt(
            ConversationEventTypes.MessageThinkingSummaryAppended,
            payloadJson: """{"delta":"推理"}"""));
        Assert.AreEqual(ConversationEventKind.MessageThinkingSummaryAppended, thinking.Kind);
        Assert.AreEqual("推理", thinking.TextDelta);
    }

    [TestMethod]
    public void BoundsHugeTextDeltaWithoutSplittingUtf8CodePoint()
    {
        var emoji = "😀";
        var repeated = string.Concat(Enumerable.Repeat("中文" + emoji, 30_000));
        var bounded = ConversationReadModelEventMapper.BoundTextDelta(repeated);

        Assert.IsNotNull(bounded);
        Assert.IsTrue(
            Encoding.UTF8.GetByteCount(bounded) <= ConversationReadModelEventMapper.MaxTextDeltaBytes,
            "delta must be bounded before entering the pure component");
        Assert.IsFalse(bounded.EndsWith('\uFFFD'), "must not split an emoji/UTF-8 code point");
        StringAssert.StartsWith(repeated, bounded);
    }

    [TestMethod]
    public void MapsTurnCompletedReplyAsAssistantBodyPresenceEvidence()
    {
        var input = ConversationReadModelEventMapper.Map(Evt(
            ConversationEventTypes.TurnCompleted,
            messageId: "assistant-msg-1",
            payloadJson: """{"reply":"final answer"}"""));

        Assert.AreEqual(ConversationEventKind.TurnCompleted, input.Kind);
        Assert.AreEqual("final answer", input.TextDelta);
        Assert.AreEqual("assistant-msg-1", input.MessageId);
    }

    [TestMethod]
    public void MapsSubAgentLifecycleWithSubAgentIdentity()
    {
        var input = ConversationReadModelEventMapper.Map(Evt(
            ConversationEventTypes.SubAgentRunCreated,
            runId: "child-run-1",
            payloadJson: """{"sub_agent_id":"sub-1","status":"created"}"""));

        Assert.AreEqual(ConversationEventKind.SubAgentRunCreated, input.Kind);
        Assert.AreEqual("child-run-1", input.RunId);
        Assert.AreEqual("sub-1", input.SubAgentId);
        Assert.IsNull(input.ParentRunId, "lifecycle events are routed by kind, not by parent-run sentinel");
    }

    [TestMethod]
    public void SubAgentProducedContentIsMarkedAsChildRun()
    {
        var input = ConversationReadModelEventMapper.Map(Evt(
            ConversationEventTypes.MessageContentAppended,
            runId: "child-run-1",
            producerComponent: "subagent.runtime",
            payloadJson: """{"delta":"child text"}"""));

        Assert.AreEqual("child-run-1", input.ParentRunId);
        Assert.AreEqual("child text", input.TextDelta);
    }

    [TestMethod]
    [DataRow(ConversationEventTypes.TurnAccepted)]
    [DataRow(ConversationEventTypes.MessageCreated)]
    [DataRow(ConversationEventTypes.UsageRecorded)]
    [DataRow(ConversationEventTypes.RunLeaseLost)]
    [DataRow("unknown.event.type")]
    public void UnknownOrIngressEventsMapToOther(string eventType)
    {
        var input = ConversationReadModelEventMapper.Map(Evt(eventType));

        Assert.AreEqual(ConversationEventKind.Other, input.Kind);
        Assert.IsNull(input.TextDelta);
    }

    [TestMethod]
    public void EmptyEnvelopeIdentitiesBecomeNull()
    {
        var input = ConversationReadModelEventMapper.Map(Evt(
            ConversationEventTypes.TurnStarted,
            turnId: "",
            runId: "",
            messageId: ""));

        Assert.IsNull(input.TurnId);
        Assert.IsNull(input.RunId);
        Assert.IsNull(input.MessageId);
    }

    private static ConversationEvent Evt(
        string type,
        string turnId = "turn-1",
        string? runId = "run-1",
        string? messageId = "msg-1",
        string? producerComponent = null,
        string payloadJson = "{}") =>
        new()
        {
            EventId = "evt-1",
            ConversationId = "conv-1",
            Sequence = 1,
            WorkspaceId = "ws",
            TurnId = turnId,
            RunId = runId,
            MessageId = messageId,
            Type = type,
            SchemaVersion = 1,
            OccurredAt = DateTimeOffset.Parse("2026-10-18T00:00:00Z"),
            CommittedAt = DateTimeOffset.Parse("2026-10-18T00:00:00Z"),
            Payload = JsonDocument.Parse(payloadJson).RootElement.Clone(),
            ProducerComponent = producerComponent,
        };
}
