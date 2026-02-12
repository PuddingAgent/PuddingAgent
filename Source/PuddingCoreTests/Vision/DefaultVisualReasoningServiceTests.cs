namespace PuddingCoreTests.Vision;

[TestClass]
public sealed class DefaultVisualReasoningServiceTests
{
    [TestMethod]
    public async Task AnalyzeAsync_Routes_To_Explicit_Provider_And_Model()
    {
        var dashScope = new RecordingVisualReasoningProvider(
            VisualReasoningProviders.DashScope,
            ["qwen3-vl-plus"]);
        var local = new RecordingVisualReasoningProvider(
            VisualReasoningProviders.Local,
            ["local-vl"]);
        var service = new DefaultVisualReasoningService([local, dashScope]);

        var result = await service.AnalyzeAsync(CreateRequest(
            provider: VisualReasoningProviders.DashScope,
            model: "qwen3-vl-plus"));

        Assert.AreEqual("answer from dashscope", result.Answer);
        Assert.AreEqual(1, dashScope.AnalyzeCalls);
        Assert.AreEqual(0, local.AnalyzeCalls);
        Assert.AreEqual("vision-session-service", dashScope.LastAnalyzeRequest?.SessionId);
    }

    [TestMethod]
    public async Task StreamAsync_Routes_By_Model_When_Request_Provider_Is_Unknown()
    {
        var dashScope = new RecordingVisualReasoningProvider(
            VisualReasoningProviders.DashScope,
            ["qwen3-vl-plus"]);
        var service = new DefaultVisualReasoningService([dashScope]);

        var events = new List<VisualReasoningStreamEvent>();
        await foreach (var item in service.StreamAsync(CreateRequest(
            provider: VisualReasoningProviders.Unknown,
            model: "qwen3-vl-plus")))
        {
            events.Add(item);
        }

        Assert.AreEqual(1, dashScope.StreamCalls);
        Assert.AreEqual(VisualReasoningStreamEventTypes.ReasoningDelta, events[0].Type);
        Assert.AreEqual("thinking", events[0].ReasoningDelta);
        Assert.AreEqual(VisualReasoningStreamEventTypes.AnswerDelta, events[1].Type);
        Assert.AreEqual("answer", events[1].AnswerDelta);
    }

    [TestMethod]
    public async Task AnalyzeAsync_Rejects_Unresolved_Visual_Artifacts_Before_Provider_Call()
    {
        var dashScope = new RecordingVisualReasoningProvider(
            VisualReasoningProviders.DashScope,
            ["qwen3-vl-plus"]);
        var service = new DefaultVisualReasoningService([dashScope]);

        var ex = await ThrowsInvalidOperationAsync(() =>
            service.AnalyzeAsync(CreateRequest(
                provider: VisualReasoningProviders.DashScope,
                model: "qwen3-vl-plus",
                uri: null)));

        StringAssert.Contains(ex.Message, "resolved URI");
        Assert.AreEqual(0, dashScope.AnalyzeCalls);
    }

    [TestMethod]
    public async Task AnalyzeAsync_Fails_When_No_Provider_Can_Handle_Model()
    {
        var service = new DefaultVisualReasoningService([
            new RecordingVisualReasoningProvider(VisualReasoningProviders.Local, ["local-vl"]),
        ]);

        var ex = await ThrowsInvalidOperationAsync(() =>
            service.AnalyzeAsync(CreateRequest(
                provider: VisualReasoningProviders.DashScope,
                model: "qwen3-vl-plus")));

        StringAssert.Contains(ex.Message, "No visual reasoning provider");
    }

    private static VisualReasoningRequest CreateRequest(
        string provider,
        string model,
        string? uri = "https://example.test/frame.jpg") => new()
    {
        WorkspaceId = "default",
        RoomId = "room-default",
        ParticipantId = "user-owner",
        SessionId = "vision-session-service",
        Provider = provider,
        Model = model,
        Transport = VisualReasoningTransports.OpenAiCompatibleSse,
        OutputMode = VisualReasoningOutputModes.Streaming,
        ThinkingMode = VisualReasoningThinkingModes.Toggleable,
        EnableThinking = true,
        ThinkingBudgetTokens = 4096,
        Prompt = "请分析这张图。",
        Inputs =
        [
            new VisualInputArtifact
            {
                ArtifactId = "frame-1",
                Kind = VisualInputKinds.ImageUrl,
                MimeType = VisualMimeTypes.Jpeg,
                Uri = uri,
                CapturedAt = 1234,
            },
        ],
    };

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

    private sealed class RecordingVisualReasoningProvider(
        string provider,
        IReadOnlyList<string> models) : IVisualReasoningProvider
    {
        public int AnalyzeCalls { get; private set; }
        public int StreamCalls { get; private set; }
        public VisualReasoningRequest? LastAnalyzeRequest { get; private set; }

        public VisualReasoningProviderCapabilities Capabilities { get; } = new()
        {
            Provider = provider,
            SupportedTransports = [VisualReasoningTransports.OpenAiCompatibleSse],
            SupportedModels = models.Select(model => new VisualReasoningModelCapability
            {
                Model = model,
                ThinkingMode = VisualReasoningThinkingModes.Toggleable,
                SupportsEnableThinking = true,
                SupportsThinkingBudget = true,
                SupportedInputKinds = [VisualInputKinds.ImageUrl],
            }).ToList(),
        };

        public Task<VisualReasoningResult> AnalyzeAsync(VisualReasoningRequest request, CancellationToken ct = default)
        {
            AnalyzeCalls++;
            LastAnalyzeRequest = request;
            return Task.FromResult(new VisualReasoningResult
            {
                SessionId = request.SessionId,
                WorkspaceId = request.WorkspaceId,
                RoomId = request.RoomId,
                ParticipantId = request.ParticipantId,
                Answer = $"answer from {provider}",
                Provider = provider,
                Model = request.Model,
            });
        }

        public async IAsyncEnumerable<VisualReasoningStreamEvent> StreamAsync(
            VisualReasoningRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamCalls++;
            await Task.Yield();
            yield return VisualReasoningStreamEvent.CreateReasoningDelta(request.SessionId, "thinking", 0);
            yield return VisualReasoningStreamEvent.CreateAnswerDelta(request.SessionId, "answer", 1);
        }
    }
}
