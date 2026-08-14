using System.Text.Json;
using PuddingCode.Platform;

namespace PuddingCoreTests.Platform;

[TestClass]
public sealed class ConversationTranscriptFoldTests
{
    private static ConversationEvent Evt(
        long sequence,
        string type,
        string payloadJson = "{}",
        string turnId = "turn-1",
        string? eventId = null,
        int schemaVersion = 1)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return new ConversationEvent
        {
            EventId = eventId ?? $"evt-{sequence}",
            ConversationId = "conv-1",
            Sequence = sequence,
            WorkspaceId = "ws-1",
            TurnId = turnId,
            Type = type,
            SchemaVersion = schemaVersion,
            OccurredAt = DateTimeOffset.UtcNow,
            CommittedAt = DateTimeOffset.UtcNow,
            Payload = doc.RootElement.Clone(),
        };
    }

    // ── message.content.appended ──────────────────────────────────

    [TestMethod]
    public void Fold_ContentAppended_AssemblesAssistantTextInSequenceOrder()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.MessageContentAppended, """{"delta":"Hello "}"""),
            Evt(2, ConversationEventTypes.MessageContentAppended, """{"delta":"world"}"""),
            Evt(3, ConversationEventTypes.MessageContentAppended, """{"delta":"!"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);

        Assert.AreEqual(1, transcript.Turns.Count);
        var turn = transcript.Turns[0];
        Assert.AreEqual("Hello world!", turn.AssistantText);
        Assert.AreEqual(1, turn.AssistantTextEvidence!.Sequence);
        Assert.AreEqual(3, turn.AssistantTextEndEvidence!.Sequence);
    }

    // ── message.thinking_summary.appended ─────────────────────────

    [TestMethod]
    public void Fold_ThinkingAppended_AssemblesThinkingSummary()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.MessageThinkingSummaryAppended, """{"delta":"分析"}"""),
            Evt(2, ConversationEventTypes.MessageThinkingSummaryAppended, """{"delta":"中..."}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);

        Assert.AreEqual("分析中...", transcript.Turns[0].ThinkingSummary);
        Assert.AreEqual(1, transcript.Turns[0].ThinkingEvidence!.Sequence);
        Assert.AreEqual(2, transcript.Turns[0].ThinkingEndEvidence!.Sequence);
    }

    // ── message.created ───────────────────────────────────────────

    [TestMethod]
    public void Fold_MessageCreated_UserRole_SetsUserText()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.MessageCreated, """{"role":"user","content":"帮我查天气"}"""),
            Evt(2, ConversationEventTypes.MessageContentAppended, """{"delta":"今天晴"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);

        Assert.AreEqual("帮我查天气", transcript.Turns[0].UserMessageText);
        Assert.AreEqual(1, transcript.Turns[0].UserMessageEvidence!.Sequence);
    }

    [TestMethod]
    public void Fold_MessageCreated_AssistantRole_DoesNotOverwriteUserText()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.MessageCreated, """{"role":"user","content":"问题"}"""),
            Evt(2, ConversationEventTypes.MessageCreated, """{"role":"assistant","content":"回答"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);

        Assert.AreEqual("问题", transcript.Turns[0].UserMessageText);
    }

    // ── tool.call.* ───────────────────────────────────────────────

    [TestMethod]
    public void Fold_ToolCall_AssociatesRequestedAndCompleted()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.ToolCallRequested,
                """{"toolCallId":"call-1","name":"file_read","arguments":"{\"path\":\"a.txt\"}"}"""),
            Evt(2, ConversationEventTypes.ToolCallCompleted,
                """{"toolCallId":"call-1","name":"file_read","exitCode":0,"output":"content"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);
        var calls = transcript.Turns[0].ToolCalls;

        Assert.AreEqual(1, calls.Count);
        var call = calls[0];
        Assert.AreEqual("call-1", call.CallId);
        Assert.AreEqual("file_read", call.Name);
        Assert.AreEqual("""{"path":"a.txt"}""", call.Arguments);
        Assert.AreEqual("content", call.Output);
        Assert.AreEqual(0, call.ExitCode);
        Assert.AreEqual(ConversationToolCallStatus.Completed, call.Status);
        Assert.AreEqual(1, call.RequestedEvidence!.Sequence);
        Assert.AreEqual(2, call.CompletedEvidence!.Sequence);
    }

    [TestMethod]
    public void Fold_ToolCall_Failed_SetsFailedStatus()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.ToolCallRequested,
                """{"toolCallId":"call-1","name":"shell","arguments":"{}"}"""),
            Evt(2, ConversationEventTypes.ToolCallFailed,
                """{"toolCallId":"call-1","name":"shell","error":"boom"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);
        var call = transcript.Turns[0].ToolCalls[0];

        Assert.AreEqual(ConversationToolCallStatus.Failed, call.Status);
        Assert.AreEqual("boom", call.Error);
    }

    [TestMethod]
    public void Fold_ToolCall_MultipleCalls_PreservesRequestOrder()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.ToolCallRequested, """{"toolCallId":"a","name":"t1"}"""),
            Evt(2, ConversationEventTypes.ToolCallRequested, """{"toolCallId":"b","name":"t2"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);
        var calls = transcript.Turns[0].ToolCalls;

        Assert.AreEqual(2, calls.Count);
        Assert.AreEqual("a", calls[0].CallId);
        Assert.AreEqual("b", calls[1].CallId);
    }

    // ── usage.recorded ────────────────────────────────────────────

    [TestMethod]
    public void Fold_UsageRecorded_AccumulatesTokens_CamelCase()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.UsageRecorded,
                """{"usage":{"promptTokens":10,"completionTokens":5,"totalTokens":15},"providerId":"p","modelId":"m"}""",
                schemaVersion: 2),
            Evt(2, ConversationEventTypes.UsageRecorded,
                """{"usage":{"promptTokens":20,"completionTokens":7,"totalTokens":27},"providerId":"p","modelId":"m"}""",
                schemaVersion: 2),
        };

        var transcript = ConversationTranscriptFold.Fold(events);
        var usage = transcript.Turns[0].Usage!;

        Assert.IsNotNull(usage);
        Assert.AreEqual(30, usage.PromptTokens);
        Assert.AreEqual(12, usage.CompletionTokens);
        Assert.AreEqual(42, usage.TotalTokens);
        Assert.AreEqual(2, usage.EventCount);
    }

    [TestMethod]
    public void Fold_UsageRecorded_TopLevelTokens_SchemaV1_Works()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.UsageRecorded,
                """{"promptTokens":3,"completionTokens":2,"totalTokens":5}""",
                schemaVersion: 1),
        };

        var transcript = ConversationTranscriptFold.Fold(events);
        var usage = transcript.Turns[0].Usage!;

        Assert.AreEqual(3, usage.PromptTokens);
        Assert.AreEqual(2, usage.CompletionTokens);
        Assert.AreEqual(5, usage.TotalTokens);
    }

    [TestMethod]
    public void Fold_UsageRecorded_TotalDerivedFromPromptPlusCompletion()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.UsageRecorded,
                """{"usage":{"promptTokens":4,"completionTokens":6}}""",
                schemaVersion: 2),
        };

        var transcript = ConversationTranscriptFold.Fold(events);
        var usage = transcript.Turns[0].Usage!;

        Assert.AreEqual(10, usage.TotalTokens);
    }

    // ── turn 终态 ─────────────────────────────────────────────────

    [TestMethod]
    public void Fold_TurnCompleted_SetsTerminalAndReply()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.MessageContentAppended, """{"delta":"hi"}"""),
            Evt(2, ConversationEventTypes.TurnCompleted,
                """{"kind":"Completed","errorCode":null,"errorMessage":null,"reply":"hi"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);
        var turn = transcript.Turns[0];

        Assert.AreEqual(TurnTerminalKind.Completed, turn.TerminalKind);
        Assert.AreEqual("hi", turn.Reply);
        Assert.AreEqual(2, turn.TerminalEvidence!.Sequence);
    }

    [TestMethod]
    public void Fold_TurnFailed_SetsErrorCodeAndMessage()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.TurnFailed,
                """{"kind":"Failed","errorCode":"execution_timeout","errorMessage":"timed out","reply":null}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);
        var turn = transcript.Turns[0];

        Assert.AreEqual(TurnTerminalKind.Failed, turn.TerminalKind);
        Assert.AreEqual("execution_timeout", turn.ErrorCode);
        Assert.AreEqual("timed out", turn.ErrorMessage);
    }

    [TestMethod]
    public void Fold_TurnCancelled_SetsCancelled()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.TurnCancelled, """{"kind":"Cancelled"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);

        Assert.AreEqual(TurnTerminalKind.Cancelled, transcript.Turns[0].TerminalKind);
    }

    [TestMethod]
    public void Fold_ReplyFallsBack_WhenNoContentDeltas()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.TurnCompleted,
                """{"kind":"Completed","errorCode":null,"errorMessage":null,"reply":"完整回复"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);

        Assert.AreEqual("完整回复", transcript.Turns[0].AssistantText);
    }

    // ── 分组与排序 ────────────────────────────────────────────────

    [TestMethod]
    public void Fold_MultipleTurns_GroupsAndOrdersByFirstSequence()
    {
        var events = new[]
        {
            Evt(3, ConversationEventTypes.MessageContentAppended, """{"delta":"B"}""", turnId: "turn-2"),
            Evt(1, ConversationEventTypes.MessageContentAppended, """{"delta":"A"}""", turnId: "turn-1"),
            Evt(4, ConversationEventTypes.MessageContentAppended, """{"delta":"B2"}""", turnId: "turn-2"),
        };

        var transcript = ConversationTranscriptFold.Fold(events);

        Assert.AreEqual(2, transcript.Turns.Count);
        Assert.AreEqual("turn-1", transcript.Turns[0].TurnId);
        Assert.AreEqual("A", transcript.Turns[0].AssistantText);
        Assert.AreEqual("turn-2", transcript.Turns[1].TurnId);
        Assert.AreEqual("BB2", transcript.Turns[1].AssistantText);
    }

    [TestMethod]
    public void Fold_OutOfOrderInput_SortsBySequence()
    {
        var events = new[]
        {
            Evt(3, ConversationEventTypes.MessageContentAppended, """{"delta":"c"}"""),
            Evt(1, ConversationEventTypes.MessageContentAppended, """{"delta":"a"}"""),
            Evt(2, ConversationEventTypes.MessageContentAppended, """{"delta":"b"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);

        Assert.AreEqual("abc", transcript.Turns[0].AssistantText);
    }

    [TestMethod]
    public void Fold_EmptyInput_ReturnsEmptyTranscript()
    {
        var transcript = ConversationTranscriptFold.Fold(Array.Empty<ConversationEvent>());

        Assert.AreEqual(string.Empty, transcript.ConversationId);
        Assert.AreEqual(0, transcript.Turns.Count);
    }

    // ── 缺失字段防御 ──────────────────────────────────────────────

    [TestMethod]
    public void Fold_MissingFields_DoesNotThrow_AndIsNullSafe()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.MessageContentAppended, "{}"),
            Evt(2, ConversationEventTypes.MessageThinkingSummaryAppended, "{}"),
            Evt(3, ConversationEventTypes.ToolCallRequested, "{}"),
            Evt(4, ConversationEventTypes.UsageRecorded, "{}", schemaVersion: 2),
            Evt(5, ConversationEventTypes.TurnCompleted, "{}"),
        };

        var transcript = ConversationTranscriptFold.Fold(events);
        var turn = transcript.Turns[0];

        Assert.IsNull(turn.AssistantText);
        Assert.IsNull(turn.ThinkingSummary);
        Assert.AreEqual(1, turn.ToolCalls.Count);
        Assert.IsNull(turn.Usage);
        Assert.AreEqual(TurnTerminalKind.Completed, turn.TerminalKind);
    }

    [TestMethod]
    public void Fold_NonObjectPayload_DoesNotThrow()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.MessageContentAppended, "\"just a string\""),
            Evt(2, ConversationEventTypes.MessageContentAppended, "123"),
            Evt(3, ConversationEventTypes.MessageContentAppended, "null"),
        };

        var transcript = ConversationTranscriptFold.Fold(events);

        Assert.AreEqual(1, transcript.Turns.Count);
        Assert.IsNull(transcript.Turns[0].AssistantText);
    }

    [TestMethod]
    public void Fold_ToolCallCompletedWithoutRequested_CreatesStandaloneCall()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.ToolCallCompleted,
                """{"toolCallId":"x","name":"t","exitCode":0,"output":"ok"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);
        var call = transcript.Turns[0].ToolCalls[0];

        Assert.AreEqual("x", call.CallId);
        Assert.AreEqual(ConversationToolCallStatus.Completed, call.Status);
        Assert.AreEqual("ok", call.Output);
    }

    [TestMethod]
    public void Fold_IgnoredEventTypes_AreSkipped()
    {
        var events = new[]
        {
            Evt(1, ConversationEventTypes.TurnAccepted, """{"foo":"bar"}"""),
            Evt(2, ConversationEventTypes.TurnStarted, """{"commandId":"c","turnId":"t","runId":"r"}"""),
            Evt(3, ConversationEventTypes.MessageContentAppended, """{"delta":"x"}"""),
        };

        var transcript = ConversationTranscriptFold.Fold(events);
        var turn = transcript.Turns[0];

        Assert.AreEqual("x", turn.AssistantText);
        Assert.IsNull(turn.TerminalKind);
    }
}
