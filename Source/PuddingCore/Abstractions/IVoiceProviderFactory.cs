using PuddingCode.Abstractions;
using PuddingCode.Configuration;

namespace PuddingCode.Abstractions;

/// <summary>
/// TTS/ASR Provider 工厂接口 — 定义在 PuddingCore 以支持跨层 DI 注入。
/// Phase 1 仅 TTS，ASR 后续补充。
/// </summary>
public interface IVoiceProviderFactory
{
    /// <summary>根据 providerId 和 modelId 创建 TTS Provider。</summary>
    ITtsProvider CreateTtsProvider(
        PuddingVoiceProvidersConfig config,
        string? providerId = null,
        string? modelId = null);
}
