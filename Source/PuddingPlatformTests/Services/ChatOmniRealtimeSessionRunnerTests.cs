using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ChatOmniRealtimeSessionRunnerTests
{
    [TestMethod]
    public async Task RunAsync_Projects_Omni_Realtime_Text_Audio_And_Status_To_Session_Frames()
    {
        var inputFrames = new[]
        {
            OmniRealtimeInputFrame.Audio("omni-session-1", [1, 2], sequence: 1, durationMs: 100),
            OmniRealtimeInputFrame.Image("omni-session-1", [3, 4], VisualMimeTypes.Jpeg, sequence: 2, width: 640, height: 480),
        }.ToAsyncEnumerable();
        var runner = CreateRunner(
            new RecordingOmniRealtimeService(
                new OmniRealtimeStreamEvent
                {
                    Type = OmniRealtimeStreamEventTypes.InputTranscriptDelta,
                    SessionId = "omni-session-1",
                    TextDelta = "你好",
                    Sequence = 1,
                    ProviderRawPayload = """{"secret":"provider-raw"}""",
                },
                new OmniRealtimeStreamEvent
                {
                    Type = OmniRealtimeStreamEventTypes.ResponseAudioTranscriptDelta,
                    SessionId = "omni-session-1",
                    TextDelta = "我在。",
                    Sequence = 2,
                },
                new OmniRealtimeStreamEvent
                {
                    Type = OmniRealtimeStreamEventTypes.ResponseAudioDelta,
                    SessionId = "omni-session-1",
                    AudioBytes = [5, 6, 7],
                    Sequence = 3,
                },
                new OmniRealtimeStreamEvent
                {
                    Type = OmniRealtimeStreamEventTypes.ResponseDone,
                    SessionId = "omni-session-1",
                    ProviderResponseId = "response-1",
                    Usage = new OmniRealtimeUsage { TotalTokens = 12, InputTokens = 5, OutputTokens = 7 },
                    Sequence = 4,
                }),
            out var service,
            out var writer);

        var result = await runner.RunAsync(new ChatOmniRealtimeSessionRunRequest
        {
            WorkspaceId = "default",
            RoomId = "room-default",
            ParticipantId = "user-owner",
            AgentId = "assistant",
            AgentDisplayName = "Default Assistant",
            ChatRequest = CreateOmniChatRequest(),
            Provider = OmniRealtimeProviders.DashScope,
            Model = "qwen3.5-omni-plus-realtime",
            Voice = "Ethan",
            SessionId = "chat-session-1",
            MessageId = "message-1",
            OmniSessionId = "omni-session-1",
            Trace = RuntimeTraceContext.CreateNew(sessionId: "chat-session-1", workspaceId: "default"),
        }, inputFrames);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("chat-session-1", result.SessionId);
        Assert.AreEqual("message-1", result.MessageId);
        Assert.AreEqual("我在。", result.Reply);
        Assert.AreEqual(2, service.InputFrameCount);
        Assert.AreEqual("qwen3.5-omni-plus-realtime", service.LastRequest?.Model);
        Assert.AreEqual("Ethan", service.LastRequest?.Voice);

        CollectionAssert.AreEqual(
            new[]
            {
                SseEventTypes.Metadata,
                SseEventTypes.VoiceCaptureStatus,
                SseEventTypes.Delta,
                SseEventTypes.VoicePlaybackStatus,
                SseEventTypes.Done,
            },
            writer.Frames.Select(frame => frame.Event).ToArray());

        var metadata = JsonDocument.Parse(writer.Frames[0].Data).RootElement;
        Assert.AreEqual("message-1", metadata.GetProperty("messageId").GetString());
        Assert.AreEqual("chat-session-1", metadata.GetProperty("sessionId").GetString());
        Assert.AreEqual("omni-session-1", metadata.GetProperty("omniSessionId").GetString());
        Assert.AreEqual("omni", metadata.GetProperty("inputMode").GetString());
        Assert.AreEqual("assistant", metadata.GetProperty("agent_id").GetString());

        var voiceCapture = JsonDocument.Parse(writer.Frames[1].Data).RootElement;
        Assert.AreEqual("message-1", voiceCapture.GetProperty("messageId").GetString());
        Assert.AreEqual("omni-session-1", voiceCapture.GetProperty("sessionId").GetString());
        Assert.AreEqual("transcribing", voiceCapture.GetProperty("status").GetString());
        Assert.AreEqual("你好", voiceCapture.GetProperty("text").GetString());
        Assert.IsFalse(voiceCapture.TryGetProperty("providerRawPayload", out _));

        var delta = JsonDocument.Parse(writer.Frames[2].Data).RootElement;
        Assert.AreEqual("message-1", delta.GetProperty("messageId").GetString());
        Assert.AreEqual("我在。", delta.GetProperty("delta").GetString());

        var playback = JsonDocument.Parse(writer.Frames[3].Data).RootElement;
        Assert.AreEqual("message-1", playback.GetProperty("messageId").GetString());
        Assert.AreEqual("buffering", playback.GetProperty("status").GetString());
        Assert.AreEqual("BQYH", playback.GetProperty("audioBase64").GetString());
        Assert.AreEqual(24000, playback.GetProperty("sampleRate").GetInt32());

        var done = JsonDocument.Parse(writer.Frames[4].Data).RootElement;
        Assert.AreEqual("message-1", done.GetProperty("messageId").GetString());
        Assert.AreEqual("我在。", done.GetProperty("reply").GetString());
        Assert.AreEqual("response-1", done.GetProperty("responseId").GetString());
        Assert.AreEqual(12, done.GetProperty("usage").GetProperty("totalTokens").GetInt32());
    }

    [TestMethod]
    public async Task RunAsync_Writes_Error_And_Playback_Failure_When_Omni_Service_Fails()
    {
        var runner = CreateRunner(
            new RecordingOmniRealtimeService(new InvalidOperationException("omni websocket closed")),
            out _,
            out var writer);

        var result = await runner.RunAsync(new ChatOmniRealtimeSessionRunRequest
        {
            WorkspaceId = "default",
            RoomId = "room-default",
            ParticipantId = "user-owner",
            AgentId = "assistant",
            ChatRequest = CreateOmniChatRequest(),
            Provider = OmniRealtimeProviders.DashScope,
            Model = "qwen3.5-omni-plus-realtime",
            SessionId = "chat-session-1",
            MessageId = "message-1",
            OmniSessionId = "omni-session-1",
        }, AsyncEnumerable.Empty<OmniRealtimeInputFrame>());

        Assert.IsFalse(result.Success);
        Assert.AreEqual("omni websocket closed", result.ErrorMessage);
        CollectionAssert.AreEqual(
            new[] { SseEventTypes.Metadata, SseEventTypes.VoicePlaybackStatus, SseEventTypes.Error },
            writer.Frames.Select(frame => frame.Event).ToArray());

        var playback = JsonDocument.Parse(writer.Frames[1].Data).RootElement;
        Assert.AreEqual("failed", playback.GetProperty("status").GetString());
        Assert.AreEqual("omni websocket closed", playback.GetProperty("error").GetString());

        var error = JsonDocument.Parse(writer.Frames[2].Data).RootElement;
        Assert.AreEqual("message-1", error.GetProperty("messageId").GetString());
        Assert.AreEqual("omni websocket closed", error.GetProperty("message").GetString());
    }

    private static ChatOmniRealtimeSessionRunner CreateRunner(
        RecordingOmniRealtimeService omniService,
        out RecordingOmniRealtimeService service,
        out RecordingSessionOutputWriter writer)
    {
        service = omniService;
        writer = new RecordingSessionOutputWriter();
        return new ChatOmniRealtimeSessionRunner(service, writer);
    }

    private static AdminChatRequest CreateOmniChatRequest() => new(
        MessageText: "实时看听并回答。",
        OriginalMessageText: null,
        SessionId: null,
        AgentId: "assistant",
        Metadata: new Dictionary<string, string>
        {
            ["inputMode"] = "omni",
        });

    private sealed class RecordingOmniRealtimeService : IOmniRealtimeService
    {
        private readonly IReadOnlyList<OmniRealtimeStreamEvent> _events;
        private readonly Exception? _exception;

        public RecordingOmniRealtimeService(params OmniRealtimeStreamEvent[] events)
        {
            _events = events;
        }

        public RecordingOmniRealtimeService(Exception exception)
        {
            _events = [];
            _exception = exception;
        }

        public OmniRealtimeSessionRequest? LastRequest { get; private set; }
        public int InputFrameCount { get; private set; }

        public async IAsyncEnumerable<OmniRealtimeStreamEvent> StartAsync(
            OmniRealtimeSessionRequest request,
            IAsyncEnumerable<OmniRealtimeInputFrame> inputFrames,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            await foreach (var _ in inputFrames.WithCancellation(ct))
            {
                InputFrameCount++;
            }

            if (_exception is not null)
                throw _exception;

            foreach (var item in _events)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingSessionOutputWriter : ISessionOutputWriter
    {
        public List<ServerSentEventFrame> Frames { get; } = [];

        public Task WriteFrameAsync(
            string sessionId,
            string workspaceId,
            ServerSentEventFrame frame,
            RuntimeTraceContext? trace = null,
            CancellationToken ct = default,
            string? component = null,
            string? operation = null)
        {
            Frames.Add(frame);
            return Task.CompletedTask;
        }
    }
}
