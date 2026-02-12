using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services;

/// <summary>
/// 阿里云百炼 DashScope TTS Provider — 实现 ITtsProvider 接口。
/// 支持非流式 HTTP 合成（返回 audio_url）和 SSE 流式合成（逐帧 PCM）。
/// 通过 X-DashScope-SSE header 切换双模。
/// </summary>
public sealed class DashScopeTtsProvider : ITtsProvider
{
    private readonly HttpClient _httpClient;
    private readonly PuddingVoiceProviderConfig _providerConfig;
    private readonly PuddingTtsModelConfig _modelConfig;
    private readonly ILogger<DashScopeTtsProvider> _logger;

    public string Provider => _providerConfig.ProviderId;

    public VoiceSynthesisProviderCapabilities Capabilities { get; }

    /// <summary>
    /// 对接阿里云 DashScope 语音合成 API。
    /// </summary>
    /// <param name="httpClient">预配置的 HttpClient，通过 IHttpClientFactory 获取。</param>
    /// <param name="providerConfig">Voice Provider 配置（Endpoint, ApiKey 等）。</param>
    /// <param name="modelConfig">具体 TTS 模型配置（ModelId, Path, Voices 等）。</param>
    /// <param name="logger">结构化日志。</param>
    public DashScopeTtsProvider(
        HttpClient httpClient,
        PuddingVoiceProviderConfig providerConfig,
        PuddingTtsModelConfig modelConfig,
        ILogger<DashScopeTtsProvider> logger)
    {
        _httpClient = httpClient;
        _providerConfig = providerConfig;
        _modelConfig = modelConfig;
        _logger = logger;

        Capabilities = new VoiceSynthesisProviderCapabilities
        {
            Provider = providerConfig.ProviderId,
            SupportedTransports = modelConfig.SupportsStreaming
                ? [VoiceSynthesisTransports.Http, VoiceSynthesisTransports.Sse]
                : [VoiceSynthesisTransports.Http],
            SupportedSessionModes = [VoiceSynthesisSessionModes.SingleTurn],
            SupportedAudioFormats = modelConfig.AudioFormats,
            SupportedSampleRates = modelConfig.SampleRates,
            SupportsConnectionReuse = false,
            SupportsVoiceCloning = modelConfig.SupportsVoiceCloning,
            SupportsVoiceDesign = modelConfig.SupportsVoiceDesign,
            SupportsInstructions = modelConfig.SupportsInstructions,
            RequiresServerSideCredential = true,
        };
    }

    /// <summary>非流式语音合成。POST DashScope → 解析 output.audio_url 或 output.audio.data。</summary>
    public async Task<VoiceSynthesisResult> SynthesizeAsync(
        VoiceSynthesisRequest request, CancellationToken ct = default)
    {
        var body = BuildRequestBody(request, stream: false);
        var json = await PostAsync(body, stream: false, ct);
        return ParseNonStreamResponse(json, request.MessageId);
    }

    /// <summary>流式语音合成（SSE）。POST + X-DashScope-SSE → IAsyncEnumerable&lt;AudioDelta&gt;。</summary>
    public async IAsyncEnumerable<VoiceSynthesisStreamEvent> StreamAsync(
        VoiceSynthesisRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequestBody(request, stream: true);
        using var response = await PostStreamAsync(body, ct);

        var fullText = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("[DashScopeTts] SSE raw response ({Len} chars): {Text}",
            fullText.Length, fullText[..Math.Min(200, fullText.Length)]);

        var lines = fullText.Split('\n', StringSplitOptions.None);
        var sequence = 0;

        foreach (var raw in lines)
        {
            if (ct.IsCancellationRequested) break;
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data:")) continue;

            var json = line[5..].Trim();
            if (json == "[DONE]") break;

            var chunk = JsonSerializer.Deserialize<DashScopeSseChunk>(json);
            if (chunk?.Output?.Audio?.Data is not null)
            {
                yield return VoiceSynthesisStreamEvent.AudioDelta(
                    messageId: request.MessageId,
                    deliveryId: request.DeliveryId,
                    audioBytes: Convert.FromBase64String(chunk.Output.Audio.Data),
                    format: request.AudioFormat,
                    sampleRate: request.SampleRate,
                    sequence: ++sequence);
            }
        }
    }

    // ── 私有方法 ──

    private string BuildEndpoint()
    {
        var baseUrl = _providerConfig.Endpoint.TrimEnd('/');
        var path = _modelConfig.Path?.TrimStart('/')
            ?? "api/v1/services/audio/tts/SpeechSynthesizer";
        return $"{baseUrl}/{path}";
    }

    private object BuildRequestBody(VoiceSynthesisRequest request, bool stream)
    {
        _ = stream;
        var modelId = _modelConfig.ModelId;

        // Qwen-TTS family: different body format (language_type instead of format/sample_rate)
        if (modelId.StartsWith("qwen", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                model = modelId,
                input = new
                {
                    text = request.Text,
                    voice = request.Voice,
                    language_type = "Chinese",
                }
            };
        }

        // CosyVoice / default
        return new
        {
            model = modelId,
            input = new
            {
                text = request.Text,
                voice = request.Voice,
                format = request.AudioFormat,
                sample_rate = request.SampleRate,
                instruction = string.IsNullOrWhiteSpace(request.Instructions)
                    ? null : request.Instructions,
            }
        };
    }

    private async Task<string> PostAsync(object body, bool stream, CancellationToken ct)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint())
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("Authorization", $"Bearer {_providerConfig.ApiKey}");
        if (stream)
            msg.Headers.Add("X-DashScope-SSE", "enable");

        var response = await _httpClient.SendAsync(msg, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> PostStreamAsync(object body, CancellationToken ct)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint())
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("Authorization", $"Bearer {_providerConfig.ApiKey}");
        msg.Headers.Add("X-DashScope-SSE", "enable");

        var response = await _httpClient.SendAsync(msg, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static VoiceSynthesisResult ParseNonStreamResponse(
        string json, string messageId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var output = root.GetProperty("output");
        var audio = output.GetProperty("audio");

        // CosyVoice non-stream: output.audio.url
        // Qwen-TTS non-stream: output.audio_url
        var audioUrl = (audio.TryGetProperty("url", out var url) ? url.GetString()
            : output.TryGetProperty("audio_url", out var au) ? au.GetString()
            : null) ?? "";

        return new VoiceSynthesisResult
        {
            MessageId = messageId,
            AudioUrl = audioUrl,
            Format = VoiceAudioFormats.Mp3,
            SampleRate = 24_000,
            Provider = VoiceSynthesisProviders.DashScope,
            Model = "cosyvoice-v3-flash",
        };
    }

    // ── SSE 响应反序列化模型 ──

    private sealed record DashScopeSseChunk
    {
        public DashScopeSseOutput? Output { get; init; }
    }

    private sealed record DashScopeSseOutput
    {
        public DashScopeSseAudio? Audio { get; init; }
    }

    private sealed record DashScopeSseAudio
    {
        public string? Data { get; init; }
    }
}
