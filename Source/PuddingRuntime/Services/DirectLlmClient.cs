using System.Runtime.CompilerServices;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Runtime 直接调用 LLM API（OpenAI 兼容），不经过 Controller 中转。
/// 同步与流式路径统一委托给 OpenAiLlmGateway，避免 Runtime 内部维护重复协议实现。
/// </summary>
public sealed class DirectLlmClient : IRuntimeLlmClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILlmConfigService _llmConfigService;
    private readonly ILogger<DirectLlmClient> _logger;
    private readonly IKeyVaultService? _keyVaultService;
    private readonly IRuntimeActivitySink? _activitySink;
    private readonly IRuntimeTraceAccessor? _traceAccessor;

    public DirectLlmClient(
        IHttpClientFactory httpClientFactory,
        ILlmConfigService llmConfigService,
        ILogger<DirectLlmClient> logger,
        IKeyVaultService? keyVaultService = null,
        IRuntimeActivitySink? activitySink = null,
        IRuntimeTraceAccessor? traceAccessor = null)
    {
        _httpClientFactory = httpClientFactory;
        _llmConfigService = llmConfigService;
        _logger = logger;
        _keyVaultService = keyVaultService;
        _activitySink = activitySink;
        _traceAccessor = traceAccessor;
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
        var config = await ResolveGatewayConfigAsync(llmConfig, ct);
        var gateway = CreateGateway(config);
        var toolSpecs = ToToolSpecs(tools);
        var trace = ResolveTrace(workspaceId, sessionId);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "[DirectLlm] REQUEST model={Model} endpoint={Endpoint} msgCount={Count} toolCount={ToolCount} thinkingMode={ThinkingMode}",
            config.Model,
            config.Endpoint,
            messages.Count,
            toolSpecs.Count,
            config.ThinkingMode ?? "(null)");

        await RecordActivityAsync(
            trace,
            operation: "chat",
            status: RuntimeActivityStatuses.Started,
            startedAt,
            endedAt: null,
            durationMs: null,
            summary: "Direct LLM chat request started.",
            metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
            error: null,
            ct);

        try
        {
            var result = await gateway.ChatAsync(messages, toolSpecs, ct);
            sw.Stop();

            _logger.LogInformation(
                "[DirectLlm] OK contentLen={Len} elapsed={Elapsed}ms promptTokens={PromptTokens} completionTokens={CompletionTokens}",
                result.Content?.Length ?? 0,
                sw.ElapsedMilliseconds,
                result.Usage?.PromptTokens,
                result.Usage?.CompletionTokens);

            await RecordActivityAsync(
                trace,
                operation: "chat",
                status: RuntimeActivityStatuses.Succeeded,
                startedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: sw.ElapsedMilliseconds,
                summary: "Direct LLM chat request completed.",
                metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count, result.Usage),
                error: null,
                CancellationToken.None);

            return result;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            await RecordActivityAsync(
                trace,
                operation: "chat",
                status: RuntimeActivityStatuses.Cancelled,
                startedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: sw.ElapsedMilliseconds,
                summary: "Direct LLM chat request cancelled.",
                metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                error: ex,
                CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[DirectLlm] ERROR elapsed={Elapsed}ms", sw.ElapsedMilliseconds);
            await RecordActivityAsync(
                trace,
                operation: "chat",
                status: RuntimeActivityStatuses.Failed,
                startedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: sw.ElapsedMilliseconds,
                summary: "Direct LLM chat request failed.",
                metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                error: ex,
                CancellationToken.None);
            throw;
        }
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
        var config = await ResolveGatewayConfigAsync(llmConfig, ct);
        var gateway = CreateGateway(config);
        var toolSpecs = ToToolSpecs(tools);
        var trace = ResolveTrace(workspaceId, sessionId);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "[DirectLlm] STREAM model={Model} endpoint={Endpoint} msgCount={Count} toolCount={ToolCount} thinkingMode={ThinkingMode}",
            config.Model,
            config.Endpoint,
            messages.Count,
            toolSpecs.Count,
            config.ThinkingMode ?? "(null)");

        await RecordActivityAsync(
            trace,
            operation: "chat_stream",
            status: RuntimeActivityStatuses.Started,
            startedAt,
            endedAt: null,
            durationMs: null,
            summary: "Direct LLM streaming request started.",
            metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
            error: null,
            ct);

        var terminalRecorded = false;
        var enumerator = gateway.ChatStreamAsync(messages, toolSpecs, ct).GetAsyncEnumerator(ct);

        try
        {
            while (true)
            {
                StreamDelta delta;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;

                    delta = enumerator.Current;
                }
                catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
                {
                    terminalRecorded = true;
                    sw.Stop();
                    await RecordActivityAsync(
                        trace,
                        operation: "chat_stream",
                        status: RuntimeActivityStatuses.Cancelled,
                        startedAt,
                        endedAt: DateTimeOffset.UtcNow,
                        durationMs: sw.ElapsedMilliseconds,
                        summary: "Direct LLM streaming request cancelled.",
                        metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                        error: ex,
                        CancellationToken.None);
                    throw;
                }
                catch (Exception ex)
                {
                    terminalRecorded = true;
                    sw.Stop();
                    _logger.LogError(ex, "[DirectLlm] STREAM ERROR elapsed={Elapsed}ms", sw.ElapsedMilliseconds);
                    await RecordActivityAsync(
                        trace,
                        operation: "chat_stream",
                        status: RuntimeActivityStatuses.Failed,
                        startedAt,
                        endedAt: DateTimeOffset.UtcNow,
                        durationMs: sw.ElapsedMilliseconds,
                        summary: "Direct LLM streaming request failed.",
                        metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                        error: ex,
                        CancellationToken.None);
                    throw;
                }

                yield return delta;
            }

            terminalRecorded = true;
            sw.Stop();
            await RecordActivityAsync(
                trace,
                operation: "chat_stream",
                status: RuntimeActivityStatuses.Succeeded,
                startedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: sw.ElapsedMilliseconds,
                summary: "Direct LLM streaming request completed.",
                metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                error: null,
                CancellationToken.None);
        }
        finally
        {
            await enumerator.DisposeAsync();

            if (!terminalRecorded)
            {
                sw.Stop();
                await RecordActivityAsync(
                    trace,
                    operation: "chat_stream",
                    status: RuntimeActivityStatuses.Cancelled,
                    startedAt,
                    endedAt: DateTimeOffset.UtcNow,
                    durationMs: sw.ElapsedMilliseconds,
                    summary: "Direct LLM streaming request ended before completion.",
                    metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                    error: null,
                    CancellationToken.None);
            }
        }
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

    private async Task<ResolvedGatewayConfig> ResolveGatewayConfigAsync(
        LlmConfig? requestConfig,
        CancellationToken ct)
    {
        var effectiveRequestConfig = await ResolveLlmConfigAsync(requestConfig, ct);
        var defaultConfig = await ResolveLlmConfigAsync(_llmConfigService.GetDefault(), ct);

        var endpoint = FirstNonBlank(
            effectiveRequestConfig?.Endpoint,
            defaultConfig?.Endpoint,
            GetEndpointFromEnvironment(),
            "https://api.openai.com/v1")!;
        var apiKey = FirstNonBlank(
            effectiveRequestConfig?.ApiKey,
            defaultConfig?.ApiKey,
            GetApiKeyFromEnvironment(),
            string.Empty)!;
        var model = FirstNonBlank(
            effectiveRequestConfig?.ModelId,
            defaultConfig?.ModelId,
            GetModelFromEnvironment(),
            "gpt-4o-mini")!;
        var reasoningEffort = FirstNonBlank(
            effectiveRequestConfig?.ReasoningEffort,
            defaultConfig?.ReasoningEffort);
        var thinkingMode = ResolveThinkingModeFromJson();

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("LLM_API_KEY not configured.");

        return new ResolvedGatewayConfig(
            endpoint,
            apiKey,
            model,
            reasoningEffort,
            thinkingMode);
    }

    private OpenAiLlmGateway CreateGateway(ResolvedGatewayConfig config)
    {
        return new OpenAiLlmGateway(
            _httpClientFactory.CreateClient("DirectLlm"),
            new PuddingCode.Platform.Options.LlmOptions(
                config.Endpoint,
                config.ApiKey,
                config.Model,
                ReasoningEffort: config.ReasoningEffort,
                ThinkingMode: config.ThinkingMode));
    }

    private static List<ITool> ToToolSpecs(IReadOnlyList<LlmToolDefinition>? tools)
        => (tools ?? []).Select(t => (ITool)new ProxyTool(t)).ToList();

    private RuntimeTraceContext ResolveTrace(string workspaceId, string sessionId)
    {
        var current = _traceAccessor?.Current;
        return current is null
            ? RuntimeTraceContext.CreateNew(sessionId: sessionId, workspaceId: workspaceId)
            : current.WithSession(sessionId, workspaceId);
    }

    private async Task RecordActivityAsync(
        RuntimeTraceContext trace,
        string operation,
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        long? durationMs,
        string summary,
        IReadOnlyDictionary<string, string> metadata,
        Exception? error,
        CancellationToken ct)
    {
        if (_activitySink is null)
            return;

        try
        {
            await _activitySink.RecordAsync(new RuntimeActivity
            {
                Trace = trace,
                Component = RuntimeActivityComponents.LlmGateway,
                Operation = operation,
                Status = status,
                StartedAtUtc = startedAt,
                EndedAtUtc = endedAt,
                DurationMs = durationMs,
                Severity = error is null ? "info" : "error",
                Summary = summary,
                Metadata = metadata,
                ErrorCode = error?.GetType().Name,
                ErrorMessage = error?.Message,
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Request cancellation must not mask the original LLM cancellation.
        }
    }

    private static IReadOnlyDictionary<string, string> BuildMetadata(
        ResolvedGatewayConfig config,
        string agentTemplateId,
        int messageCount,
        int toolCount,
        TokenUsageDto? usage = null)
    {
        var metadata = new Dictionary<string, string>
        {
            ["model"] = config.Model,
            ["endpoint"] = config.Endpoint,
            ["message_count"] = messageCount.ToString(),
            ["tool_count"] = toolCount.ToString(),
            ["agent_template_id"] = agentTemplateId,
        };

        if (!string.IsNullOrWhiteSpace(config.ReasoningEffort))
            metadata["reasoning_effort"] = config.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(config.ThinkingMode))
            metadata["thinking_mode"] = config.ThinkingMode;

        if (usage is not null)
        {
            AddIfPresent(metadata, "prompt_tokens", usage.PromptTokens);
            AddIfPresent(metadata, "completion_tokens", usage.CompletionTokens);
            AddIfPresent(metadata, "total_tokens", usage.TotalTokens);
            AddIfPresent(metadata, "prompt_cache_hit_tokens", usage.PromptCacheHitTokens);
            AddIfPresent(metadata, "prompt_cache_miss_tokens", usage.PromptCacheMissTokens);
        }

        return metadata;
    }

    private static void AddIfPresent(Dictionary<string, string> metadata, string key, int? value)
    {
        if (value.HasValue)
            metadata[key] = value.Value.ToString();
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

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

    private sealed record ResolvedGatewayConfig(
        string Endpoint,
        string ApiKey,
        string Model,
        string? ReasoningEffort,
        string? ThinkingMode);

    private sealed class ProxyTool(LlmToolDefinition dto) : ITool
    {
        public string Name => dto.Name;
        public string Description => dto.Description;
        public ToolParameterSchema Parameters => dto.Parameters;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => throw new NotSupportedException("Proxy tool definitions are only for function schema transport.");
    }
}
