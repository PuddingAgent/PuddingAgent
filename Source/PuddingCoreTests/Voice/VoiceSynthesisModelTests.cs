using System.Reflection;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCoreTests.Voice;

[TestClass]
public sealed class VoiceSynthesisModelTests
{
    [TestMethod]
    public void RealtimeRequest_Represents_Qwen_WebSocket_Commit_Mode_Without_Exposing_ApiKey()
    {
        var request = new VoiceSynthesisRequest
        {
            WorkspaceId = "default",
            MessageId = "msg-1",
            DeliveryId = "delivery-1",
            Text = "你好，Pudding。",
            Provider = VoiceSynthesisProviders.DashScope,
            Model = "qwen3-tts-flash-realtime",
            Voice = "Cherry",
            LanguageType = "Chinese",
            Transport = VoiceSynthesisTransports.WebSocket,
            OutputMode = VoiceSynthesisOutputModes.RealtimeDuplex,
            SessionMode = VoiceSynthesisSessionModes.Commit,
            AudioFormat = VoiceAudioFormats.Pcm,
            SampleRate = 24_000,
        };

        Assert.AreEqual(VoiceSynthesisTransports.WebSocket, request.Transport);
        Assert.AreEqual(VoiceSynthesisSessionModes.Commit, request.SessionMode);
        Assert.AreEqual(VoiceSynthesisOutputModes.RealtimeDuplex, request.OutputMode);
        Assert.AreEqual(24_000, request.SampleRate);

        var publicPropertyNames = typeof(VoiceSynthesisRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        CollectionAssert.DoesNotContain(publicPropertyNames, "ApiKey");
        CollectionAssert.DoesNotContain(publicPropertyNames, "Authorization");
    }

    [TestMethod]
    public void ProviderCapabilities_Model_Http_Sse_And_WebSocket_As_FirstClass_Transports()
    {
        var capabilities = new VoiceSynthesisProviderCapabilities
        {
            Provider = VoiceSynthesisProviders.DashScope,
            SupportedTransports =
            [
                VoiceSynthesisTransports.Http,
                VoiceSynthesisTransports.Sse,
                VoiceSynthesisTransports.WebSocket,
            ],
            SupportedSessionModes =
            [
                VoiceSynthesisSessionModes.SingleTurn,
                VoiceSynthesisSessionModes.ServerCommit,
                VoiceSynthesisSessionModes.Commit,
            ],
            SupportsConnectionReuse = true,
            RequiresServerSideCredential = true,
            MaxConnectionIdleSeconds = 60,
        };

        CollectionAssert.Contains(capabilities.SupportedTransports.ToList(), VoiceSynthesisTransports.WebSocket);
        CollectionAssert.Contains(capabilities.SupportedSessionModes.ToList(), VoiceSynthesisSessionModes.ServerCommit);
        CollectionAssert.Contains(capabilities.SupportedSessionModes.ToList(), VoiceSynthesisSessionModes.Commit);
        Assert.IsTrue(capabilities.SupportsConnectionReuse);
        Assert.IsTrue(capabilities.RequiresServerSideCredential);
        Assert.AreEqual(60, capabilities.MaxConnectionIdleSeconds);
    }

    [TestMethod]
    public void StreamEvent_Exposes_Decoded_Audio_Delta_Instead_Of_Provider_Raw_Response()
    {
        var audio = new byte[] { 1, 2, 3, 4 };
        var streamEvent = VoiceSynthesisStreamEvent.AudioDelta(
            messageId: "msg-2",
            deliveryId: "delivery-2",
            audioBytes: audio,
            format: VoiceAudioFormats.Pcm,
            sampleRate: 24_000,
            sequence: 7);

        Assert.AreEqual(VoiceSynthesisStreamEventTypes.AudioDelta, streamEvent.Type);
        CollectionAssert.AreEqual(audio, streamEvent.AudioBytes);
        Assert.AreEqual(VoiceAudioFormats.Pcm, streamEvent.Format);
        Assert.AreEqual(24_000, streamEvent.SampleRate);
        Assert.AreEqual(7, streamEvent.Sequence);
        Assert.IsNull(streamEvent.ProviderRawPayload);
    }

    [TestMethod]
    public void Service_And_Provider_Interfaces_Separate_Business_Policy_From_Vendor_Protocol()
    {
        Assert.IsTrue(typeof(IVoiceSynthesisService).GetMethods().Any(method => method.Name == "SynthesizeAsync"));
        Assert.IsTrue(typeof(IVoiceSynthesisService).GetMethods().Any(method => method.Name == "StreamAsync"));
        Assert.IsTrue(typeof(ITtsProvider).GetMethods().Any(method => method.Name == "SynthesizeAsync"));
        Assert.IsTrue(typeof(ITtsProvider).GetMethods().Any(method => method.Name == "StreamAsync"));
    }
}
