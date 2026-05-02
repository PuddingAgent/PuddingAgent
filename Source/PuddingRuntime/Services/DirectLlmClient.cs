using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Runtime 直接调用 LLM API（OpenAI 兼容），不经过 Controller 中转。
/// V1 简化版：直接从 LlmConfig 读取端点与密钥。
/// </summary>
public sealed class DirectLlmClient : IRuntimeLlmClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DirectLlmClient> _logger;

    public DirectLlmClient(IHttpClientFactory httpClientFactory, ILogger<DirectLlmClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<LlmResponse> ChatAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        LlmConfig? llmConfig = null,
        CancellationToken ct = default)
    {
        var endpoint = llmConfig?.Endpoint ?? "https://api.openai.com/v1";
        var apiKey = llmConfig?.ApiKey ?? "";
        var model = llmConfig?.ModelId ?? "gpt-4o-mini";

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("LLM_API_KEY not configured.");

        var url = $"{endpoint.TrimEnd('/')}/chat/completions";

        var requestBody = new
        {
            model,
            messages = messages.Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content }).ToArray(),
            temperature = 0.7,
            max_tokens = 2048
        };

        using var httpClient = _httpClientFactory.CreateClient("DirectLlm");
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.Add("Authorization", $"Bearer {apiKey}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[DirectLlm] REQUEST model={Model} url={Url} msgCount={Count}", model, url, messages.Count);

        var response = await httpClient.PostAsync(url, content, ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("[DirectLlm] ERROR {Status} elapsed={Elapsed}ms body={Body}",
                (int)response.StatusCode, sw.ElapsedMilliseconds, errorBody);
            throw new HttpRequestException($"LLM API returned {(int)response.StatusCode}: {errorBody}");
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var replyContent = body.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            ? choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c)
                ? c.GetString() ?? ""
                : ""
            : "";

        _logger.LogInformation("[DirectLlm] OK contentLen={Len} elapsed={Elapsed}ms", replyContent.Length, sw.ElapsedMilliseconds);

        return new LlmResponse(replyContent, null, null);
    }
}
