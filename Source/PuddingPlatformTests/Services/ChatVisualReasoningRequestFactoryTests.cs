using PuddingCode.Models;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ChatVisualReasoningRequestFactoryTests
{
    [TestMethod]
    public async Task BuildAsync_Resolves_Camera_Metadata_To_Controlled_Visual_Request()
    {
        var resolver = new RecordingVisualArtifactReferenceResolver(new VisualArtifactReference(
            ArtifactId: "vision-frame-1",
            Uri: "https://storage.local/workspaces/default/vision-frame-1.jpg?sig=server",
            MimeType: VisualMimeTypes.Jpeg,
            Width: 1280,
            Height: 720,
            CapturedAt: 1234));
        var factory = new ChatVisualReasoningRequestFactory(resolver);
        var request = CreateCameraChatRequest(metadata: new Dictionary<string, string>
        {
            ["inputMode"] = "camera",
            ["cameraSessionId"] = "camera-session-1",
            ["visionArtifactId"] = "vision-frame-1",
            ["visionArtifactUri"] = "https://client.example/untrusted.jpg",
            ["width"] = "9999",
            ["height"] = "9999",
        });

        var visualRequest = await factory.BuildAsync(
            workspaceId: "default",
            roomId: "room-default",
            participantId: "user-owner",
            chatRequest: request,
            provider: VisualReasoningProviders.DashScope,
            model: "qwen3-vl-plus",
            traceId: "trace-1");

        Assert.AreEqual("default", resolver.LastWorkspaceId);
        Assert.AreEqual("vision-frame-1", resolver.LastArtifactId);
        Assert.AreEqual("请分析这张图。", visualRequest.Prompt);
        Assert.AreEqual("camera-session-1", visualRequest.SessionId);
        Assert.AreEqual(VisualReasoningProviders.DashScope, visualRequest.Provider);
        Assert.AreEqual("qwen3-vl-plus", visualRequest.Model);
        Assert.IsTrue(visualRequest.EnableThinking);
        Assert.AreEqual(VisualReasoningTransports.OpenAiCompatibleSse, visualRequest.Transport);

        var input = visualRequest.Inputs.Single();
        Assert.AreEqual(VisualInputKinds.CameraFrame, input.Kind);
        Assert.AreEqual("vision-frame-1", input.ArtifactId);
        Assert.AreEqual("https://storage.local/workspaces/default/vision-frame-1.jpg?sig=server", input.Uri);
        Assert.AreEqual(1280, input.Width);
        Assert.AreEqual(720, input.Height);
        Assert.AreEqual(1234, input.CapturedAt);
        Assert.AreEqual("camera", visualRequest.Metadata["inputMode"]);
        Assert.AreEqual("camera-session-1", visualRequest.Metadata["cameraSessionId"]);
        Assert.IsFalse(visualRequest.Metadata.ContainsKey("visionArtifactUri"));
    }

    [TestMethod]
    public async Task BuildAsync_Rejects_Camera_Request_When_Artifact_Cannot_Be_Resolved()
    {
        var factory = new ChatVisualReasoningRequestFactory(new RecordingVisualArtifactReferenceResolver(null));

        var ex = await ThrowsInvalidOperationAsync(() =>
            factory.BuildAsync(
                workspaceId: "default",
                roomId: "room-default",
                participantId: "user-owner",
                chatRequest: CreateCameraChatRequest(),
                provider: VisualReasoningProviders.DashScope,
                model: "qwen3-vl-plus",
                traceId: null));

        StringAssert.Contains(ex.Message, "vision-frame-1");
    }

    [TestMethod]
    public async Task BuildAsync_Rejects_Non_Camera_Message()
    {
        var factory = new ChatVisualReasoningRequestFactory(new RecordingVisualArtifactReferenceResolver(null));
        var chatRequest = new AdminChatRequest(
            MessageText: "普通文本",
            OriginalMessageText: null,
            SessionId: null,
            AgentId: "assistant",
            Metadata: new Dictionary<string, string>
            {
                ["inputMode"] = "text",
            });

        var ex = await ThrowsInvalidOperationAsync(() =>
            factory.BuildAsync(
                workspaceId: "default",
                roomId: "room-default",
                participantId: "user-owner",
                chatRequest: chatRequest,
                provider: VisualReasoningProviders.DashScope,
                model: "qwen3-vl-plus",
                traceId: null));

        StringAssert.Contains(ex.Message, "camera");
    }

    private static AdminChatRequest CreateCameraChatRequest(
        Dictionary<string, string>? metadata = null) => new(
        MessageText: "请分析这张图。",
        OriginalMessageText: null,
        SessionId: null,
        AgentId: "assistant",
        Metadata: metadata ?? new Dictionary<string, string>
        {
            ["inputMode"] = "camera",
            ["cameraSessionId"] = "camera-session-1",
            ["visionArtifactId"] = "vision-frame-1",
        });

    private static async Task<InvalidOperationException> ThrowsInvalidOperationAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        Assert.Fail("Expected InvalidOperationException.");
        throw new InvalidOperationException("unreachable");
    }

    private sealed class RecordingVisualArtifactReferenceResolver(VisualArtifactReference? result)
        : IVisualArtifactReferenceResolver
    {
        public string? LastWorkspaceId { get; private set; }
        public string? LastArtifactId { get; private set; }

        public Task<VisualArtifactReference?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default)
        {
            LastWorkspaceId = workspaceId;
            LastArtifactId = artifactId;
            return Task.FromResult(result);
        }
    }
}
