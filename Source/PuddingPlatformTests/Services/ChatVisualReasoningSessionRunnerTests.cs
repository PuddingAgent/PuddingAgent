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
public sealed class ChatVisualReasoningSessionRunnerTests
{
    [TestMethod]
    public async Task RunAsync_Projects_Visual_Reasoning_Stream_To_Chat_Sse_Frames()
    {
        var runner = CreateRunner(
            new RecordingVisualReasoningService(
                VisualReasoningStreamEvent.CreateReasoningDelta("camera-session-1", "先看图。", 1) with
                {
                    ProviderRawPayload = """{"secret":"raw-provider-payload"}""",
                },
                VisualReasoningStreamEvent.CreateAnswerDelta("camera-session-1", "答案是B", 2),
                new VisualReasoningStreamEvent
                {
                    Type = VisualReasoningStreamEventTypes.Completed,
                    SessionId = "camera-session-1",
                    ProviderRequestId = "dashscope-request-1",
                    Sequence = 3,
                }),
            out var service,
            out var writer);

        var result = await runner.RunAsync(new ChatVisualReasoningSessionRunRequest
        {
            WorkspaceId = "default",
            RoomId = "room-default",
            ParticipantId = "user-owner",
            AgentId = "assistant",
            AgentDisplayName = "Default Assistant",
            ChatRequest = CreateCameraChatRequest(),
            Provider = VisualReasoningProviders.DashScope,
            Model = "qwen3-vl-plus",
            SessionId = "chat-session-1",
            MessageId = "message-1",
            Trace = RuntimeTraceContext.CreateNew(sessionId: "chat-session-1", workspaceId: "default"),
        });

        Assert.IsTrue(result.Success);
        Assert.AreEqual("chat-session-1", result.SessionId);
        Assert.AreEqual("message-1", result.MessageId);
        Assert.AreEqual("答案是B", result.Reply);
        Assert.AreEqual("chat-session-1", service.LastRequest?.Metadata["chatSessionId"]);
        Assert.AreEqual("message-1", service.LastRequest?.Metadata["messageId"]);

        CollectionAssert.AreEqual(
            new[] { SseEventTypes.Metadata, SseEventTypes.Thinking, SseEventTypes.Delta, SseEventTypes.Done },
            writer.Frames.Select(frame => frame.Event).ToArray());

        var metadata = JsonDocument.Parse(writer.Frames[0].Data).RootElement;
        Assert.AreEqual("message-1", metadata.GetProperty("messageId").GetString());
        Assert.AreEqual("chat-session-1", metadata.GetProperty("sessionId").GetString());
        Assert.AreEqual("assistant", metadata.GetProperty("agent_id").GetString());
        Assert.AreEqual("agent", metadata.GetProperty("source_type").GetString());
        Assert.AreEqual("camera", metadata.GetProperty("inputMode").GetString());
        Assert.AreEqual("camera-session-1", metadata.GetProperty("cameraSessionId").GetString());
        Assert.AreEqual("vision-frame-1", metadata.GetProperty("visionArtifactId").GetString());

        var thinking = JsonDocument.Parse(writer.Frames[1].Data).RootElement;
        Assert.AreEqual("message-1", thinking.GetProperty("messageId").GetString());
        Assert.AreEqual("先看图。", thinking.GetProperty("delta").GetString());
        Assert.IsFalse(thinking.TryGetProperty("providerRawPayload", out _));

        var delta = JsonDocument.Parse(writer.Frames[2].Data).RootElement;
        Assert.AreEqual("message-1", delta.GetProperty("messageId").GetString());
        Assert.AreEqual("答案是B", delta.GetProperty("delta").GetString());

        var done = JsonDocument.Parse(writer.Frames[3].Data).RootElement;
        Assert.AreEqual("message-1", done.GetProperty("messageId").GetString());
        Assert.AreEqual("答案是B", done.GetProperty("reply").GetString());
        Assert.AreEqual("dashscope-request-1", done.GetProperty("requestId").GetString());
    }

    [TestMethod]
    public async Task RunAsync_Writes_Error_Frame_When_Visual_Service_Fails()
    {
        var runner = CreateRunner(
            new RecordingVisualReasoningService(new InvalidOperationException("provider unavailable")),
            out _,
            out var writer);

        var result = await runner.RunAsync(new ChatVisualReasoningSessionRunRequest
        {
            WorkspaceId = "default",
            RoomId = "room-default",
            ParticipantId = "user-owner",
            AgentId = "assistant",
            ChatRequest = CreateCameraChatRequest(),
            Provider = VisualReasoningProviders.DashScope,
            Model = "qwen3-vl-plus",
            SessionId = "chat-session-1",
            MessageId = "message-1",
        });

        Assert.IsFalse(result.Success);
        Assert.AreEqual("provider unavailable", result.ErrorMessage);
        CollectionAssert.AreEqual(
            new[] { SseEventTypes.Metadata, SseEventTypes.Error },
            writer.Frames.Select(frame => frame.Event).ToArray());

        var error = JsonDocument.Parse(writer.Frames[1].Data).RootElement;
        Assert.AreEqual("message-1", error.GetProperty("messageId").GetString());
        Assert.AreEqual("provider unavailable", error.GetProperty("message").GetString());
    }

    private static ChatVisualReasoningSessionRunner CreateRunner(
        RecordingVisualReasoningService visualService,
        out RecordingVisualReasoningService service,
        out RecordingSessionOutputWriter writer)
    {
        service = visualService;
        writer = new RecordingSessionOutputWriter();
        var resolver = new RecordingVisualArtifactReferenceResolver(new VisualArtifactReference(
            ArtifactId: "vision-frame-1",
            Uri: "https://storage.local/workspaces/default/vision-frame-1.jpg?sig=server",
            MimeType: VisualMimeTypes.Jpeg,
            Width: 1280,
            Height: 720,
            CapturedAt: 1234));
        var factory = new ChatVisualReasoningRequestFactory(resolver);
        return new ChatVisualReasoningSessionRunner(factory, service, writer);
    }

    private static AdminChatRequest CreateCameraChatRequest() => new(
        MessageText: "请分析这张图。",
        OriginalMessageText: null,
        SessionId: null,
        AgentId: "assistant",
        Metadata: new Dictionary<string, string>
        {
            ["inputMode"] = "camera",
            ["cameraSessionId"] = "camera-session-1",
            ["visionArtifactId"] = "vision-frame-1",
        });

    private sealed class RecordingVisualArtifactReferenceResolver(VisualArtifactReference? result)
        : IVisualArtifactReferenceResolver
    {
        public Task<VisualArtifactReference?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class RecordingVisualReasoningService : IVisualReasoningService
    {
        private readonly IReadOnlyList<VisualReasoningStreamEvent> _events;
        private readonly Exception? _exception;

        public RecordingVisualReasoningService(params VisualReasoningStreamEvent[] events)
        {
            _events = events;
        }

        public RecordingVisualReasoningService(Exception exception)
        {
            _events = [];
            _exception = exception;
        }

        public VisualReasoningRequest? LastRequest { get; private set; }

        public Task<VisualReasoningResult> AnalyzeAsync(
            VisualReasoningRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<VisualReasoningStreamEvent> StreamAsync(
            VisualReasoningRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
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
