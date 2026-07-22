using System.Text;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services;

/// <summary>
/// 阿里云百炼 DashScope ASR Provider — Phase 1 HTTP 非实时语音识别。
/// Phase 2 将实现 IAsrProvider 的 WebSocket 实时接口。
/// 读取 voice/providers.json 配置，接收 Base64 音频，返回识别文本 + 情感标签。
/// </summary>
public sealed class DashScopeAsrProvider : IAsrHttpRecognizer
{
    private readonly HttpClient _httpClient;
    private readonly PuddingVoiceProviderConfig _providerConfig;
    private readonly PuddingAsrModelConfig _modelConfig;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    public string ProviderId => _providerConfig.ProviderId;

    public DashScopeAsrProvider(
        HttpClient httpClient,
        PuddingVoiceProviderConfig providerConfig,
        PuddingAsrModelConfig modelConfig,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        _httpClient = httpClient;
        _providerConfig = providerConfig;
        _modelConfig = modelConfig;
        _logger = logger;
    }

    /// <summary>HTTP 非实时语音识别。接收 Base64 编码音频，返回文本和情感标签。</summary>
    /// <remarks>
    /// qwen3-asr-flash 通过多模态生成端点仅支持异步模式。
    /// ① POST 提交任务（带 X-DashScope-Async: enable）获取 task_id
    /// ② 轮询 GET /api/v1/tasks/{task_id} 直到 SUCCEEDED 或超时
    /// ③ 解析最终结果
    /// </remarks>
        public async Task<AsrRecognizeResult> RecognizeAsync(
        byte[] audioData,
        string format = "wav",
        string? language = null,
        CancellationToken ct = default)
    {
        var body = BuildRequestBody(audioData, format, language);
        var endpoint = BuildEndpoint();

        // ① 提交异步任务，获取 task_id
        var submitJson = await PostAsync(endpoint, body, ct, asyncMode: true);
        using var submitDoc = JsonDocument.Parse(submitJson);
        var submitRoot = submitDoc.RootElement;

        if (submitRoot.TryGetProperty("code", out var codeEl))
        {
            var errMsg = submitRoot.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Unknown";
            throw new InvalidOperationException(
                $"DashScope ASR submit error: {codeEl.GetString()} — {errMsg}");
        }

        // 尝试同步返回（极少情况）
        if (submitRoot.TryGetProperty("output", out var syncOutput)
            && syncOutput.TryGetProperty("choices", out _))
        {
            return ParseSyncResponse(submitJson);
        }

        // 获取 task_id
        var taskId = submitRoot.TryGetProperty("output", out var output)
            && output.TryGetProperty("task_id", out var tid)
            ? tid.GetString()
            : null;

        if (string.IsNullOrEmpty(taskId))
        {
            throw new InvalidOperationException(
                $"DashScope ASR: no task_id in response: {submitJson[..Math.Min(300, submitJson.Length)]}");
        }

        _logger.LogInformation("[DashScopeAsr] Task submitted task_id={TaskId}, polling...", taskId);

        // ② 轮询任务状态
        var taskUrl = $"{_providerConfig.Endpoint.TrimEnd('/')}/api/v1/tasks/{taskId}";
        for (int poll = 0; poll < 30; poll++)
        {
            await Task.Delay(2000, ct);
            var taskJson = await GetAsync(taskUrl, ct);
            using var taskDoc = JsonDocument.Parse(taskJson);
            var taskRoot = taskDoc.RootElement;

            if (taskRoot.TryGetProperty("output", out var taskOutput))
            {
                var status = taskOutput.TryGetProperty("task_status", out var ts)
                    ? ts.GetString() : "UNKNOWN";

                _logger.LogDebug("[DashScopeAsr] Task {TaskId} status={Status}", taskId, status);

                switch (status)
                {
                    case "SUCCEEDED":
                        return ParseSyncResponse(taskJson);
                    case "FAILED":
                        var failMsg = taskOutput.TryGetProperty("message", out var fm) ? fm.GetString() : "Unknown";
                        throw new InvalidOperationException($"DashScope ASR task failed: {failMsg}");
                    case "PENDING":
                    case "RUNNING":
                        continue;
                }
            }
        }

        throw new TimeoutException($"DashScope ASR task {taskId} did not complete after 60s polling");
    }

