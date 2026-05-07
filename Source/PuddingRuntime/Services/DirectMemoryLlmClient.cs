using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// 使用独立 LLM 配置处理记忆。
/// 未配置时自动回退到主 IRuntimeLlmClient。
/// </summary>
public sealed class DirectMemoryLlmClient : IMemoryLlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DirectMemoryLlmClient> _logger;
    private readonly IRuntimeLlmClient? _mainLlmClient;
    private readonly LlmConfig? _memoryConfig;

    public DirectMemoryLlmClient(
        IHttpClientFactory httpClientFactory,
        ILogger<DirectMemoryLlmClient> logger,
        IRuntimeLlmClient? mainLlmClient = null,
        LlmConfig? memoryConfig = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _mainLlmClient = mainLlmClient;
        _memoryConfig = memoryConfig;
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

        var raw = await CompleteAsync(systemPrompt, userPrompt, 220, ct);
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

        var raw = await CompleteAsync(systemPrompt, sb.ToString(), 300, ct);
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
        var raw = await CompleteAsync(systemPrompt, userPrompt, 180, ct);
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
    /// 优先使用专用记忆模型，未配置时回退到主 IRuntimeLlmClient。
    /// </summary>
    public async Task<string> ChatAsync(
        string systemPrompt, string userMessage, IReadOnlyList<object>? tools = null, CancellationToken ct = default)
    {
        // 回退到 CompleteAsync（当前不支持 tools，后续对接真实 LLM 时扩展）
        var result = await CompleteAsync(systemPrompt, userMessage, maxTokens: 1024, ct);
        return result ?? "ok";
    }

    private async Task<string?> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken ct)
    {
        if (_memoryConfig is not null && !string.IsNullOrWhiteSpace(_memoryConfig.ApiKey))
        {
            try
            {
                return await CompleteByDedicatedModelAsync(systemPrompt, userPrompt, maxTokens, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MemoryLlm] Dedicated memory model failed, fallback to runtime model.");
            }
        }

        if (_mainLlmClient is null)
        {
            return null;
        }

        var response = await _mainLlmClient.ChatAsync(
            workspaceId: "memory",
            sessionId: "memory",
            agentTemplateId: "memory-agent",
            messages:
            [
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, userPrompt),
            ],
            tools: null,
            llmConfig: null,
            ct: ct);

        return response.Content;
    }

    private async Task<string?> CompleteByDedicatedModelAsync(
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken ct)
    {
        var endpoint = _memoryConfig?.Endpoint?.TrimEnd('/') ?? "https://api.openai.com/v1";
        var model = _memoryConfig?.ModelId ?? "gpt-4o-mini";
        var apiKey = _memoryConfig?.ApiKey ?? string.Empty;

        var requestBody = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            temperature = 0.1,
            max_tokens = Math.Clamp(maxTokens, 64, 1200),
        };

        using var client = _httpClientFactory.CreateClient("DirectLlm");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions")
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var text = body.TryGetProperty("choices", out var choices)
                   && choices.ValueKind == JsonValueKind.Array
                   && choices.GetArrayLength() > 0
                   && choices[0].TryGetProperty("message", out var msg)
                   && msg.TryGetProperty("content", out var content)
            ? content.GetString()
            : null;

        return text;
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
}
