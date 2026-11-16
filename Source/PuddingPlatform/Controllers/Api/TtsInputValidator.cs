using PuddingCode.Models;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// TTS 输入校验的唯一真源。
///
/// 为什么单独抽出来：<c>tts/synthesize</c> 与 <c>tts/stream</c> 都要同一套边界，
/// 一旦各写一份，第三次加端点时必然再抄一遍。校验抛 <see cref="PuddingSpeech.SpeechFailure"/>
/// （自带 400 状态），由 <see cref="VoiceFailureAttribute"/> 统一映射，调用方不写 try/catch。
///
/// 流式端点必须在写出 SSE 响应头**之前**调用：头一旦发出，状态码就改不了了。
/// </summary>
public static class TtsInputValidator
{
    /// <summary>与 <c>QwenTts.BuildRequest</c> 的文本上限保持一致。</summary>
    public const int MaxTextLength = 10_000;

    /// <summary>返回去掉首尾空白的文本；空白或超长时抛 400。</summary>
    public static string ValidateText(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
            throw new PuddingSpeech.SpeechFailure("voice_invalid_text", "朗读内容不能为空。", 400);
        if (text.Length > MaxTextLength)
            throw new PuddingSpeech.SpeechFailure("voice_text_too_long", $"朗读内容不能超过 {MaxTextLength} 个字符。", 400);
        return text;
    }

    /// <summary>返回归一化后的格式（默认 wav）；不支持的格式抛 400。</summary>
    public static string NormalizeFormat(string? raw)
    {
        var format = string.IsNullOrWhiteSpace(raw)
            ? VoiceAudioFormats.Wav
            : raw.Trim().ToLowerInvariant();
        if (format != VoiceAudioFormats.Wav && format != VoiceAudioFormats.Mp3)
            throw new PuddingSpeech.SpeechFailure("voice_unsupported_format", "仅支持 wav 或 mp3 音频格式。", 400);
        return format;
    }
}
