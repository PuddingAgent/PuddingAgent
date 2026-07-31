using System.Net;
using System.Text;
using HarnessAgent.Core.Connectors.Feishu;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Connectors;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingPlatform.Services;
using PuddingRuntime.Services;

namespace PuddingAgent.IntegrationTests.Feishu;

[TestClass]
public sealed class FeishuInboundAudioTests
{
    [TestMethod]
    public async Task AudioEvent_DownloadsOnceAndMaterializesProviderSafeWavArtifact()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-feishu-audio-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var audioStorage = new AudioArtifactStorageService(
                paths,
                NullLogger<AudioArtifactStorageService>.Instance);
            var transcoder = new ManagedOggOpusTranscoder();
            var mapper = new FeishuInboundMessageMapper(
                new VisionArtifactStorageService(
                    paths,
                    NullLogger<VisionArtifactStorageService>.Instance),
                audioStorage,
                transcoder,
                NullLogger<FeishuInboundMessageMapper>.Instance);
            var opus = await transcoder.TranscodeAsync(
                new AudioTranscodingRequest
                {
                    Content = CreateStereoPcm16Wav(
                        sampleRate: 24_000,
                        duration: TimeSpan.FromMilliseconds(500)),
                    SourceFormat = VoiceAudioFormats.Wav,
                    TargetFormat = VoiceAudioFormats.Opus,
                    TargetSampleRate = 16_000,
                    TargetChannels = 1,
                });
            var handler = new FeishuAudioHandler(opus.Content);
            using var http = new HttpClient(handler);
            using var client = new FeishuClient(
                new FeishuConfig
                {
                    AppId = "app_test",
                    AppSecret = "secret_test",
                },
                http);
            var binding = new FeishuConnectorBinding(
                "agent-test",
                "default",
                "app_test",
                "secret_test",
                null,
                TtsRepliesEnabled: true,
                TtsVoice: "Stella");
            var evt = CreateAudioEvent();

            var envelope = await mapper.MapAsync(
                binding,
                "feishu:agent-test",
                evt,
                client);
            var retried = await mapper.MapAsync(
                binding,
                "feishu:agent-test",
                evt,
                client);

            Assert.AreEqual("audio", envelope.MessageType);
            Assert.AreEqual(
                "用户从飞书发送了一条语音消息。",
                envelope.MessageText);
            Assert.AreEqual("audio", envelope.Metadata["inputMode"]);
            var artifactId = envelope.Metadata["audioArtifactId"];
            Assert.AreEqual(artifactId, envelope.Metadata["audioArtifactIds"]);
            Assert.AreEqual(artifactId, retried.Metadata["audioArtifactId"]);
            StringAssert.Matches(artifactId, new("^audio-[a-f0-9]{32}$"));
            Assert.HasCount(2, handler.Requests);
            StringAssert.Contains(
                handler.Requests[1],
                "/resources/file_audio_test?type=file");

            var local = await audioStorage.ResolveLocalFileAsync(
                "default",
                artifactId);
            Assert.IsNotNull(local);
            Assert.AreEqual(VoiceAudioFormats.Wav, local.Format);
            StringAssert.EndsWith(local.Path, $"{artifactId}.wav");
            var wav = await File.ReadAllBytesAsync(local.Path);
            CollectionAssert.AreEqual(
                Encoding.ASCII.GetBytes("RIFF"),
                wav[..4]);
            CollectionAssert.AreEqual(
                Encoding.ASCII.GetBytes("WAVE"),
                wav[8..12]);
            Assert.AreEqual(16_000, BitConverter.ToInt32(wav, 24));
            Assert.AreEqual(1, BitConverter.ToInt16(wav, 22));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FeishuEvent CreateAudioEvent() => new()
    {
        Header = new FeishuEventHeader
        {
            EventId = "evt_audio",
            EventType = "im.message.receive_v1",
        },
        Event = new FeishuEventV2
        {
            Sender = new FeishuEventSender
            {
                SenderId = new FeishuSenderId { OpenId = "ou_sender" },
            },
            Message = new FeishuMessageEvent
            {
                MessageId = "om_audio",
                ChatId = "oc_chat",
                MessageType = "audio",
                Content = "{\"file_key\":\"file_audio_test\"}",
                CreateTime = "1720000000000",
            },
        },
    };

    private static byte[] CreateStereoPcm16Wav(
        int sampleRate,
        TimeSpan duration)
    {
        const short channels = 2;
        const short bitsPerSample = 16;
        var frameCount = (int)Math.Round(
            sampleRate * duration.TotalSeconds);
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var dataLength = frameCount * blockAlign;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(
            stream,
            Encoding.ASCII,
            leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (var index = 0; index < frameCount; index++)
        {
            var value = (short)(Math.Sin(
                    2 * Math.PI * 440 * index / sampleRate)
                * short.MaxValue
                * 0.2);
            writer.Write(value);
            writer.Write(value);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private sealed class FeishuAudioHandler(byte[] content) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.AbsoluteUri ?? "");
            if (request.RequestUri?.AbsolutePath.EndsWith(
                    "/tenant_access_token/internal",
                    StringComparison.Ordinal) == true)
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"code\":0,\"msg\":\"ok\",\"tenant_access_token\":\"token\",\"expire\":7200}",
                            Encoding.UTF8,
                            "application/json"),
                    });
            }

            var responseContent = new ByteArrayContent(content);
            responseContent.Headers.ContentType = new("audio/ogg");
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = responseContent,
                });
        }
    }
}
