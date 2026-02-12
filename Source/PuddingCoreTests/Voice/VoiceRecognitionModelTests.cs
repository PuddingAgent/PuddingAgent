using System.Reflection;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCoreTests.Voice;

[TestClass]
public sealed class VoiceRecognitionModelTests
{
    [TestMethod]
    public void RealtimeRequest_Represents_Browser_Microphone_As_WebSocket_Asr_Session()
    {
        var request = new VoiceRecognitionRequest
        {
            WorkspaceId = "default",
            RoomId = "room-default",
            ParticipantId = "user-owner",
            SessionId = "voice-session-1",
            Provider = VoiceRecognitionProviders.DashScope,
            Model = "qwen3-asr-flash-realtime",
            Transport = VoiceRecognitionTransports.WebSocket,
            TurnMode = VoiceRecognitionTurnModes.ServerVad,
            AudioFormat = VoiceAudioFormats.Pcm,
            SampleRate = 16_000,
            Language = "zh",
            EnablePunctuation = true,
            EnableEmotion = true,
        };

        Assert.AreEqual(VoiceRecognitionTransports.WebSocket, request.Transport);
        Assert.AreEqual(VoiceRecognitionTurnModes.ServerVad, request.TurnMode);
        Assert.AreEqual(16_000, request.SampleRate);
        Assert.IsTrue(request.EnableEmotion);

        var publicPropertyNames = typeof(VoiceRecognitionRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        CollectionAssert.DoesNotContain(publicPropertyNames, "ApiKey");
        CollectionAssert.DoesNotContain(publicPropertyNames, "Authorization");
    }

    [TestMethod]
    public void AudioFrame_Models_Browser_Captured_Bytes_Without_Leaking_Raw_Device_Details()
    {
        var frame = new VoiceAudioFrame
        {
            SessionId = "voice-session-2",
            Sequence = 3,
            AudioBytes = [1, 2, 3, 4],
            Format = VoiceAudioFormats.Pcm,
            SampleRate = 16_000,
            DurationMs = 100,
            CapturedAt = 2000,
        };

        Assert.AreEqual("voice-session-2", frame.SessionId);
        Assert.AreEqual(3, frame.Sequence);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, frame.AudioBytes);
        Assert.AreEqual(100, frame.DurationMs);
    }

    [TestMethod]
    public void TranscriptEvent_Represents_Intermediate_Final_Emotion_And_Word_Timestamps()
    {
        var transcript = VoiceRecognitionStreamEvent.Transcript(
            sessionId: "voice-session-3",
            text: "好，我知道了。",
            isFinal: true,
            sequence: 9,
            emotion: VoiceRecognitionEmotions.Neutral,
            words:
            [
                new VoiceTranscriptWord
                {
                    Text = "好",
                    BeginTimeMs = 170,
                    EndTimeMs = 295,
                    Punctuation = "，",
                },
            ]);

        Assert.AreEqual(VoiceRecognitionStreamEventTypes.Transcript, transcript.Type);
        Assert.AreEqual("好，我知道了。", transcript.Text);
        Assert.IsTrue(transcript.IsFinal);
        Assert.AreEqual(VoiceRecognitionEmotions.Neutral, transcript.Emotion);
        Assert.AreEqual(170, transcript.Words[0].BeginTimeMs);
        Assert.IsNull(transcript.ProviderRawPayload);
    }

    [TestMethod]
    public void RecognitionResult_Can_Become_Voice_Message_Metadata_For_MessageFabric()
    {
        var result = new VoiceRecognitionResult
        {
            SessionId = "voice-session-4",
            WorkspaceId = "default",
            RoomId = "room-default",
            ParticipantId = "user-owner",
            Text = "打开空调。",
            Language = "zh",
            Emotion = VoiceRecognitionEmotions.Neutral,
            CompletedAt = 3000,
            Metadata =
            {
                ["inputMode"] = "voice",
                ["asrProvider"] = VoiceRecognitionProviders.DashScope,
            },
        };

        Assert.AreEqual("打开空调。", result.Text);
        Assert.AreEqual("voice", result.Metadata["inputMode"]);
        Assert.AreEqual(VoiceRecognitionProviders.DashScope, result.Metadata["asrProvider"]);
    }

    [TestMethod]
    public void Service_And_Provider_Interfaces_Separate_Asr_Policy_From_Vendor_Protocol()
    {
        Assert.IsTrue(typeof(IVoiceRecognitionService).GetMethods().Any(method => method.Name == "StartAsync"));
        Assert.IsTrue(typeof(IVoiceRecognitionService).GetMethods().Any(method => method.Name == "RecognizeAsync"));
        Assert.IsTrue(typeof(IAsrProvider).GetMethods().Any(method => method.Name == "StartAsync"));
        Assert.IsTrue(typeof(IAsrProvider).GetMethods().Any(method => method.Name == "SendAudioAsync"));
    }
}