    // ── 私有方法 ──

    private string BuildEndpoint()
    {
        var baseUrl = _providerConfig.Endpoint.TrimEnd('/');
        var path = _modelConfig.Path?.TrimStart('/')
            ?? "api/v1/services/aigc/multimodal-generation/generation";
        return $"{baseUrl}/{path}";
    }

    private object BuildRequestBody(byte[] audioData, string format, string? language)
    {
        var mimeType = format switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            _ => "audio/wav",
        };
        var base64 = Convert.ToBase64String(audioData);
        var audioInput = $"data:{mimeType};base64,{base64}";

        return new
        {
            model = _modelConfig.ModelId,
            input = new
            {
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new[]
                        {
                            new { audio = audioInput }
                        }
                    }
                }
            },
            parameters = new
            {
                asr_options = (object)(string.IsNullOrWhiteSpace(language)
                    ? new { }
                    : new { language })
            }
        };
    }

    private async Task<string> PostAsync(
        string endpoint, object body, CancellationToken ct, bool asyncMode = false)
    {
        var json = JsonSerializer.Serialize(body);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new("application/json");

        var msg = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };
        msg.Headers.Add("Authorization", $"Bearer {_providerConfig.ApiKey}");
        if (asyncMode)
            msg.Headers.Add("X-DashScope-Async", "enable");
        msg.Headers.TryAddWithoutValidation("Accept", "application/json");
        msg.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

        _logger.LogDebug("[DashScopeAsr] POST {Url} body={BodyLen} chars async={Async}",
            endpoint, json.Length, asyncMode);

        var response = await _httpClient.SendAsync(
            msg, HttpCompletionOption.ResponseContentRead, ct);

        var responseText = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("[DashScopeAsr] Response {Status} {Len} chars: {Preview}",
            (int)response.StatusCode, responseText.Length,
            responseText[..Math.Min(200, responseText.Length)]);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[DashScopeAsr] API error {Status}: {Body}",
                (int)response.StatusCode, responseText[..Math.Min(500, responseText.Length)]);
        }
        response.EnsureSuccessStatusCode();
        return responseText;
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Add("Authorization", $"Bearer {_providerConfig.ApiKey}");
        msg.Headers.TryAddWithoutValidation("Accept", "application/json");

        var response = await _httpClient.SendAsync(
            msg, HttpCompletionOption.ResponseContentRead, ct);

        var responseText = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return responseText;
    }

    /// <summary>解析 DashScope 同步响应（含 task 完成后轮询得到的最终结果）。</summary>
    private static AsrRecognizeResult ParseSyncResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var output = root.GetProperty("output");

        // 路径: output.choices[0].message.content[0].text
        // 异步任务完成后可能包裹在 output.results[0] 中
        JsonElement firstChoice;
        if (output.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array
            && results.GetArrayLength() > 0)
        {
            // 任务结果格式: output.results[0].choices[0]...
            var result0 = results[0];
            firstChoice = result0.TryGetProperty("choices", out var rChoices)
                ? rChoices[0]
                : result0; // fallback: 结果直接是 choice
        }
        else
        {
            // 同步格式: output.choices[0]...
            firstChoice = output.GetProperty("choices")[0];
        }

        var message = firstChoice.GetProperty("message");
        var content = message.GetProperty("content");
        var text = content[0].GetProperty("text").GetString() ?? "";

        // 情感识别（千问3-ASR-Flash 固定开启）
        string? emotion = null;
        if (message.TryGetProperty("annotations", out var annotations)
            && annotations.ValueKind == JsonValueKind.Array
            && annotations.GetArrayLength() > 0)
        {
            var firstAnnotation = annotations[0];
            if (firstAnnotation.TryGetProperty("emotion", out var emotionEl))
            {
                emotion = emotionEl.GetString();
            }
        }

        return new AsrRecognizeResult(text, emotion);
    }

}

