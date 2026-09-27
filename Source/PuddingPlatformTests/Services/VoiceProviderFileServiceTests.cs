using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// DS-03 语音服务商：密钥保持/替换/清除、字段校验，以及「TTS 与 ASR 默认项互不覆盖」——
/// 运行时只读根上的 Default{Tts,Asr}{Provider,Model}Id，模型的 IsDefault 必须与它一致。
/// </summary>
[TestClass]
public sealed class VoiceProviderFileServiceTests
{
    [TestMethod]
    public async Task ProviderKeyKeepsReplacesAndClears_WithoutTouchingModels()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = await CreateAsync(root, new PuddingVoiceProvidersConfig
            {
                Providers = [new()
                {
                    ProviderId = "dashscope", Name = "Dash", Endpoint = "https://example.invalid",
                    ApiKey = "existing-secret", Description = "kept",
                    TtsModels = [new() { ModelId = "tts-1", Name = "TTS 1", Voices = ["a"], SampleRates = [24000] }]
                }],
                DefaultTtsProviderId = "dashscope", DefaultTtsModelId = "tts-1",
            });

            // Keep: 未提交密钥时不清除，也不丢模型。
            await service.UpdateProviderAsync("dashscope", new UpsertVoiceProviderRequest(
                "dashscope", "Dash renamed", "https://example.invalid", null, "kept", true), CancellationToken.None);
            var config = await service.LoadAsync();
            Assert.AreEqual("existing-secret", config.Providers.Single().ApiKey);
            Assert.AreEqual(1, config.Providers.Single().TtsModels.Count);
            Assert.AreEqual("Dash renamed", config.Providers.Single().Name);

            // Replace: 新明文覆盖旧值。
            await service.UpdateProviderAsync("dashscope", new UpsertVoiceProviderRequest(
                "dashscope", "Dash", "https://example.invalid", "new-secret", "kept", true), CancellationToken.None);
            Assert.AreEqual("new-secret", (await service.LoadAsync()).Providers.Single().ApiKey);

            // Clear: 显式清除。
            await service.UpdateProviderAsync("dashscope", new UpsertVoiceProviderRequest(
                "dashscope", "Dash", "https://example.invalid", null, "kept", true, ClearApiKey: true), CancellationToken.None);
            Assert.AreEqual("", (await service.LoadAsync()).Providers.Single().ApiKey);
            Assert.IsFalse((await service.ListProvidersAsync()).Single().HasApiKey);
            Assert.AreEqual(1, (await service.LoadAsync()).Providers.Single().TtsModels.Count);

            // 同时替换与清除必须被拒绝，而不是静默选一个。
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.UpdateProviderAsync("dashscope",
                new UpsertVoiceProviderRequest("dashscope", "Dash", "https://example.invalid", "x", null, true, ClearApiKey: true),
                CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProviderFieldsAreRejectedInsteadOfWritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = await CreateAsync(root, new PuddingVoiceProvidersConfig { Providers = [] });
            foreach (var invalid in new[]
            {
                new UpsertVoiceProviderRequest("bad id", "N", "https://example.invalid", null, null, true),
                new UpsertVoiceProviderRequest("ok", " ", "https://example.invalid", null, null, true),
                new UpsertVoiceProviderRequest("ok", "N", "example.invalid", null, null, true),
                new UpsertVoiceProviderRequest("ok", "N", "https://user:pass@example.invalid", null, null, true),
                new UpsertVoiceProviderRequest("ok", "N", "https://example.invalid?x=1", null, null, true),
            })
                await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.CreateProviderAsync(invalid, CancellationToken.None));
            Assert.AreEqual(0, (await service.LoadAsync()).Providers.Count, "校验失败不得落盘");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task TtsAndAsrDefaultsDoNotOverwriteEachOther()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = await CreateAsync(root, new PuddingVoiceProvidersConfig
            {
                Providers =
                [
                    new() { ProviderId = "a", Name = "A", Endpoint = "https://a.invalid" },
                    new() { ProviderId = "b", Name = "B", Endpoint = "https://b.invalid" },
                ],
                DefaultTtsProviderId = "a", DefaultTtsModelId = "tts-1",
                DefaultAsrProviderId = "a", DefaultAsrModelId = "asr-1",
            });
            await service.CreateTtsModelAsync("a", Tts("tts-1", isDefault: true), CancellationToken.None);
            await service.CreateAsrModelAsync("a", Asr("asr-1", isDefault: true), CancellationToken.None);

            // 把 TTS 默认切到 b/tts-2：只影响 TTS 列表与 TTS 根指针。
            await service.CreateTtsModelAsync("b", Tts("tts-2", isDefault: true), CancellationToken.None);
            var config = await service.LoadAsync();
            var defaults = await service.GetDefaultsAsync();
            Assert.AreEqual("b", defaults.DefaultTtsProviderId);
            Assert.AreEqual("tts-2", defaults.DefaultTtsModelId);
            Assert.AreEqual("a", defaults.DefaultAsrProviderId, "TTS 默认切换不得动到 ASR 默认");
            Assert.AreEqual("asr-1", defaults.DefaultAsrModelId);
            Assert.IsFalse(config.Providers.Single(p => p.ProviderId == "a").TtsModels.Single().IsDefault);
            Assert.IsTrue(config.Providers.Single(p => p.ProviderId == "b").TtsModels.Single().IsDefault);
            Assert.IsTrue(config.Providers.Single(p => p.ProviderId == "a").AsrModels.Single().IsDefault);

