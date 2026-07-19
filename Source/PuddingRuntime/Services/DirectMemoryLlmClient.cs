using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// 使用独立 LLM 配置处理记忆。
/// 未配置完整记忆模型时直接报错，避免隐式切换到主聊天模型。
/// </summary>
public sealed class DirectMemoryLlmClient : IMemoryLlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DirectMemoryLlmClient> _logger;
    private readonly LlmConfig? _memoryConfig;
    private readonly string? _memoryProviderId;
    private readonly ITokenUsageRecorder? _tokenUsageRecorder;
    private readonly ProviderRateLimiter? _rateLimiter;

    public DirectMemoryLlmClient(
        IHttpClientFactory httpClientFactory,
        ILogger<DirectMemoryLlmClient> logger,
        LlmConfig? memoryConfig = null,
        ITokenUsageRecorder? tokenUsageRecorder = null,
        string? memoryProviderId = null,
        ProviderRateLimiter? rateLimiter = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _memoryConfig = memoryConfig;
        _tokenUsageRecorder = tokenUsageRecorder;
        _memoryProviderId = memoryProviderId;
        _rateLimiter = rateLimiter;
    }

    public async Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageText))
        {
            return new MemoryClassification(false, 0, 1, null, null);
        }

        const string systemPrompt =
            "You are a memory classifier. Return strict JSON only: " +
            "{\"isWorth\":bool,\"importance\":number,\"confidence\":number,\"tag\":string|null,\"summary\":string|null}.";

        var userPrompt =
            "Classify this message for long-term memory. Keep output concise JSON only.\n" +
            "Message:\n" + messageText;

        var raw = await CompleteAsync(systemPrompt, userPrompt, 220, null, ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new MemoryClassification(false, 0.1, 0.2, null, null);
        }

        if (!TryParseClassification(raw, out var classification))
        {
            _logger.LogWarning("[MemoryLlm] Classify parse failed. rawLen={Len}", raw.Length);
            return new MemoryClassification(false, 0.2, 0.2, null, null);
        }

        return classification;
    }

    public async Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default)
    {
        if (memoryContents.Count == 0)
        {
            return null;
        }

        var normalized = memoryContents
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToArray();

        if (normalized.Length == 0)
        {
            return null;
        }

        const string systemPrompt =
            "You merge duplicate memories. Keep key facts, constraints and preferences. Output plain text only.";

        var sb = new StringBuilder();
        sb.AppendLine("Summarize and merge these memories into one concise memory:");
        foreach (var item in normalized)
        {
            sb.AppendLine($"- {item}");
        }

        var raw = await CompleteAsync(systemPrompt, sb.ToString(), 300, null, ct);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    public async Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return null;
        }

        const string systemPrompt =
            "Analyze user message for memory retrieval and return strict JSON only:\n" +
            "{\"intentType\":\"task_progress|preference|past_conversation|factual|general\",\"entities\":[\"...\"],\"timeRange\":\"recent|recent_month|months_ago|any\",\"searchQuery\":\"...\",\"tagPrefix\":\"...\"}.";

        var userPrompt = "Message:\n" + userMessage.Trim();
        var raw = await CompleteAsync(systemPrompt, userPrompt, 180, null, ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!TryParseIntent(raw, userMessage, out var intent))
        {
            _logger.LogWarning("[MemoryLlm] ParseIntent parse failed. rawLen={Len}", raw.Length);
            return null;
        }

        return intent;
    }

    /// <summary>
    /// 通用对话接口，支持 Tool/Function calling（用于记忆深度探索）。
    /// 必须使用专用记忆模型；缺少完整配置时抛出错误。
    /// </summary>
    public async Task<string> ChatAsync(
        string systemPrompt, string userMessage, IReadOnlyList<object>? tools = null, CancellationToken ct = default)
    {
        return await ChatWithConfigAsync(systemPrompt, userMessage, null, tools, ct);
    }

    public async Task<string> ChatWithConfigAsync(
        string systemPrompt,
        string userMessage,
        MemoryLlmConfig? memoryLlmConfig,
        IReadOnlyList<object>? tools = null,
        CancellationToken ct = default)
    {
        var dedicatedConfig = RequireDedicatedConfig(memoryLlmConfig);

        try
        {
            if (tools is { Count: > 0 })
                return await CompleteByDedicatedModelWithToolsAsync(systemPrompt, userMessage, tools, dedicatedConfig, ct);
            return await CompleteByDedicatedModelAsync(systemPrompt, userMessage, 1024, dedicatedConfig, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MemoryLlm] Dedicated memory model failed.");
            throw;
        }
    }

    /// <summary>
    /// 带 token 用量追踪的对话接口。直接从 HTTP 响应中解析 usage 字段。
    /// </summary>
    public async Task<(string Text, TokenUsageDto? Usage)> ChatWithUsageAsync(
        string systemPrompt,
        string userMessage,
        MemoryLlmConfig? memoryLlmConfig,
        IReadOnlyList<object>? tools = null,
        CancellationToken ct = default)
    {
        var dedicatedConfig = RequireDedicatedConfig(memoryLlmConfig);

        try
        {
            if (tools is { Count: > 0 })
            {
                // tool 模式返回原始 JSON，无法可靠提取 usage
                var rawJson = await CompleteByDedicatedModelWithToolsAsync(systemPrompt, userMessage, tools, dedicatedConfig, ct);
                return (rawJson, null);
            }

            return await CompleteByDedicatedModelWithUsageAsync(systemPrompt, userMessage, 1024, dedicatedConfig, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MemoryLlm] Dedicated memory model failed (with-usage).");
            throw;
        }
    }

    private async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        MemoryLlmConfig? overrideConfig,
        CancellationToken ct)
    {
        var dedicatedConfig = RequireDedicatedConfig(overrideConfig);

        try
        {
            return await CompleteByDedicatedModelAsync(systemPrompt, userPrompt, maxTokens, dedicatedConfig, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MemoryLlm] Dedicated memory model failed.");
            throw;
        }
    }

        private async Task<string> CompleteByDedicatedModelAsync(
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        LlmConfig dedicatedConfig,
        CancellationToken ct)
    {
        var (text, _) = await CompleteByDedicatedModelWithUsageAsync(systemPrompt, userPrompt, maxTokens, dedicatedConfig, ct);
        return text;
    }

    /// <summary>
    /// 执行 LLM 调用并返回文本内容 + TokenUsageDto。
    /// </summary>
    private async Task<(string Text, TokenUsageDto? Usage)> CompleteByDedicatedModelWithUsageAsync(
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        LlmConfig dedicatedConfig,
        CancellationToken ct)
    {
        var endpoint = dedicatedConfig.Endpoint!.TrimEnd('/');
        var model = dedicatedConfig.ModelId!;
        var apiKey = dedicatedConfig.ApiKey!;

        var requestBody = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            temperature = 0.0,
            seed = 42,
            max_completion_tokens = Math.Clamp(maxTokens, 64, 1600),
            thinking = new { type = "disabled" },
        };

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions")
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        _logger.LogDebug(
            "[MemoryLlm] Sending request endpoint={Endpoint} model={Model} sysLen={SysLen} userLen={UserLen} maxTokens={MaxTokens}",
            endpoint, model, systemPrompt.Length, userPrompt.Length, maxTokens);

                // ── 并发限流 ──
        async Task<HttpResponseMessage> SendClassify() => await httpClient.SendAsync(request, ct);
        var memProviderId = _memoryProviderId ?? "memory";
        using var response = _rateLimiter is not null
            ? await _rateLimiter.ExecuteAsync(memProviderId, SendClassify, ct)
            : await SendClassify();
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        string? text = null;

        // 1. 标准 content
        if (body.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var msg)
            && msg.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString();
        }

        // 2. 兜底：thinking 模型的 reasoning_content
        if (string.IsNullOrWhiteSpace(text)
            && body.TryGetProperty("choices", out choices)
            && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var msgFallback)
            && msgFallback.TryGetProperty("reasoning_content", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String)
        {
            text = reasoning.GetString();
            _logger.LogDebug("[MemoryLlm] Used reasoning_content len={Len}", text?.Length ?? 0);
        }

        // 3. 最终兜底日志
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("[MemoryLlm] Response body has no content field. body preview: {Body}",
                body.GetRawText().Length > 500 ? body.GetRawText()[..500] : body.GetRawText());
            throw new InvalidOperationException("Memory LLM response did not contain message content.");
        }

        var usageTokens = body.TryGetProperty("usage", out var usage)
                          && usage.TryGetProperty("total_tokens", out var total)
            ? (int?)total.GetInt32()
            : null;

        _logger.LogDebug(
            "[MemoryLlm] Response received model={Model} textLen={TextLen} tokens={Tokens}",
            model, text?.Length ?? 0, usageTokens);
                        // 从 HTTP 响应中解析 Token 使用
        TokenUsageDto? capturedUsage = null;
        if (body.TryGetProperty("usage", out var usageEl))
        {
            try
            {
                var usageDto = JsonSerializer.Deserialize<TokenUsageDto>(
                    usageEl.GetRawText(), JsonOptions);
                if (usageDto is not null && HasTokenValues(usageDto))
                {
                    capturedUsage = usageDto;

                    // 记录 Subconscious LLM 调用的 Token 使用
                                        if (_tokenUsageRecorder is not null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _tokenUsageRecorder.RecordAsync(
                                    usageDto,
                                    sourceType: "subconscious_memory",
                                    sourceId: $"mem:{Guid.NewGuid():N}",
                                    workspaceId: null,
                                    sessionId: null,
                                    providerId: _memoryProviderId,
                                    modelId: dedicatedConfig.ModelId);
                            }
                            catch { /* fire-and-forget best-effort */ }
                        });
                    }
                }
            }
            catch { /* best-effort */ }
        }

        return (text!, capturedUsage);
    }

    /// <summary>
    /// 带 Tool/Function Calling 的 Memory LLM 调用。
    /// 直接构建 OpenAI 兼容的 tool_choice 请求，返回原始 JSON 字符串供调用方解析 tool_calls。
    /// </summary>
    private async Task<string> CompleteByDedicatedModelWithToolsAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<object> tools,
        LlmConfig dedicatedConfig,
        CancellationToken ct)
    {
        var endpoint = dedicatedConfig.Endpoint!.TrimEnd('/');
        var model = dedicatedConfig.ModelId!;
        var apiKey = dedicatedConfig.ApiKey!;

        var requestBody = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            temperature = 0.0,
            seed = 42,
            max_completion_tokens = 1024,
            thinking = new { type = "disabled" },
            tools,
            tool_choice = "auto",
        };

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions")
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        _logger.LogDebug(
            "[MemoryLlm] Sending tool request endpoint={Endpoint} model={Model} sysLen={SysLen} userLen={UserLen}",
            endpoint, model, systemPrompt.Length, userPrompt.Length);

                // ── 并发限流 ──
        var extractProviderId = _memoryProviderId ?? "memory";
        async Task<HttpResponseMessage> SendExtract() => await httpClient.SendAsync(request, ct);
        using var response = _rateLimiter is not null
            ? await _rateLimiter.ExecuteAsync(extractProviderId, SendExtract, ct)
            : await SendExtract();
        response.EnsureSuccessStatusCode();

        var rawJson = await response.Content.ReadAsStringAsync(ct);

        // 验证格式：检查是否有 tool_calls
        using var doc = JsonDocument.Parse(rawJson);
        var hasToolCalls = doc.RootElement.TryGetProperty("choices", out var choices)
            && choices[0].TryGetProperty("message", out var msg)
            && msg.TryGetProperty("tool_calls", out _);

        _logger.LogDebug(
            "[MemoryLlm] Tool response model={Model} hasToolCalls={HasTools} textLen={Len}",
            model, hasToolCalls, rawJson.Length);

        return rawJson;
    }

    private LlmConfig? ResolveDedicatedConfig(MemoryLlmConfig? overrideConfig)
    {
        if (overrideConfig is not null
            && (!string.IsNullOrWhiteSpace(overrideConfig.Endpoint)
                || !string.IsNullOrWhiteSpace(overrideConfig.ApiKey)
                || !string.IsNullOrWhiteSpace(overrideConfig.ModelId)))
        {
            return new LlmConfig
            {
                Endpoint = overrideConfig.Endpoint,
                ApiKey = overrideConfig.ApiKey,
                ModelId = overrideConfig.ModelId,
            };
        }

        return _memoryConfig;
    }

    private LlmConfig RequireDedicatedConfig(MemoryLlmConfig? overrideConfig)
    {
        var config = ResolveDedicatedConfig(overrideConfig);
        if (config is null
            || string.IsNullOrWhiteSpace(config.Endpoint)
            || string.IsNullOrWhiteSpace(config.ApiKey)
            || string.IsNullOrWhiteSpace(config.ModelId))
        {
            throw new InvalidOperationException(
                "Memory LLM config is missing or incomplete. Configure memory provider/model in data/config/llm.providers.json.");
        }

        return config;
    }

    private static bool TryParseClassification(string raw, out MemoryClassification classification)
    {
        classification = new MemoryClassification(false, 0.2, 0.2, null, null);

        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var isWorth = root.TryGetProperty("isWorth", out var isWorthEl)
            && (isWorthEl.ValueKind == JsonValueKind.True || isWorthEl.ValueKind == JsonValueKind.False)
            && isWorthEl.GetBoolean();

        var importance = ReadScore(root, "importance", 0.2);
        var confidence = ReadScore(root, "confidence", 0.2);

        var tag = root.TryGetProperty("tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.String
            ? tagEl.GetString()
            : null;

        var summary = root.TryGetProperty("summary", out var summaryEl) && summaryEl.ValueKind == JsonValueKind.String
            ? summaryEl.GetString()
            : null;

        classification = new MemoryClassification(isWorth, importance, confidence, tag, summary);
        return true;
    }

    private static bool TryParseIntent(string raw, string userMessage, out MemoryQueryIntent intent)
    {
        intent = null!;

        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var intentType = root.TryGetProperty("intentType", out var intentEl) && intentEl.ValueKind == JsonValueKind.String
                ? NormalizeIntentType(intentEl.GetString())
                : "general";

            var timeRange = root.TryGetProperty("timeRange", out var timeEl) && timeEl.ValueKind == JsonValueKind.String
                ? NormalizeTimeRange(timeEl.GetString())
                : "any";

            var searchQuery = root.TryGetProperty("searchQuery", out var queryEl) && queryEl.ValueKind == JsonValueKind.String
                ? (queryEl.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                searchQuery = userMessage.Trim();
            }

            var entities = root.TryGetProperty("entities", out var entitiesEl)
                ? ReadStringList(entitiesEl)
                : Array.Empty<string>();

            var tagPrefix = root.TryGetProperty("tagPrefix", out var tagPrefixEl) && tagPrefixEl.ValueKind == JsonValueKind.String
                ? NormalizeTagPrefix(tagPrefixEl.GetString())
                : null;

            intent = new MemoryQueryIntent
            {
                IntentType = intentType,
                Entities = entities,
                TimeRange = timeRange,
                SearchQuery = searchQuery,
                TagPrefix = tagPrefix,
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => (x.GetString() ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    private static string NormalizeIntentType(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "task_progress" => "task_progress",
            "preference" => "preference",
            "past_conversation" => "past_conversation",
            "factual" => "factual",
            _ => "general",
        };
    }

    private static string NormalizeTimeRange(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "recent" => "recent",
            "recent_month" => "recent_month",
            "months_ago" => "months_ago",
            _ => "any",
        };
    }

    private static string? NormalizeTagPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Join(
            '/',
            value
                .Trim()
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x)));

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static double ReadScore(JsonElement root, string name, double fallback)
    {
        if (!root.TryGetProperty(name, out var el))
        {
            return fallback;
        }

        var value = el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String when double.TryParse(el.GetString(), out var parsed) => parsed,
            _ => fallback,
        };

        return Math.Clamp(value, 0, 1);
    }

    private static string? ExtractJson(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
        {
            return trimmed;
        }

        var markdownJson = Regex.Match(trimmed, "```(?:json)?\\s*(\\{[\\s\\S]*\\})\\s*```", RegexOptions.IgnoreCase);
        if (markdownJson.Success)
        {
            return markdownJson.Groups[1].Value;
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return trimmed[firstBrace..(lastBrace + 1)];
        }

        return null;
    }

    private static bool HasTokenValues(TokenUsageDto usage)
    {
        return (usage.PromptTokens ?? 0) > 0
            || (usage.CompletionTokens ?? 0) > 0
            || (usage.TotalTokens ?? 0) > 0;
    }
}
