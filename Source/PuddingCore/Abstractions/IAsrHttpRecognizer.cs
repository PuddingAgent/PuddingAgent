namespace PuddingCode.Abstractions;

/// <summary>
/// ASR HTTP 非实时语音识别接口 — 与 ITtsProvider 对称的 DI 抽象。
/// Phase 1: HTTP 异步任务模式；Phase 2 可扩展 WebSocket 实时接口。
/// </summary>
public interface IAsrHttpRecognizer
{
    /// <summary>识别音频数据，返回文本和情感标签。</summary>
    Task<AsrRecognizeResult> RecognizeAsync(byte[] audioData, string format, string? language, CancellationToken ct);
}

/// <summary>ASR 识别结果。</summary>
public record AsrRecognizeResult(string Text, string? Emotion);