            // 把 ASR 默认切到 b/asr-2：同样只影响 ASR。
            await service.CreateAsrModelAsync("b", Asr("asr-2", isDefault: true), CancellationToken.None);
            defaults = await service.GetDefaultsAsync();
            Assert.AreEqual("b", defaults.DefaultAsrProviderId);
            Assert.AreEqual("asr-2", defaults.DefaultAsrModelId);
            Assert.AreEqual("b", defaults.DefaultTtsProviderId, "ASR 默认切换不得动到 TTS 默认");
            Assert.AreEqual("tts-2", defaults.DefaultTtsModelId);

            // 取消默认：根指针一并清空，避免指向一个不再标记为默认的模型。
            await service.UpdateTtsModelAsync("b", "tts-2", Tts("tts-2", isDefault: false), CancellationToken.None);
            defaults = await service.GetDefaultsAsync();
            Assert.IsNull(defaults.DefaultTtsProviderId);
            Assert.IsNull(defaults.DefaultTtsModelId);
            Assert.AreEqual("asr-2", defaults.DefaultAsrModelId);

            // 删除默认模型与默认 Provider 都会清理根指针。
            await service.UpdateAsrModelAsync("b", "asr-2", Asr("asr-2", isDefault: true), CancellationToken.None);
            await service.DeleteAsrModelAsync("b", "asr-2", CancellationToken.None);
            Assert.IsNull((await service.GetDefaultsAsync()).DefaultAsrModelId);

            await service.CreateTtsModelAsync("a", Tts("tts-3", isDefault: true), CancellationToken.None);
            await service.DeleteProviderAsync("a", CancellationToken.None);
            defaults = await service.GetDefaultsAsync();
            Assert.IsNull(defaults.DefaultTtsProviderId);
            Assert.IsNull(defaults.DefaultTtsModelId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ModelArraysAndCapabilitiesRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = await CreateAsync(root, new PuddingVoiceProvidersConfig
            {
                Providers = [new() { ProviderId = "a", Name = "A", Endpoint = "https://a.invalid" }]
            });
            await service.CreateTtsModelAsync("a", new UpsertTtsModelRequest("tts", "TTS", "/tts",
                ["v1", "v2"], ["wav", "mp3"], [24000, 48000], true, true, true, false, false, true, 3), CancellationToken.None);
            await service.CreateAsrModelAsync("a", new UpsertAsrModelRequest("asr", "ASR", "/asr",
                ["zh-CN", "en-US"], [16000], true, true, true, false, true, 1), CancellationToken.None);

            var detail = await service.GetProviderAsync("a");
            var tts = detail!.TtsModels.Single();
            CollectionAssert.AreEqual(new[] { "v1", "v2" }, tts.Voices);
            CollectionAssert.AreEqual(new[] { "wav", "mp3" }, tts.AudioFormats);
            CollectionAssert.AreEqual(new[] { 24000, 48000 }, tts.SampleRates);
            Assert.IsTrue(tts.SupportsStreaming && tts.SupportsInstructions && tts.SupportsVoiceCloning);
            Assert.IsFalse(tts.SupportsVoiceDesign);

            // 未提交数组时沿用现有值（null = 保持），提交空数组才是清空。
            await service.UpdateTtsModelAsync("a", "tts", new UpsertTtsModelRequest("tts", "TTS renamed", "/tts",
                null, null, null, true, true, true, false, false, true, 3), CancellationToken.None);
            tts = (await service.GetProviderAsync("a"))!.TtsModels.Single();
            Assert.AreEqual("TTS renamed", tts.Name);
            CollectionAssert.AreEqual(new[] { "v1", "v2" }, tts.Voices);
            CollectionAssert.AreEqual(new[] { 24000, 48000 }, tts.SampleRates);

            await service.UpdateTtsModelAsync("a", "tts", new UpsertTtsModelRequest("tts", "TTS renamed", "/tts",
                [], [], [], false, false, false, false, false, false, 3), CancellationToken.None);
            tts = (await service.GetProviderAsync("a"))!.TtsModels.Single();
            Assert.AreEqual(0, tts.Voices.Count);
            Assert.AreEqual(0, tts.SampleRates.Count);

            var asr = (await service.GetProviderAsync("a"))!.AsrModels.Single();
            CollectionAssert.AreEqual(new[] { "zh-CN", "en-US" }, asr.Languages);
            Assert.IsTrue(asr.SupportsEmotion && asr.SupportsTimestamps && asr.SupportsHotWords);

            await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.CreateTtsModelAsync("a",
                Tts("bad-rate", isDefault: false) with { SampleRates = [0] }, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.CreateAsrModelAsync("a",
                Asr(" ", isDefault: false), CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static UpsertTtsModelRequest Tts(string modelId, bool isDefault) => new(
        modelId, modelId, null, ["v"], ["wav"], [24000], false, false, false, false, false, isDefault, 0);

    private static UpsertAsrModelRequest Asr(string modelId, bool isDefault) => new(
        modelId, modelId, null, ["zh-CN"], [16000], false, false, false, false, isDefault, 0);

    private static async Task<VoiceProviderFileService> CreateAsync(string root, PuddingVoiceProvidersConfig config)
    {
        var paths = PuddingDataPaths.FromRoot(root);
        await AtomicFileWriter.WriteJsonAsync(paths.SystemConfigFile("voice/providers.json"), config);
        return new VoiceProviderFileService(paths, NullLogger<VoiceProviderFileService>.Instance);
    }
}
