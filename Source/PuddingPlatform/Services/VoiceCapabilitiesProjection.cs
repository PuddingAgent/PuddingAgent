using PuddingCode.Configuration;

namespace PuddingPlatform.Services;

/// <summary>
/// <c>GET /api/voice/capabilities</c> 中「有哪些音色」这一项的纯投影。
///
/// 抽成静态纯函数是为了可测：控制器里取同一个值需要整套依赖
/// （配置读取服务 + 实时识别服务），而这些依赖与「音色清单」无关。
///
/// 音色清单的唯一真源是 <c>providers.json</c> 的 <c>ttsModels[].voices</c>：
/// 前端下拉改为消费本投影，历史音色 <c>Vera</c> 那类「前后端各硬编码一份、
/// 配置改了 UI 不知道」的漂移就此消除。
/// </summary>
public static class VoiceCapabilitiesProjection
{
    /// <summary>
    /// 取默认 TTS Provider 与默认模型上声明的音色；
    /// 任一环节缺失都返回空列表，而不是回退到某个硬编码默认值——
    /// 空列表让调用方显式处理「没有可用音色」，不会假装成功。
    /// </summary>
    public static IReadOnlyList<string> TtsVoices(
        PuddingVoiceProvidersConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var provider = config.Providers.FirstOrDefault(item =>
            item.IsEnabled
            && string.Equals(
                item.ProviderId,
                config.DefaultTtsProviderId,
                StringComparison.OrdinalIgnoreCase));

        return provider?.TtsModels
            .FirstOrDefault(model => string.Equals(
                model.ModelId,
                config.DefaultTtsModelId,
                StringComparison.OrdinalIgnoreCase))
            ?.Voices ?? [];
    }
}
