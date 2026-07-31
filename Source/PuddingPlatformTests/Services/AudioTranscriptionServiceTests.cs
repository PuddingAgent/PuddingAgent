using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AudioTranscriptionServiceTests
{
    [TestMethod]
    public async Task TranscribeAsync_ExplicitProvider_UsesItsOwnDefaultModel()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-audio-transcription-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var config = new PuddingVoiceProvidersConfig
            {
                DefaultAsrProviderId = "provider-a",
                DefaultAsrModelId = "model-a",
                Providers =
                [
                    Provider("provider-a", "model-a"),
                    Provider("provider-b", "model-b"),
                ],
            };
            var configPath = paths.SystemConfigFile("voice/providers.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await File.WriteAllTextAsync(
                configPath,
                JsonSerializer.Serialize(
                    config,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var factory = new RecordingVoiceProviderFactory();
            var service = new AudioTranscriptionService(
                new VoiceProviderFileService(
                    paths,
                    NullLogger<VoiceProviderFileService>.Instance),
                factory,
                NullLogger<AudioTranscriptionService>.Instance);

            var result = await service.TranscribeAsync(
                new AudioTranscriptionRequest
                {
                    Content = [82, 73, 70, 70],
                    Format = VoiceAudioFormats.Wav,
                    Language = "zh-CN",
                    Provider = "provider-b",
                });

            Assert.AreEqual("provider-b", factory.ProviderId);
            Assert.AreEqual("model-b", factory.ModelId);
            Assert.IsNotNull(factory.Recognizer.LastAudio);
            Assert.AreEqual(VoiceAudioFormats.Wav, factory.Recognizer.LastFormat);
            Assert.AreEqual("zh-CN", factory.Recognizer.LastLanguage);
            Assert.AreEqual("今天天气很好", result.Text);
            Assert.AreEqual("happy", result.Emotion);
            Assert.AreEqual("provider-b", result.Provider);
            Assert.AreEqual("model-b", result.Model);
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
            AsrModels =
            [
                new PuddingAsrModelConfig
                {
                    ModelId = modelId,
                    Name = modelId,
                    IsDefault = true,
                    Languages = ["zh-CN"],
                    SampleRates = [16_000],
                },
            ],
        };

    private sealed class RecordingVoiceProviderFactory : IVoiceProviderFactory
    {
        public string? ProviderId { get; private set; }
        public string? ModelId { get; private set; }
        public RecordingAsrRecognizer Recognizer { get; } = new();

        public ITtsProvider CreateTtsProvider(
            PuddingVoiceProvidersConfig config,
            string? providerId = null,
            string? modelId = null)
            => throw new NotSupportedException();

        public IAsrHttpRecognizer CreateAsrProvider(
            PuddingVoiceProvidersConfig config,
            string? providerId = null,
            string? modelId = null)
        {
            ProviderId = providerId;
            ModelId = modelId;
            return Recognizer;
        }
    }

    private sealed class RecordingAsrRecognizer : IAsrHttpRecognizer
    {
        public byte[]? LastAudio { get; private set; }
        public string? LastFormat { get; private set; }
        public string? LastLanguage { get; private set; }

        public Task<AsrRecognizeResult> RecognizeAsync(
            byte[] audioData,
            string format,
            string? language,
            CancellationToken ct)
        {
            LastAudio = audioData;
            LastFormat = format;
            LastLanguage = language;
            return Task.FromResult(
                new AsrRecognizeResult("今天天气很好", "happy"));
        }
    }
}
