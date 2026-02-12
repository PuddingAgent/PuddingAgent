using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;

namespace PuddingRuntime.Services;

/// <summary>
/// TTS/ASR Provider 工厂 — 根据配置文件按需创建 Provider 实例。
/// 不缓存实例，每次调用创建新实例（Provider 本身无状态）。
/// Phase 1 仅支持 TTS；ASR 工厂方法后续补充。
/// </summary>
public sealed class VoiceProviderFactory : IVoiceProviderFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;

    public VoiceProviderFactory(
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// 根据 providerId 和 modelId 创建 TTS Provider。
    /// 若未指定则使用配置文件中的默认值。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Provider 未找到、未启用、或无默认配置。
    /// </exception>
    public ITtsProvider CreateTtsProvider(
        PuddingVoiceProvidersConfig config,
        string? providerId = null,
        string? modelId = null)
    {
        providerId ??= config.DefaultTtsProviderId
            ?? throw new InvalidOperationException(
                "No default TTS provider configured. Set defaultTtsProviderId in voice/providers.json.");

        var provider = config.Providers.FirstOrDefault(p =>
            p.IsEnabled && string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"TTS provider '{providerId}' not found or disabled.");

        modelId ??= config.DefaultTtsModelId
            ?? provider.TtsModels.FirstOrDefault(m => m.IsDefault)?.ModelId
            ?? throw new InvalidOperationException(
                $"No default TTS model configured for provider '{providerId}'.");

        var model = provider.TtsModels.FirstOrDefault(m =>
            string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"TTS model '{modelId}' not found in provider '{providerId}'.");

        // Phase 1：仅 DashScope
        if (!string.Equals(provider.ProviderId, "dashscope", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"TTS provider '{provider.ProviderId}' is not yet supported. Phase 1 only supports DashScope.");

        return new DashScopeTtsProvider(
            _httpFactory.CreateClient("DashScopeTts"),
            provider,
            model,
            _loggerFactory.CreateLogger<DashScopeTtsProvider>());
    }

    /// <summary>
    /// 根据 providerId 和 modelId 创建 ASR Provider (Phase 1: HTTP 非实时)。
    /// </summary>
    public DashScopeAsrProvider CreateAsrProvider(
        PuddingVoiceProvidersConfig config,
        string? providerId = null,
        string? modelId = null)
    {
        providerId ??= config.DefaultAsrProviderId
            ?? throw new InvalidOperationException(
                "No default ASR provider configured.");

        var provider = config.Providers.FirstOrDefault(p =>
            p.IsEnabled && string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"ASR provider '{providerId}' not found or disabled.");

        modelId ??= config.DefaultAsrModelId
            ?? provider.AsrModels.FirstOrDefault(m => m.IsDefault)?.ModelId
            ?? throw new InvalidOperationException(
                $"No default ASR model configured for provider '{providerId}'.");

        var model = provider.AsrModels.FirstOrDefault(m =>
            string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"ASR model '{modelId}' not found in provider '{providerId}'.");

        return new DashScopeAsrProvider(
            _httpFactory.CreateClient("DashScopeAsr"),
            provider,
            model,
            _loggerFactory.CreateLogger<DashScopeAsrProvider>());
    }
}
