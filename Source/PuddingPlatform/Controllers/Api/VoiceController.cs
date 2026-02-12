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
        [FromServices] IVoiceProviderFactory factory,
        [FromServices] VoiceProviderFileService voiceService,
        CancellationToken ct)
    {
        var voiceConfig = await voiceService.LoadAsync(ct);
        if (voiceConfig is null || voiceConfig.Providers.Count == 0)
            return Problem("Voice providers not configured.", statusCode: 503);

        var provider = factory.CreateTtsProvider(voiceConfig,
            providerId: request.ProviderId,
            modelId: request.ModelId);

        var result = await provider.SynthesizeAsync(new VoiceSynthesisRequest
        {
            WorkspaceId = "default",
            MessageId = Guid.NewGuid().ToString("N"),
            Text = request.Text,
            Provider = provider.Provider,
            Model = request.ModelId ?? voiceConfig.DefaultTtsModelId ?? "",
            Voice = request.Voice ?? "Cherry",
            AudioFormat = request.Format ?? "wav",
            SampleRate = request.SampleRate > 0 ? request.SampleRate : 24000,
            Instructions = request.Instructions,
        }, ct);

        byte[] audioBytes;
        if (!string.IsNullOrWhiteSpace(result.AudioUrl))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            audioBytes = await http.GetByteArrayAsync(result.AudioUrl, ct);
        }
        else
        {
            return Problem("TTS returned no audio data.", statusCode: 502);
        }

        var contentType = request.Format == "mp3" ? "audio/mpeg" : "audio/wav";
        return File(audioBytes, contentType);
    }

    /// <summary>语音识别。接收原始音频二进制（multipart/form-data 或 raw body），返回文本。</summary>
    [HttpPost("asr/recognize")]
    [RequestSizeLimit(10_485_760)]
    public async Task<IActionResult> Recognize(
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

        string providerId = voiceConfig.DefaultAsrProviderId ?? voiceConfig.Providers[0].ProviderId;
        var providerCfg = voiceConfig.Providers.First(p =>
            p.IsEnabled && string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

        string modelId = voiceConfig.DefaultAsrModelId
            ?? providerCfg.AsrModels.First(m => m.IsDefault).ModelId;
        var modelCfg = providerCfg.AsrModels.First(m =>
            string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

        // ③ 内联创建 DashScopeAsrProvider（避免跨项目引用）
        var httpFactory = HttpContext.RequestServices.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var loggerFactory = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
        var httpClient = httpFactory.CreateClient("DashScopeAsr");

        // 通过反射调用 RecognizeAsync
        var asrType = Type.GetType("PuddingRuntime.Services.DashScopeAsrProvider, PuddingRuntime")!;
        var asrInstance = Activator.CreateInstance(asrType,
            [httpClient, providerCfg, modelCfg,
             loggerFactory.CreateLogger("PuddingRuntime.Services.DashScopeAsrProvider")])!;

        var recognizeMethod = asrType.GetMethod("RecognizeAsync")!;
        var format = Request.Headers.ContentType.ToString().Contains("webm") ? "webm" : "wav";
        var task = (Task)recognizeMethod.Invoke(asrInstance, new object?[] { audioBytes, format, null, ct })!;
        await task;

        var result = ((dynamic)task).Result;
        return Ok(new { text = (string)result.Text, emotion = (string?)result.Emotion });
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
