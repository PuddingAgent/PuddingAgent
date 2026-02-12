using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Services;

namespace PuddingRuntime.Services;

/// <summary>
/// Memory-oriented LLM client backed by the shared LLM invocation facade.
/// </summary>
/// <remarks>
/// <see cref="IMemoryLlmClient"/> is a semantic boundary: callers ask for
/// classification, summary, recall intent parsing, or subconscious text
/// processing. It must not also become a second OpenAI-compatible protocol
/// client. This adapter keeps the memory-specific prompts and parsers here,
/// while provider resolution, retries, circuit breaking, key-vault injection,
/// activity records, and protocol execution stay behind
/// <see cref="ILlmInvocationService"/>.
/// </remarks>
public sealed class MemoryLlmInvocationClient(
    ILlmInvocationService invocationService,
    ILogger<MemoryLlmInvocationClient> logger,
    ILlmConfigService? llmConfigService = null,
    TokenUsageRecorder? tokenUsageRecorder = null) : IMemoryLlmClient
{
    private const string DefaultWorkspaceId = "memory";
    private const string DefaultSessionId = "subconscious-memory";
    private const string DefaultAgentInstanceId = "subconscious-memory";
    private const string DefaultAgentTemplateId = "subconscious-memory";
    private const string DefaultSubconsciousProfileId = "default-subconscious";

    public async Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageText))
            return new MemoryClassification(false, 0, 1, null, null);

        const string systemPrompt =
            "You are a memory classifier. Return strict JSON only: " +
            "{\"isWorth\":bool,\"importance\":number,\"confidence\":number,\"tag\":string|null,\"summary\":string|null}.";

        var userPrompt =
            "Classify this message for long-term memory. Keep output concise JSON only.\n" +
            "Message:\n" + messageText;

        var raw = await CompleteAsync(systemPrompt, userPrompt, null, ct);
        if (string.IsNullOrWhiteSpace(raw))
            return new MemoryClassification(false, 0.1, 0.2, null, null);

        if (!TryParseClassification(raw, out var classification))
        {
            logger.LogWarning("[MemoryLlm] Classify parse failed. rawLen={Len}", raw.Length);
            return new MemoryClassification(false, 0.2, 0.2, null, null);
        }

        return classification;
    }

    public async Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default)
    {
        if (memoryContents.Count == 0)
            return null;

        var normalized = memoryContents
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToArray();

        if (normalized.Length == 0)
            return null;

        const string systemPrompt =
            "You merge duplicate memories. Keep key facts, constraints and preferences. Output plain text only.";

        var sb = new StringBuilder();
        sb.AppendLine("Summarize and merge these memories into one concise memory:");
        foreach (var item in normalized)
            sb.AppendLine($"- {item}");

        var raw = await CompleteAsync(systemPrompt, sb.ToString(), null, ct);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    public async Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        const string systemPrompt =
            "Analyze user message for memory retrieval and return strict JSON only:\n" +
            "{\"intentType\":\"task_progress|preference|past_conversation|factual|general\",\"entities\":[\"...\"],\"timeRange\":\"recent|recent_month|months_ago|any\",\"searchQuery\":\"...\",\"tagPrefix\":\"...\"}.";

        var raw = await CompleteAsync(systemPrompt, "Message:\n" + userMessage.Trim(), null, ct);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!TryParseIntent(raw, userMessage, out var intent))
        {
            logger.LogWarning("[MemoryLlm] ParseIntent parse failed. rawLen={Len}", raw.Length);
            return null;
        }

        return intent;
    }

    public Task<string> ChatAsync(
        string systemPrompt,
        string userMessage,
        IReadOnlyList<object>? tools = null,
        CancellationToken ct = default)
        => ChatWithConfigAsync(systemPrompt, userMessage, null, tools, ct);

    public Task<string> ChatWithConfigAsync(
        string systemPrompt,
        string userMessage,
        MemoryLlmConfig? memoryLlmConfig,
        IReadOnlyList<object>? tools = null,
        CancellationToken ct = default)
    {
        if (tools is { Count: > 0 })
        {
            throw new NotSupportedException(
                "Memory LLM tool calls must be modeled as LlmToolDefinition before routing through ILlmInvocationService.");
        }

        return CompleteAsync(systemPrompt, userMessage, memoryLlmConfig, ct);
    }

    public Task<string> ChatWithScopedConfigAsync(
        string systemPrompt,
        string userMessage,
        MemoryLlmConfig? memoryLlmConfig,
        SubconsciousMemoryScope targetScope,
        IReadOnlyList<object>? tools = null,
        CancellationToken ct = default)
    {
        if (tools is { Count: > 0 })
        {
            throw new NotSupportedException(
                "Memory LLM tool calls must be modeled as LlmToolDefinition before routing through ILlmInvocationService.");
        }

        return CompleteAsync(systemPrompt, userMessage, memoryLlmConfig, ct, targetScope);
    }

    private async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        MemoryLlmConfig? overrideConfig,
        CancellationToken ct,
        SubconsciousMemoryScope? targetScope = null)
    {
        var profile = ResolveSubconsciousProfile(overrideConfig);
        var result = await invocationService.InvokeAsync(new LlmInvocationRequest
        {
            WorkspaceId = targetScope?.WorkspaceId ?? DefaultWorkspaceId,
            SessionId = targetScope?.SessionId ?? DefaultSessionId,
            AgentInstanceId = targetScope?.AgentId ?? DefaultAgentInstanceId,
            AgentTemplateId = targetScope?.AgentTemplateId ?? DefaultAgentTemplateId,
            Profile = profile,
            ConfigOverride = ToOverrideConfig(overrideConfig),
            Messages =
            [
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, userPrompt),
            ],
        }, ct);

        if (!result.Success)
            throw new InvalidOperationException($"Memory LLM call failed: {result.Error}");

        if (result.Usage is not null && HasTokenValues(result.Usage))
        {
            _ = RecordUsageBestEffortAsync(result, targetScope, ct);
        }

        return result.ReplyText ?? string.Empty;
    }

    private LlmInvocationProfile ResolveSubconsciousProfile(MemoryLlmConfig? overrideConfig)
    {
        var configured = llmConfigService?.ResolveProfile(DefaultSubconsciousProfileId);
        var memoryConfig = llmConfigService?.GetMemoryConfig();
        var modelId = FirstNonBlank(
            overrideConfig?.ModelId,
            configured?.ModelId,
            memoryConfig?.ModelId,
            "subconscious");

        var providerId = configured?.ProviderId
            ?? ResolveProviderIdForModel(modelId)
            ?? "subconscious";

        return new LlmInvocationProfile
        {
            ProviderId = providerId,
            ProfileId = configured?.ProfileId ?? DefaultSubconsciousProfileId,
            ModelId = modelId,
            Role = "subconscious",
        };
    }

    private string? ResolveProviderIdForModel(string modelId)
        => llmConfigService?.GetAllModels()
            .FirstOrDefault(model => string.Equals(model.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            ?.ProviderId;

    private static LlmConfig? ToOverrideConfig(MemoryLlmConfig? config)
    {
        if (config is null
            || (string.IsNullOrWhiteSpace(config.Endpoint)
                && string.IsNullOrWhiteSpace(config.ApiKey)
                && string.IsNullOrWhiteSpace(config.ModelId)))
        {
            return null;
        }

        return new LlmConfig
        {
            Endpoint = config.Endpoint,
#pragma warning disable CS0618
            ApiKey = config.ApiKey,
#pragma warning restore CS0618
            ModelId = config.ModelId,
        };
    }

    private async Task RecordUsageBestEffortAsync(
        LlmInvocationResult result,
        SubconsciousMemoryScope? targetScope,
        CancellationToken ct)
    {
        if (tokenUsageRecorder is null || result.Usage is null)
            return;

        try
        {
            await tokenUsageRecorder.RecordAsync(
                result.Usage,
                sourceType: "subconscious_memory",
                sourceId: $"mem:{Guid.NewGuid():N}",
                workspaceId: targetScope?.WorkspaceId,
                sessionId: targetScope?.SessionId,
                providerId: result.ProviderId,
                modelId: result.ModelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "[MemoryLlm] Token usage record failed.");
        }
    }

    private static bool TryParseClassification(string raw, out MemoryClassification classification)
    {
        classification = new MemoryClassification(false, 0.2, 0.2, null, null);

        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return false;

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
            return false;

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
                searchQuery = userMessage.Trim();

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
            return Array.Empty<string>();

        return el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => (x.GetString() ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    private static string NormalizeIntentType(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "task_progress" => "task_progress",
            "preference" => "preference",
            "past_conversation" => "past_conversation",
            "factual" => "factual",
            _ => "general",
        };

    private static string NormalizeTimeRange(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "recent" => "recent",
            "recent_month" => "recent_month",
            "months_ago" => "months_ago",
            _ => "any",
        };

    private static string? NormalizeTagPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

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
            return fallback;

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
            return trimmed;

        var markdownJson = Regex.Match(trimmed, "```(?:json)?\\s*(\\{[\\s\\S]*\\})\\s*```", RegexOptions.IgnoreCase);
        if (markdownJson.Success)
            return markdownJson.Groups[1].Value;

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return trimmed[firstBrace..(lastBrace + 1)];

        return null;
    }

    private static bool HasTokenValues(TokenUsageDto usage)
        => (usage.PromptTokens ?? 0) > 0
            || (usage.CompletionTokens ?? 0) > 0
            || (usage.TotalTokens ?? 0) > 0;

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
