using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingCode.Configuration;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class VoiceProviderFactoryRoutingTests
{
    [TestMethod]
    public void MessageModel_SelectsItsProtocolWithoutBusinessProviderBranch()
    {
        var factory = new VoiceProviderFactory(Mock.Of<IHttpClientFactory>(), NullLoggerFactory.Instance);
        var config = new PuddingVoiceProvidersConfig
        {
            DefaultAsrProviderId = "my-voice-route", DefaultAsrModelId = "qwen-audio-3.1-asr-flash-message",
            Providers = [new PuddingVoiceProviderConfig { ProviderId = "my-voice-route", Endpoint = "https://example.invalid",
                AsrModels = [new PuddingAsrModelConfig { ModelId = "qwen-audio-3.1-asr-flash-message", IsDefault = true }] }],
        };
        Assert.IsInstanceOfType<QwenMessageAsrProvider>(factory.CreateAsrProvider(config));
        Assert.IsInstanceOfType<PuddingSpeech.IStreamingSpeechRecognizer>(factory.CreateAsrProvider(config));
    }
}
