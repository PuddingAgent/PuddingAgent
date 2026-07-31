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
public sealed class VoiceController : ControllerBase
{
    [HttpPost("tts/synthesize")]
    [Produces("audio/wav", "audio/mpeg")]
    public async Task<IActionResult> Synthesize(
        [FromBody] TtsSynthesizeRequest request,
        [FromServices] IVoiceSynthesisService synthesisService,
        CancellationToken ct)
    {
        var result = await synthesisService.SynthesizeAsync(new VoiceSynthesisRequest
        {
            WorkspaceId = "default",
            MessageId = Guid.NewGuid().ToString("N"),
            Text = request.Text,
            Provider = request.ProviderId ?? VoiceSynthesisProviders.Unknown,
            Model = request.ModelId ?? "",
            Voice = request.Voice ?? "Cherry",
            AudioFormat = request.Format ?? "wav",
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
        [FromServices] IVoiceProviderFactory factory,
        [FromServices] VoiceProviderFileService voiceService,
        CancellationToken ct)
    {
        // ① 读取原始字节
        var audioBytes = await ReadRequestBodyAsync(Request, ct);
        if (audioBytes is null or { Length: 0 })
            return BadRequest("No audio data provided.");

        // ② 加载语音配置
        var voiceConfig = await voiceService.LoadAsync(ct);
        if (voiceConfig is null || voiceConfig.Providers.Count == 0)
            return Problem("Voice providers not configured.", statusCode: 503);

        // ③ 通过标准 DI 创建 ASR Provider（与 TTS 对称）
        var provider = factory.CreateAsrProvider(voiceConfig);
        var format = Request.Headers.ContentType.ToString().Contains("webm") ? "webm" : "wav";
        var result = await provider.RecognizeAsync(audioBytes, format, null, ct);

        return Ok(new { text = result.Text, emotion = result.Emotion });
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
