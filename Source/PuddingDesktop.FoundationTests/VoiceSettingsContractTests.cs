using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-03: voice form helpers and the default description that must never look like a real default.</summary>
public sealed class VoiceSettingsContractTests
{
    [Fact]
    public void ListsAreSplitDeduplicatedAndRoundTrip()
    {
        Assert.Equal(["wav", "mp3"], VoiceSettingsText.ParseList("wav, mp3"));
        Assert.Equal(["zh-CN", "en-US"], VoiceSettingsText.ParseList("zh-CN，en-US；zh-cn"));
        Assert.Equal(["a", "b"], VoiceSettingsText.ParseList("a b  a"));
        Assert.Empty(VoiceSettingsText.ParseList(null));
        Assert.Empty(VoiceSettingsText.ParseList(" , ; "));
        Assert.Equal("wav, mp3", VoiceSettingsText.FormatList(["wav", "mp3"]));
        Assert.Equal("", VoiceSettingsText.FormatList(null));
    }

    [Fact]
    public void SampleRatesKeepOnlyPositiveDistinctIntegers()
    {
        Assert.Equal([16000, 24000, 48000], VoiceSettingsText.ParseSampleRates("24000, 16000, 48000"));
        Assert.Equal([24000], VoiceSettingsText.ParseSampleRates("24000Hz, 24000"));
        Assert.Empty(VoiceSettingsText.ParseSampleRates("abc, 0, -5"));
        Assert.Empty(VoiceSettingsText.ParseSampleRates(null));
        Assert.Equal("16000, 24000", VoiceSettingsText.FormatSampleRates([16000, 24000]));
        Assert.Equal("", VoiceSettingsText.FormatSampleRates(null));
    }

    [Fact]
    public void OptionalSortOrderTreatsEmptyAsZeroNotAsAnError()
    {
        Assert.Null(VoiceSettingsText.ParseOptionalInt(""));
        Assert.Null(VoiceSettingsText.ParseOptionalInt("0"));
        Assert.Null(VoiceSettingsText.ParseOptionalInt("-2"));
        Assert.Equal(3, VoiceSettingsText.ParseOptionalInt(" 3 "));
    }

    [Fact]
    public void ProviderFormRejectsWhatCoreRejects()
    {
        var valid = new VoiceProviderEdit("dashscope", "Dash", "https://dashscope.aliyuncs.com", "", true, ApiKeyChange.Keep, null);
        Assert.Empty(VoiceSettingsText.Validate(valid));
        Assert.Contains("服务商 ID", VoiceSettingsText.Validate(valid with { ProviderId = "bad id" }).Single(), StringComparison.Ordinal);
        Assert.Contains("名称", VoiceSettingsText.Validate(valid with { Name = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("Endpoint", VoiceSettingsText.Validate(valid with { Endpoint = "dashscope.aliyuncs.com" }).Single(), StringComparison.Ordinal);
        Assert.Contains("Endpoint", VoiceSettingsText.Validate(valid with { Endpoint = "https://x.invalid?k=1" }).Single(), StringComparison.Ordinal);
        Assert.Contains("新密钥", VoiceSettingsText.Validate(valid with { KeyChange = ApiKeyChange.Replace, NewKey = "" }).Single(), StringComparison.Ordinal);
        Assert.Empty(VoiceSettingsText.Validate(valid with { KeyChange = ApiKeyChange.Clear, NewKey = null }));
    }

    [Fact]
    public void ModelFormsCheckIdentityAndSampleRates()
    {
        var tts = new VoiceTtsModel("p", "m", "M", "/tts", ["v"], ["wav"], [24000],
            true, false, false, false, false, true, 0);
        Assert.Empty(VoiceSettingsText.Validate(tts));
        Assert.Contains("服务商", VoiceSettingsText.Validate(tts with { ProviderId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("模型 ID", VoiceSettingsText.Validate(tts with { ModelId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("模型名称", VoiceSettingsText.Validate(tts with { Name = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("采样率", VoiceSettingsText.Validate(tts with { SampleRates = [0] }).Single(), StringComparison.Ordinal);
        Assert.Contains("128", VoiceSettingsText.Validate(tts with { ModelId = new string('x', 129) }).Single(), StringComparison.Ordinal);

        var asr = new VoiceAsrModel("p", "m", "M", "/asr", ["zh-CN"], [16000], true, true, false, false, true, 0);
        Assert.Empty(VoiceSettingsText.Validate(asr));
        Assert.Contains("采样率", VoiceSettingsText.Validate(asr with { SampleRates = [-1] }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsDescribeBothKindsWithoutPretendingAnUnsetOneExists()
    {
        Assert.Equal("默认 TTS：未设置 · 默认 ASR：未设置", VoiceDefaults.None.Describe());
        var partial = new VoiceDefaults("dashscope", "cosyvoice-v3-flash", null, null);
        Assert.Equal("默认 TTS：dashscope / cosyvoice-v3-flash · 默认 ASR：未设置", partial.Describe());
        var both = new VoiceDefaults("a", "tts", "b", "asr");
        Assert.Contains("默认 TTS：a / tts", both.Describe(), StringComparison.Ordinal);
        Assert.Contains("默认 ASR：b / asr", both.Describe(), StringComparison.Ordinal);
        // A provider without a model must not be reported as a usable default.
        Assert.Contains("未设置", new VoiceDefaults("a", null, null, "asr").Describe(), StringComparison.Ordinal);
    }
}
