using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class VoiceSynthesisServiceTests
{
    [TestMethod]
    public async Task SynthesizeAsync_ExplicitProvider_UsesItsOwnDefaultModel()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-voice-service-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            Directory.CreateDirectory(paths.ConfigRoot);
            var config = new PuddingVoiceProvidersConfig
            {
                DefaultTtsProviderId = "provider-a",
                DefaultTtsModelId = "model-a",
                Providers =
                [
                    Provider("provider-a", "model-a"),
                    Provider("provider-b", "model-b"),
                ],
            };
            Directory.CreateDirectory(
                Path.GetDirectoryName(
                    paths.SystemConfigFile("voice/providers.json"))!);
            await File.WriteAllTextAsync(
                paths.SystemConfigFile("voice/providers.json"),
                JsonSerializer.Serialize(
                    config,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var factory = new RecordingVoiceProviderFactory();
            var service = new VoiceSynthesisService(
                new VoiceProviderFileService(
                    paths,
                    NullLogger<VoiceProviderFileService>.Instance),
                factory,
                new UnusedHttpClientFactory(),
                NullLogger<VoiceSynthesisService>.Instance);

            var result = await service.SynthesizeAsync(
                new VoiceSynthesisRequest
                {
                    WorkspaceId = "default",
                    MessageId = "message-1",
                    Text = "你好",
                    Provider = "provider-b",
                    Model = "",
                    Voice = "voice-b",
                    AudioFormat = VoiceAudioFormats.Wav,
                });

            Assert.AreEqual("provider-b", factory.ProviderId);
            Assert.AreEqual("model-b", factory.ModelId);
            Assert.IsNotNull(factory.Provider.LastRequest);
            Assert.AreEqual("provider-b", factory.Provider.LastRequest.Provider);
            Assert.AreEqual("model-b", factory.Provider.LastRequest.Model);
            CollectionAssert.AreEqual(
                new byte[] { 82, 73, 70, 70 },
                result.AudioBytes);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static PuddingVoiceProviderConfig Provider(
        string providerId,
        string modelId) => new()
        {
            ProviderId = providerId,
            Name = providerId,
            Endpoint = "https://example.invalid",
            IsEnabled = true,
            TtsModels =
            [
                new PuddingTtsModelConfig
                {
                    ModelId = modelId,
                    Name = modelId,
                    IsDefault = true,
                    AudioFormats = [VoiceAudioFormats.Wav],
                    SampleRates = [24_000],
                },
            ],
        };

    private sealed class RecordingVoiceProviderFactory : IVoiceProviderFactory
    {
        public string? ProviderId { get; private set; }
        public string? ModelId { get; private set; }
        public RecordingTtsProvider Provider { get; } = new();

        public ITtsProvider CreateTtsProvider(
            PuddingVoiceProvidersConfig config,
            string? providerId = null,
            string? modelId = null)
        {
            ProviderId = providerId;
            ModelId = modelId;
            Provider.ProviderId = providerId ?? VoiceSynthesisProviders.Unknown;
            return Provider;
        }

        public IAsrHttpRecognizer CreateAsrProvider(
            PuddingVoiceProvidersConfig config,
            string? providerId = null,
            string? modelId = null)
            => throw new NotSupportedException();
    }

    private sealed class RecordingTtsProvider : ITtsProvider
    {
        public string ProviderId { get; set; } =
            VoiceSynthesisProviders.Unknown;
        public VoiceSynthesisRequest? LastRequest { get; private set; }
        public string Provider => ProviderId;
        public VoiceSynthesisProviderCapabilities Capabilities => new()
        {
            Provider = ProviderId,
        };

        public Task<VoiceSynthesisResult> SynthesizeAsync(
            VoiceSynthesisRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new VoiceSynthesisResult
            {
                MessageId = request.MessageId,
                AudioBytes = [82, 73, 70, 70],
                Format = request.AudioFormat,
                SampleRate = request.SampleRate,
                Provider = request.Provider,
                Model = request.Model,
            });
        }

        public async IAsyncEnumerable<VoiceSynthesisStreamEvent> StreamAsync(
            VoiceSynthesisRequest request,
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No download was expected.");
    }
}
