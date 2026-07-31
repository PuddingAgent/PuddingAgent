using PuddingCode.Abstractions;
using PuddingCode.Configuration;

namespace PuddingCode.Abstractions;

/// <summary>
/// TTS/ASR Provider 工厂接口 — 定义在 PuddingCore 以支持跨层 DI 注入。
/// 文件式 TTS 与 HTTP 文件 ASR 共用该 Provider-neutral 创建边界。
/// </summary>
public interface IVoiceProviderFactory
{
    /// <summary>根据 providerId 和 modelId 创建 TTS Provider。</summary>
    ITtsProvider CreateTtsProvider(
        PuddingVoiceProvidersConfig config,
        string? providerId = null,
        string? modelId = null);

    /// <summary>根据 providerId 和 modelId 创建 ASR HTTP 识别器。</summary>
    IAsrHttpRecognizer CreateAsrProvider(
        PuddingVoiceProvidersConfig config,
        string? providerId = null,
        string? modelId = null);
}
