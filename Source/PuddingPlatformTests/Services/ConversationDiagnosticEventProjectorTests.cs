using System.Text.Json;
using PuddingCode.Platform;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ConversationDiagnosticEventProjectorTests
{
    // 与实现 §6.1 常量一致（实现侧为 private，测试侧硬编码同一契约值）。
    private const int SummaryMax = 512;
    private const int ErrorMax = 1024;
    private const string Marker = "… (truncated)";

    private readonly IConversationDiagnosticEventProjector _projector =
        new ConversationDiagnosticEventProjector();

    // ═══════════════════════════════════════════════════════════
    // 状态映射（§5）
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public void MapStatus_MapsCanonicalAndUnknownTypes()
    {
        Assert.AreEqual("completed", _projector.MapStatus(ConversationEventTypes.TurnCompleted));
        Assert.AreEqual("failed", _projector.MapStatus(ConversationEventTypes.MessageFailed));
        Assert.AreEqual("recorded", _projector.MapStatus(ConversationEventTypes.UsageRecorded));
        Assert.AreEqual("recorded", _projector.MapStatus("some.unknown.type"));
    }

    [TestMethod]
    public void IsTerminalType_OnlyTrueForTerminalSet()
    {
        Assert.IsTrue(_projector.IsTerminalType(ConversationEventTypes.TurnCompleted));
        Assert.IsTrue(_projector.IsTerminalType(ConversationEventTypes.MessageFailed));
        Assert.IsTrue(_projector.IsTerminalType(ConversationEventTypes.ToolCallFailed));
        Assert.IsFalse(_projector.IsTerminalType(ConversationEventTypes.UsageRecorded));
        Assert.IsFalse(_projector.IsTerminalType(ConversationEventTypes.MessageContentAppended));
        Assert.IsFalse(_projector.IsTerminalType("some.unknown.type"));
    }

    [TestMethod]
    public void Project_SetsCompletedAt_OnlyForTerminalEvents()
    {
        var occurred = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);

        var terminal = _projector.Project(Evt(ConversationEventTypes.TurnCompleted, occurredAt: occurred));
        Assert.AreEqual(occurred, terminal.CompletedAtUtc!.Value);

        var nonTerminal = _projector.Project(Evt(ConversationEventTypes.UsageRecorded, occurredAt: occurred));
        Assert.IsNull(nonTerminal.CompletedAtUtc);
    }

    // ═══════════════════════════════════════════════════════════
    // 有界截断（§6）
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public void Project_Summary_IsTruncatedAt512_WithMarker()
    {
        var longName = new string('a', 300);
        var longError = new string('b', 300);
        var payloadJson = JsonSerializer.Serialize(new { name = longName, exitCode = 0, error = longError });
        var evt = Evt(ConversationEventTypes.ToolCallCompleted, Json(payloadJson));

        var dto = _projector.Project(evt);

        Assert.IsNotNull(dto.Summary);
        StringAssert.Contains(dto.Summary, "(truncated)");
        Assert.IsTrue(dto.Summary.Length <= SummaryMax + Marker.Length,
            $"summary length {dto.Summary.Length} exceeds bound");
    }

    [TestMethod]
    public void Project_Error_IsTruncatedAt1024_WithMarker()
    {
        var longError = new string('x', 2000);
        var payloadJson = JsonSerializer.Serialize(new { errorMessage = longError });
        var evt = Evt(ConversationEventTypes.MessageFailed, Json(payloadJson));

        var dto = _projector.Project(evt);

        Assert.IsNotNull(dto.Error);
        StringAssert.Contains(dto.Error, "(truncated)");
        Assert.IsTrue(dto.Error.Length <= ErrorMax + Marker.Length,
            $"error length {dto.Error.Length} exceeds bound");
    }

    [TestMethod]
    public void Project_SingleFieldSummary_IsBounded()
    {
        var longContent = new string('c', 1000);
        var payloadJson = JsonSerializer.Serialize(new { content = longContent });
        var evt = Evt(ConversationEventTypes.MessageContentAppended, Json(payloadJson));

        var dto = _projector.Project(evt);

        Assert.IsNotNull(dto.Summary);
        StringAssert.Contains(dto.Summary, "(truncated)");
        Assert.IsTrue(dto.Summary.Length <= SummaryMax + Marker.Length);
    }

    // ═══════════════════════════════════════════════════════════
    // 字段投影（§4）
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public void Project_MapsEnvelopeFieldsToDto()
    {
        var occurred = new DateTimeOffset(2026, 8, 15, 10, 30, 0, TimeSpan.Zero);
        var evt = Evt(
            ConversationEventTypes.TurnCompleted,
            producerComponent: "chat.acceptance",
            sourceKind: ConversationEventSourceKind.Agent,
            eventId: "evt-123",
            conversationId: "conv-456",
            traceId: "trace-789",
            runId: "run-1011",
            agentId: "agent-1213",
            turnId: "turn-1415",
            commandId: "cmd-1617",
            messageId: "msg-1819",
            correlationId: "corr-2021",
            causationId: "caus-2223",
            sequence: 42,
            schemaVersion: 2,
            occurredAt: occurred);

        var dto = _projector.Project(evt);

        Assert.AreEqual("evt-123", dto.Id);
        Assert.AreEqual("conversation_event", dto.Kind);
        Assert.AreEqual("chat.acceptance", dto.Component);
        Assert.AreEqual("turn.completed", dto.Operation);
        Assert.AreEqual("completed", dto.Status);
        Assert.AreEqual("default", dto.WorkspaceId);
        Assert.AreEqual("conv-456", dto.SessionId);
        Assert.AreEqual("trace-789", dto.TraceId);
        Assert.AreEqual("run-1011", dto.RunId);
        Assert.AreEqual("agent-1213", dto.AgentInstanceId);
        Assert.AreEqual("evt-123", dto.EventId);
        Assert.AreEqual("corr-2021", dto.CorrelationId);
        Assert.AreEqual(occurred, dto.StartedAtUtc);
        Assert.AreEqual(occurred, dto.CompletedAtUtc!.Value);
        Assert.IsNull(dto.DurationMs);

        // 补充投影表（Metadata）
        Assert.AreEqual("42", dto.Metadata["sequence"]);
        Assert.AreEqual("turn-1415", dto.Metadata["turn_id"]);
        Assert.AreEqual("cmd-1617", dto.Metadata["command_id"]);
        Assert.AreEqual("msg-1819", dto.Metadata["message_id"]);
        Assert.AreEqual("caus-2223", dto.Metadata["causation_id"]);
        Assert.AreEqual("corr-2021", dto.Metadata["correlation_id"]);
        Assert.AreEqual("agent-1213", dto.Metadata["agent_id"]);
        Assert.AreEqual("agent", dto.Metadata["source_kind"]);
        Assert.AreEqual("chat.acceptance", dto.Metadata["producer_component"]);
        Assert.AreEqual("2", dto.Metadata["schema_version"]);
    }

    [TestMethod]
    public void Project_Component_Priority_ProducerComponentThenSourceKindThenDefault()
    {
        var byProducer = _projector.Project(Evt(
            ConversationEventTypes.TurnCompleted,
            producerComponent: "chat.acceptance",
            sourceKind: ConversationEventSourceKind.Agent));
        Assert.AreEqual("chat.acceptance", byProducer.Component);

        var bySourceKind = _projector.Project(Evt(
            ConversationEventTypes.TurnCompleted,
            sourceKind: ConversationEventSourceKind.SubAgent));
        Assert.AreEqual("subagent", bySourceKind.Component);

        var byDefault = _projector.Project(Evt(ConversationEventTypes.TurnCompleted));
        Assert.AreEqual("conversation", byDefault.Component);
    }

    // ═══════════════════════════════════════════════════════════
    // 永不抛（§6.4）
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public void Project_NeverThrows_ForMalformedEmptyOrNonObjectPayload()
    {
        var payloads = new[]
        {
            default(JsonElement),                               // 畸形（ValueKind=Undefined）
            JsonDocument.Parse("{}").RootElement.Clone(),       // 空对象
            JsonDocument.Parse("[]").RootElement.Clone(),       // 数组
            JsonDocument.Parse("\"text\"").RootElement.Clone(), // 字符串
            JsonDocument.Parse("42").RootElement.Clone(),       // 数字
            JsonDocument.Parse("null").RootElement.Clone(),     // null
        };

        foreach (var payload in payloads)
        {
            var evt = Evt(ConversationEventTypes.TurnCompleted, payload);
            var dto = _projector.Project(evt);

            Assert.IsNotNull(dto);
            Assert.AreEqual("completed", dto.Status);
            Assert.IsNull(dto.Summary);
            Assert.IsNull(dto.Error);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 聚合辅助（trace-report 复用）
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public void TryProjectUsage_ParsesV2Payload()
    {
        var evt = Evt(
            ConversationEventTypes.UsageRecorded,
            Json("""{"usage":{"promptTokens":100,"completionTokens":50,"totalTokens":150},"providerId":"deepseek","modelId":"deepseek-v4-pro"}"""));

        var usage = _projector.TryProjectUsage(evt);

        Assert.IsNotNull(usage);
        Assert.AreEqual("deepseek", usage.ProviderId);
        Assert.AreEqual("deepseek-v4-pro", usage.ModelId);
        Assert.AreEqual(100L, usage.InputTokens);
        Assert.AreEqual(50L, usage.OutputTokens);
        Assert.AreEqual(150L, usage.TotalTokens);
    }

    [TestMethod]
    public void TryProjectUsage_ReturnsNull_ForNonUsageType()
    {
        var evt = Evt(ConversationEventTypes.TurnCompleted, Json("""{"reply":"hi"}"""));
        Assert.IsNull(_projector.TryProjectUsage(evt));
    }

    [TestMethod]
    public void TryProjectToolCall_ParsesFields()
    {
        var evt = Evt(
            ConversationEventTypes.ToolCallCompleted,
            Json("""{"name":"shell","toolCallId":"c1","exitCode":0,"output":"ok"}"""));

        var tool = _projector.TryProjectToolCall(evt);

        Assert.IsNotNull(tool);
        Assert.AreEqual("shell", tool.ToolName);
        Assert.AreEqual(0, tool.ExitCode);
        Assert.AreEqual("ok", tool.Output);
        Assert.IsNull(tool.Error);
    }

    [TestMethod]
    public void ExtractSubAgentId_ReadsPayload()
    {
        var evt = Evt(ConversationEventTypes.SubAgentRunCompleted, Json("""{"subAgentId":"sub-9"}"""));
        Assert.AreEqual("sub-9", _projector.ExtractSubAgentId(evt));
    }

    [TestMethod]
    public void ExtractSubAgentId_ReturnsNull_ForNonSubAgentType()
    {
        var evt = Evt(ConversationEventTypes.TurnCompleted, Json("""{"subAgentId":"sub-9"}"""));
        Assert.IsNull(_projector.ExtractSubAgentId(evt));
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    private static ConversationEvent Evt(
        string type,
        JsonElement? payload = null,
        string? producerComponent = null,
        ConversationEventSourceKind? sourceKind = null,
        string eventId = "evt-1",
        string conversationId = "conv-1",
        string? traceId = "trace-1",
        string? runId = "run-1",
        string? agentId = null,
        string turnId = "turn-1",
        string? commandId = "cmd-1",
        string? messageId = null,
        string? correlationId = null,
        string? causationId = null,
        long sequence = 1,
        int schemaVersion = 2,
        DateTimeOffset? occurredAt = null)
    {
        var occurred = occurredAt ?? new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        return new ConversationEvent
        {
            EventId = eventId,
            ConversationId = conversationId,
            Sequence = sequence,
            WorkspaceId = "default",
            TurnId = turnId,
            CommandId = commandId,
            RunId = runId,
            MessageId = messageId,
            Type = type,
            SchemaVersion = schemaVersion,
            OccurredAt = occurred,
            CommittedAt = occurred,
            CorrelationId = correlationId,
            CausationId = causationId,
            ProducerEventId = null,
            Payload = payload ?? EmptyObject(),
            AgentId = agentId,
            SourceKind = sourceKind,
            TraceId = traceId,
            ProducerComponent = producerComponent,
        };
    }

    private static JsonElement EmptyObject()
        => JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
