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
    private readonly IKeyVaultService? _keyVaultService;

    public DirectLlmClient(
        IHttpClientFactory httpClientFactory,
        ILogger<DirectLlmClient> logger,
        IKeyVaultService? keyVaultService = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _keyVaultService = keyVaultService;
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
        var effectiveConfig = await ResolveLlmConfigAsync(llmConfig, ct);
        var endpoint = effectiveConfig?.Endpoint
            ?? Environment.GetEnvironmentVariable("LLM_ENDPOINT")
            ?? "https://api.openai.com/v1";
        var apiKey = effectiveConfig?.ApiKey
            ?? Environment.GetEnvironmentVariable("LLM_API_KEY")
            ?? "";
        var model = effectiveConfig?.ModelId
            ?? Environment.GetEnvironmentVariable("LLM_MODEL")
            ?? "gpt-4o-mini";

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("LLM_API_KEY not configured.");

        var url = $"{endpoint.TrimEnd('/')}/chat/completions";

        object requestBody = effectiveConfig?.ReasoningEffort is not null
            ? new
            {
                model,
                messages = messages.Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content }).ToArray(),
                temperature = 0.7,
                max_tokens = 2048,
                reasoning_effort = effectiveConfig.ReasoningEffort
            }
            : new
            {
                model,
                messages = messages.Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content }).ToArray(),
                temperature = 0.7,
                max_tokens = 2048
            };

        using var httpClient = new HttpClient();
        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[DirectLlm] REQUEST model={Model} url={Url} msgCount={Count}", model, url, messages.Count);

        var response = await httpClient.SendAsync(request, ct);
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

        var toolCalls = body.TryGetProperty("choices", out var tcChoices) && tcChoices.GetArrayLength() > 0
            ? tcChoices[0].TryGetProperty("message", out var tcMsg) && tcMsg.TryGetProperty("tool_calls", out var tcArr)
                ? tcArr.EnumerateArray().Select(tc => new ToolCall(
                    tc.TryGetProperty("id", out var tcid) ? tcid.GetString() ?? "" : "",
                    tc.TryGetProperty("function", out var tcfn) && tcfn.TryGetProperty("name", out var tcname)
                        ? tcname.GetString() ?? "" : "",
                    tc.TryGetProperty("function", out var tcargs) && tcargs.TryGetProperty("arguments", out var tcargStr)
                        ? tcargStr.GetString() ?? "" : ""
                )).ToArray()
                : null
            : null;

        return new LlmResponse(replyContent, toolCalls, null, usage);
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
        var effectiveConfig = await ResolveLlmConfigAsync(llmConfig, ct);
        var endpoint = effectiveConfig?.Endpoint ?? GetEndpointFromEnvironment() ?? "https://api.openai.com/v1";
        var apiKey = effectiveConfig?.ApiKey ?? GetApiKeyFromEnvironment() ?? "";
        var model = effectiveConfig?.ModelId ?? GetModelFromEnvironment() ?? "gpt-4o-mini";
        var thinkingMode = ResolveThinkingModeFromJson();

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("LLM_API_KEY not configured.");

        _logger.LogInformation(
            "[DirectLlm] STREAM model={Model} endpoint={Endpoint} msgCount={Count} thinkingMode={ThinkingMode}",
            model, endpoint, messages.Count, thinkingMode ?? "(null)");

        var gateway = new OpenAiLlmGateway(
            _httpClientFactory.CreateClient("DirectLlm"),
            new PuddingCode.Platform.Options.LlmOptions(
                endpoint,
                apiKey,
                model,
                ReasoningEffort: effectiveConfig?.ReasoningEffort,
                ThinkingMode: thinkingMode));
        var toolSpecs = (tools ?? []).Select(t => (ITool)new ProxyTool(t)).ToList();

        await foreach (var delta in gateway.ChatStreamAsync(messages, toolSpecs, ct))
            yield return delta;
    }

    /// <summary>
    /// 解析 LLM 配置中的 KeyVault 引用并产出最终可用 ApiKey（仅在内存中使用）。
    /// </summary>
    private async Task<LlmConfig?> ResolveLlmConfigAsync(LlmConfig? config, CancellationToken ct)
    {
        if (config is null)
            return null;

        var apiKey = config.ApiKey;

        if (!string.IsNullOrWhiteSpace(config.KeyVaultId)
            && string.IsNullOrWhiteSpace(apiKey)
            && _keyVaultService is not null)
        {
            var byId = await _keyVaultService.GetSecretAsync(config.KeyVaultId, includePlainText: true, ct);
            if (!string.IsNullOrWhiteSpace(byId?.Value))
            {
                apiKey = byId.Value;
            }
            else
            {
                var placeholder = $"{{{{vault:{config.KeyVaultId}}}}}";
                var injected = await _keyVaultService.InjectAsync(placeholder, ct);
                if (!string.Equals(injected, placeholder, StringComparison.Ordinal))
                    apiKey = injected;
            }
        }

        if (!string.IsNullOrWhiteSpace(apiKey)
            && apiKey.Contains("{{vault:", StringComparison.OrdinalIgnoreCase)
            && _keyVaultService is not null)
        {
            apiKey = await _keyVaultService.InjectAsync(apiKey, ct);
        }

        if (string.Equals(apiKey, config.ApiKey, StringComparison.Ordinal))
            return config;

        return config with { ApiKey = apiKey };
    }

    /// <summary>环境变量最终回退（KeyVault 清理后仍可工作）。</summary>
    public static string? GetApiKeyFromEnvironment()
        => PuddingConfigLoader.ResolveConscious().ApiKey;
    public static string? GetEndpointFromEnvironment()
        => PuddingConfigLoader.ResolveConscious().Endpoint;
    public static string? GetModelFromEnvironment()
        => PuddingConfigLoader.ResolveConscious().Model;

    private static string? ResolveThinkingModeFromJson()
    {
        var mode = PuddingConfigLoader.Load()?.Llm?.Conscious?.ThinkingMode;
        if (string.IsNullOrWhiteSpace(mode))
            return null;

        var normalized = mode.Trim().ToLowerInvariant();
        return normalized is "auto" or "enabled" or "disabled" ? normalized : null;
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
