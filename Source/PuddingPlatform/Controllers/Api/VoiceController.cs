using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// TTS/ASR 语音 API — 聊天消息朗读 + 语音识别。
/// </summary>
[Authorize]
[ApiController]
[Route("api/voice")]
[VoiceFailure]
public sealed class VoiceController : ControllerBase
{
    /// <summary>待朗读文本上限，转发到唯一真源。</summary>
    public const int MaxTextLength = TtsInputValidator.MaxTextLength;

    [HttpPost("tts/synthesize")]
    [Produces("audio/wav", "audio/mpeg")]
    public async Task<IActionResult> Synthesize(
        [FromBody] VoiceSynthesizeInput request,
        [FromServices] IVoiceSynthesisService synthesisService,
        CancellationToken ct)
    {
        // 校验交给唯一真源；错误映射仍由 [VoiceFailure] 负责，不另写 try/catch。
        var text = TtsInputValidator.ValidateText(request.Text);
        var format = TtsInputValidator.NormalizeFormat(request.Format);

        var result = await synthesisService.SynthesizeAsync(new VoiceSynthesisRequest
        {
            WorkspaceId = request.WorkspaceId ?? string.Empty,
            MessageId = Guid.NewGuid().ToString("N"),
            Text = text,
            Model = "",
            Voice = request.Voice ?? "",
            AudioFormat = format,
            SampleRate = request.SampleRate > 0 ? request.SampleRate : 24000,
            Instructions = request.Instructions,
        }, ct);

        if (result.AudioBytes is not { Length: > 0 } audioBytes)
            return Problem("TTS returned no audio data.", statusCode: 502);

        var contentType = string.Equals(
            result.Format,
            VoiceAudioFormats.Mp3,
            StringComparison.OrdinalIgnoreCase)
            ? "audio/mpeg"
            : "audio/wav";
        return File(audioBytes, contentType);
    }

    /// <summary>语音识别。接收原始音频二进制（multipart/form-data 或 raw body），返回文本。</summary>
    [HttpPost("asr/recognize")]
    [RequestSizeLimit(10_485_760)]
    public async Task<IActionResult> Recognize(
        [FromServices] IAudioTranscriptionService transcription,
        CancellationToken ct)
    {
        // ① 读取原始字节
        var audioBytes = await ReadRequestBodyAsync(Request, ct);
        if (audioBytes is null or { Length: 0 })
            throw new PuddingSpeech.SpeechFailure("voice_no_speech", "没有录到声音，请重新录音。", 400);

        // The unified service selects the provider; multipart labels cannot establish encoding.
        PuddingSpeech.PcmWave.ReadPcm16KhzMono(audioBytes);
        var result = await transcription.TranscribeAsync(new AudioTranscriptionRequest { Content = audioBytes, Format = "wav" }, ct);

        return Ok(new { text = result.Text, emotion = result.Emotion });
    }

    [HttpGet("capabilities")]
    public async Task<IActionResult> Capabilities([FromServices] VoiceProviderFileService voiceService,
        [FromServices] RealtimeAudioTranscriptionService realtime, CancellationToken ct)
    {
        var config = await voiceService.LoadAsync(ct);
        var asr = config.Providers.FirstOrDefault(p => p.IsEnabled && p.ProviderId == config.DefaultAsrProviderId);
        var tts = config.Providers.FirstOrDefault(p => p.IsEnabled && p.ProviderId == config.DefaultTtsProviderId);
        return Ok(new { canRecognize = asr?.AsrModels.Any(m => m.ModelId == config.DefaultAsrModelId) == true,
            canSynthesize = tts?.TtsModels.Any(m => m.ModelId == config.DefaultTtsModelId) == true,
            canStreamRecognize = await realtime.IsAvailableAsync(ct),
            maxRecordingSeconds = 120, recordingFormat = "wav", streamingFormat = "pcm", sampleRate = 16000 });
    }

    [HttpPost("tts/stream")]
    public async Task Stream([FromBody] VoiceSynthesizeInput request, [FromServices] IVoiceSynthesisService synthesis, CancellationToken ct)
    {
        // 必须先校验再写 SSE 头：响应头一旦发出，状态码就无法再改成 400。
        var text = TtsInputValidator.ValidateText(request.Text);
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        try
        {
            await foreach (var frame in synthesis.StreamAsync(new VoiceSynthesisRequest
            {
                WorkspaceId = request.WorkspaceId ?? "", MessageId = Guid.NewGuid().ToString("N"),
                Text = text, Model = "", Voice = request.Voice ?? "", AudioFormat = "pcm", SampleRate = 24000,
            }, ct))
            {
                await WriteEvent(new { type = "audio", data = Convert.ToBase64String(frame.AudioBytes ?? []), sampleRate = frame.SampleRate ?? 24000 }, ct);
            }
            await WriteEvent(new { type = "completed" }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failure = ex as PuddingSpeech.SpeechFailure
                ?? new PuddingSpeech.SpeechFailure("voice_provider_failed", "语音生成失败，请检查语音设置后重试。");
            var errorId = Guid.NewGuid().ToString("N")[..12];
            HttpContext.RequestServices.GetService<ILogger<VoiceController>>()?
                .LogWarning("[VoiceStream] errorId={ErrorId} code={Code}", errorId, failure.Code);
            await WriteEvent(new { type = "failed", code = failure.Code, message = failure.Message, errorId }, ct);
        }
    }

    private async Task WriteEvent(object data, CancellationToken ct)
    {
        await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(data)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    private static async Task<byte[]?> ReadRequestBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("audio");
            if (file is null) return null;
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        using var bodyMs = new MemoryStream();
        await request.Body.CopyToAsync(bodyMs, ct);
        return bodyMs.Length > 0 ? bodyMs.ToArray() : null;
    }
}

/// <summary>Business request: routing and credentials are always resolved by Core.</summary>
public sealed record VoiceSynthesizeInput
{
    public required string Text { get; init; }
    public string? WorkspaceId { get; init; }
    public string? Voice { get; init; }
    public string? Format { get; init; }
    public int SampleRate { get; init; }
    public string? Instructions { get; init; }
}
