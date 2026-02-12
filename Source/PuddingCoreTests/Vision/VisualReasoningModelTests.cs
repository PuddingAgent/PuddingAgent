using System.Reflection;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCoreTests.Vision;

[TestClass]
public sealed class VisualReasoningModelTests
{
    [TestMethod]
    public void Request_Represents_Camera_Or_Image_Reasoning_Without_Credentials()
    {
        var request = new VisualReasoningRequest
        {
            WorkspaceId = "default",
            RoomId = "room-default",
            ParticipantId = "user-owner",
            SessionId = "vision-session-1",
            Provider = VisualReasoningProviders.DashScope,
            Model = "qwen3-vl-plus",
            Transport = VisualReasoningTransports.Sse,
            OutputMode = VisualReasoningOutputModes.Streaming,
            ThinkingMode = VisualReasoningThinkingModes.Toggleable,
            EnableThinking = true,
            ThinkingBudgetTokens = 81920,
            Prompt = "分析这张图表并给出结论。",
            Inputs =
            [
                new VisualInputArtifact
                {
                    ArtifactId = "camera-frame-1",
                    Kind = VisualInputKinds.CameraFrame,
                    MimeType = VisualMimeTypes.Jpeg,
                    Width = 1280,
                    Height = 720,
                    CapturedAt = 4000,
                },
            ],
        };

        Assert.AreEqual(VisualReasoningTransports.Sse, request.Transport);
        Assert.AreEqual(VisualReasoningThinkingModes.Toggleable, request.ThinkingMode);
        Assert.IsTrue(request.EnableThinking);
        Assert.AreEqual(81920, request.ThinkingBudgetTokens);
        Assert.AreEqual(VisualInputKinds.CameraFrame, request.Inputs[0].Kind);

        var publicPropertyNames = typeof(VisualReasoningRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        CollectionAssert.DoesNotContain(publicPropertyNames, "ApiKey");
        CollectionAssert.DoesNotContain(publicPropertyNames, "Authorization");
    }

    [TestMethod]
    public void StreamEvents_Separate_Reasoning_From_Final_Answer_Deltas()
    {
        var reasoning = VisualReasoningStreamEvent.CreateReasoningDelta(
            sessionId: "vision-session-2",
            delta: "先观察图表坐标轴。",
            sequence: 1);

        var answer = VisualReasoningStreamEvent.CreateAnswerDelta(
            sessionId: "vision-session-2",
            delta: "结论是收入同比增长。",
            sequence: 2);

        Assert.AreEqual(VisualReasoningStreamEventTypes.ReasoningDelta, reasoning.Type);
        Assert.AreEqual("先观察图表坐标轴。", reasoning.ReasoningDelta);
        Assert.IsNull(reasoning.AnswerDelta);
        Assert.AreEqual(VisualReasoningStreamEventTypes.AnswerDelta, answer.Type);
        Assert.AreEqual("结论是收入同比增长。", answer.AnswerDelta);
        Assert.IsNull(answer.ReasoningDelta);
        Assert.IsNull(answer.ProviderRawPayload);
    }

    [TestMethod]
    public void Result_Carries_Final_Answer_Reasoning_Metadata_And_Usage()
    {
        var result = new VisualReasoningResult
        {
            SessionId = "vision-session-3",
            WorkspaceId = "default",
            RoomId = "room-default",
            ParticipantId = "user-owner",
            Answer = "B",
            ReasoningSummary = "墙上的画在图像深处，离当前位置最远。",
            Provider = VisualReasoningProviders.DashScope,
            Model = "qvq-max",
            RequestId = "req-1",
            InputTokens = 544,
            OutputTokens = 590,
            ImageTokens = 520,
            CompletedAt = 5000,
            Metadata =
            {
                ["inputMode"] = "vision",
                ["visionProvider"] = VisualReasoningProviders.DashScope,
                ["visionModel"] = "qvq-max",
            },
        };

        Assert.AreEqual("B", result.Answer);
        Assert.AreEqual("vision", result.Metadata["inputMode"]);
        Assert.AreEqual(520, result.ImageTokens);
        Assert.AreEqual(1134, result.TotalTokens);
    }

    [TestMethod]
    public void Capabilities_Represent_Qvq_Streaming_Only_And_Toggleable_QwenVl()
    {
        var capabilities = new VisualReasoningProviderCapabilities
        {
            Provider = VisualReasoningProviders.DashScope,
            SupportedModels =
            [
                new VisualReasoningModelCapability
                {
                    Model = "qvq-max",
                    ThinkingMode = VisualReasoningThinkingModes.AlwaysOn,
                    RequiresStreaming = true,
                    SupportsEnableThinking = false,
                    SupportsThinkingBudget = false,
                },
                new VisualReasoningModelCapability
                {
                    Model = "qwen3-vl-plus",
                    ThinkingMode = VisualReasoningThinkingModes.Toggleable,
                    RequiresStreaming = false,
                    SupportsEnableThinking = true,
                    SupportsThinkingBudget = true,
                },
            ],
        };

        Assert.IsTrue(capabilities.SupportedModels.Single(model => model.Model == "qvq-max").RequiresStreaming);
        Assert.IsTrue(capabilities.SupportedModels.Single(model => model.Model == "qwen3-vl-plus").SupportsEnableThinking);
    }

    [TestMethod]
    public void Service_And_Provider_Interfaces_Separate_Vision_Policy_From_Vendor_Protocol()
    {
        Assert.IsTrue(typeof(IVisualReasoningService).GetMethods().Any(method => method.Name == "AnalyzeAsync"));
        Assert.IsTrue(typeof(IVisualReasoningService).GetMethods().Any(method => method.Name == "StreamAsync"));
        Assert.IsTrue(typeof(IVisualReasoningProvider).GetMethods().Any(method => method.Name == "AnalyzeAsync"));
        Assert.IsTrue(typeof(IVisualReasoningProvider).GetMethods().Any(method => method.Name == "StreamAsync"));
    }
}
