using System.Reflection;
using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingCoreTests.Voice;

[TestClass]
public sealed class OmniRealtimeModelTests
{
    [TestMethod]
    public void Request_Represents_Unified_Audio_Image_Text_And_Audio_Output_Session()
    {
        var request = new OmniRealtimeSessionRequest
        {
            WorkspaceId = "default",
            RoomId = "room-default",
            ParticipantId = "user-owner",
            SessionId = "omni-session-1",
            Provider = OmniRealtimeProviders.DashScope,
            Model = "qwen3.5-omni-plus-realtime",
            Transport = OmniRealtimeTransports.WebSocket,
            OutputModalities = [OmniRealtimeModalities.Text, OmniRealtimeModalities.Audio],
            Voice = "Ethan",
            InputAudioFormat = VoiceAudioFormats.Pcm,
            OutputAudioFormat = VoiceAudioFormats.Pcm,
            InputSampleRate = 16_000,
            OutputSampleRate = 24_000,
            TurnMode = OmniRealtimeTurnModes.SemanticVad,
            EnableInputAudioTranscription = true,
            InputAudioTranscriptionModel = "qwen3-asr-flash-realtime",
            Instructions = "你是 Pudding 的实时多模态助手。",
            EnableSearch = true,
            EnableSearchSource = true,
        };

        CollectionAssert.Contains(request.OutputModalities.ToList(), OmniRealtimeModalities.Text);
        CollectionAssert.Contains(request.OutputModalities.ToList(), OmniRealtimeModalities.Audio);
        Assert.AreEqual(OmniRealtimeTurnModes.SemanticVad, request.TurnMode);
        Assert.AreEqual(16_000, request.InputSampleRate);
        Assert.AreEqual(24_000, request.OutputSampleRate);
        Assert.IsTrue(request.EnableInputAudioTranscription);
        Assert.IsTrue(request.EnableSearch);

        var publicPropertyNames = typeof(OmniRealtimeSessionRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        CollectionAssert.DoesNotContain(publicPropertyNames, "ApiKey");
        CollectionAssert.DoesNotContain(publicPropertyNames, "Authorization");
    }

    [TestMethod]
    public void MediaInputFrame_Models_Audio_And_Image_Appends_Without_Device_Details()
    {
        var audio = OmniRealtimeInputFrame.Audio(
            sessionId: "omni-session-2",
            audioBytes: [1, 2, 3],
            sequence: 1,
            durationMs: 100);
        var image = OmniRealtimeInputFrame.Image(
            sessionId: "omni-session-2",
            imageBytes: [4, 5, 6],
            mimeType: VisualMimeTypes.Jpeg,
            sequence: 2,
            width: 640,
            height: 480);

        Assert.AreEqual(OmniRealtimeInputKinds.Audio, audio.Kind);
        Assert.AreEqual(VoiceAudioFormats.Pcm, audio.Format);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, audio.Bytes);
        Assert.AreEqual(OmniRealtimeInputKinds.Image, image.Kind);
        Assert.AreEqual(VisualMimeTypes.Jpeg, image.MimeType);
        Assert.AreEqual(640, image.Width);
        Assert.AreEqual(480, image.Height);
    }

    [TestMethod]
    public void DashScopeEventMapper_Normalizes_Input_Transcript_Output_Text_And_Audio()
    {
        var inputDelta = DashScopeOmniRealtimeEventMapper.TryMap(
            """{"type":"conversation.item.input_audio_transcription.delta","text":"你","stash":"好"}""",
            "omni-session-3",
            1);
        var outputText = DashScopeOmniRealtimeEventMapper.TryMap(
            """{"type":"response.audio_transcript.delta","delta":"你好"}""",
            "omni-session-3",
            2);
        var outputAudio = DashScopeOmniRealtimeEventMapper.TryMap(
            """{"type":"response.audio.delta","delta":"AQIDBA=="}""",
            "omni-session-3",
            3);
        var done = DashScopeOmniRealtimeEventMapper.TryMap(
            """{"type":"response.done","response":{"id":"resp-1","usage":{"total_tokens":12,"input_tokens":5,"output_tokens":7}}}""",
            "omni-session-3",
            4);

        Assert.IsNotNull(inputDelta);
        Assert.AreEqual(OmniRealtimeStreamEventTypes.InputTranscriptDelta, inputDelta.Type);
        Assert.AreEqual("你好", inputDelta.TextDelta);

        Assert.IsNotNull(outputText);
        Assert.AreEqual(OmniRealtimeStreamEventTypes.ResponseAudioTranscriptDelta, outputText.Type);
        Assert.AreEqual("你好", outputText.TextDelta);

        Assert.IsNotNull(outputAudio);
        Assert.AreEqual(OmniRealtimeStreamEventTypes.ResponseAudioDelta, outputAudio.Type);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, outputAudio.AudioBytes);
        Assert.IsNull(outputAudio.ProviderRawPayload);

        Assert.IsNotNull(done);
        Assert.AreEqual(OmniRealtimeStreamEventTypes.ResponseDone, done.Type);
        Assert.AreEqual("resp-1", done.ProviderResponseId);
        Assert.AreEqual(12, done.Usage?.TotalTokens);
    }

    [TestMethod]
    public void Service_And_Provider_Interfaces_Model_Realtime_Multimodal_Session()
    {
        Assert.IsTrue(typeof(IOmniRealtimeService).GetMethods().Any(method => method.Name == "StartAsync"));
        Assert.IsTrue(typeof(IOmniRealtimeProvider).GetMethods().Any(method => method.Name == "StartAsync"));
        Assert.IsTrue(typeof(IOmniRealtimeProvider).GetMethods().Any(method => method.Name == "SendInputAsync"));
        Assert.IsTrue(typeof(IOmniRealtimeProvider).GetMethods().Any(method => method.Name == "CreateResponseAsync"));
    }
}
