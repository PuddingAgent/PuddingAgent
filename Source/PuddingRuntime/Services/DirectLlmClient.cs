using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Core;
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

        var usage = body.TryGetProperty("usage", out var usageEl)
            ? new TokenUsageDto
            {
                PromptTokens = usageEl.TryGetProperty("prompt_tokens", out var prompt) ? prompt.GetInt32() : null,
                CompletionTokens = usageEl.TryGetProperty("completion_tokens", out var completion) ? completion.GetInt32() : null,
                TotalTokens = usageEl.TryGetProperty("total_tokens", out var total) ? total.GetInt32() : null,
            }
            : null;

        return new LlmResponse(replyContent, null, null, usage);
    }

    public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        LlmConfig? llmConfig = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var endpoint = llmConfig?.Endpoint ?? "https://api.openai.com/v1";
        var apiKey = llmConfig?.ApiKey ?? "";
        var model = llmConfig?.ModelId ?? "gpt-4o-mini";

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("LLM_API_KEY not configured.");

        _logger.LogInformation(
            "[DirectLlm] STREAM model={Model} endpoint={Endpoint} msgCount={Count}",
            model, endpoint, messages.Count);

        var gateway = new OpenAiLlmGateway(
            _httpClientFactory.CreateClient("DirectLlm"),
            new LlmOptions(endpoint, apiKey, model));
        var toolSpecs = (tools ?? []).Select(t => (ITool)new ProxyTool(t)).ToList();

        await foreach (var delta in gateway.ChatStreamAsync(messages, toolSpecs, ct))
            yield return delta;
    }

    private sealed class ProxyTool(LlmToolDefinition dto) : ITool
    {
        public string Name => dto.Name;
        public string Description => dto.Description;
        public ToolParameterSchema Parameters => dto.Parameters;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => throw new NotSupportedException("Proxy tool definitions are only for function schema transport.");
    }
}
