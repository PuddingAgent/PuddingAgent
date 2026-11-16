using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// <see cref="VoiceCapabilitiesProjection"/> 是「前端下拉有哪些音色」的唯一来源，
/// 所以它的边界必须锁死：只认默认 provider + 默认模型，缺失就返回空，不猜默认值。
/// </summary>
[TestClass]
public sealed class VoiceCapabilitiesProjectionTests
{
    [TestMethod]
    public void TtsVoices_ReturnsVoicesOfDefaultProviderAndModel()
    {
        var config = Config(
            ("dashscope", true, "qwen3-tts-flash", ["Cherry", "Stella", "Serena"]));

        var voices = VoiceCapabilitiesProjection.TtsVoices(config);

        CollectionAssert.AreEqual(
            new[] { "Cherry", "Stella", "Serena" },
            voices.ToArray());
    }

    [TestMethod]
    public void TtsVoices_IgnoresProviderThatIsNotTheDefault()
    {
        var config = Config(
            ("dashscope", true, "qwen3-tts-flash", ["Cherry"]),
            ("other", true, "model-x", ["Ignored"]));

        var voices = VoiceCapabilitiesProjection.TtsVoices(config);

        CollectionAssert.AreEqual(new[] { "Cherry" }, voices.ToArray());
    }

    [TestMethod]
    public void TtsVoices_IgnoresDisabledDefaultProvider()
    {
        var config = Config(
            ("dashscope", false, "qwen3-tts-flash", ["Cherry"]));

        var voices = VoiceCapabilitiesProjection.TtsVoices(config);

        Assert.AreEqual(0, voices.Count);
    }

    [TestMethod]
    public void TtsVoices_ReturnsEmpty_WhenDefaultModelHasNoVoices()
    {
        // 空清单是显式结果：调用方必须处理「没有可用音色」，
        // 不能在这里塞一个硬编码默认音色假装可用（历史 Vera 就是硬编码的产物）。
        var config = Config(("dashscope", true, "qwen3-tts-flash", []));

        var voices = VoiceCapabilitiesProjection.TtsVoices(config);

        Assert.AreEqual(0, voices.Count);
    }

    [TestMethod]
    public void TtsVoices_ReturnsEmpty_WhenDefaultModelMissing()
    {
        var config = Config(("dashscope", true, "other-model", ["Cherry"]));

        var voices = VoiceCapabilitiesProjection.TtsVoices(config);

        Assert.AreEqual(0, voices.Count);
    }

    private static PuddingVoiceProvidersConfig Config(
        params (string ProviderId, bool Enabled, string ModelId, string[] Voices)[] providers) =>
        new()
        {
            DefaultTtsProviderId = "dashscope",
            DefaultTtsModelId = "qwen3-tts-flash",
            DefaultAsrProviderId = "dashscope",
            DefaultAsrModelId = "qwen-audio-3.1-asr-flash-message",
            Providers = providers
                .Select(item => new PuddingVoiceProviderConfig
                {
                    ProviderId = item.ProviderId,
                    Name = item.ProviderId,
                    Endpoint = "https://example.invalid",
                    IsEnabled = item.Enabled,
                    TtsModels =
                    [
                        new PuddingTtsModelConfig
                        {
                            ModelId = item.ModelId,
                            Name = item.ModelId,
                            Voices = [.. item.Voices],
                        },
                    ],
                })
                .ToList(),
        };
}
