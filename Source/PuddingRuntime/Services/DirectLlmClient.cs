using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, CircuitBreakerState> _circuitBreakers = new();

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
        var strategy = config.Strategy;

        _logger.LogInformation(
            "[DirectLlm] REQUEST model={Model} endpoint={Endpoint} msgCount={Count} toolCount={ToolCount} thinkingMode={ThinkingMode} provider={Provider}",
            config.Model,
            config.Endpoint,
            messages.Count,
            toolSpecs.Count,
            config.ThinkingMode ?? "(null)",
            config.ProviderId);

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

        // ── 熔断检查 ──────────────────────────────────────
        var circuit = _circuitBreakers.GetOrAdd(config.ProviderId, _ => new CircuitBreakerState());
        var circuitRejected = false;
        DateTimeOffset recoveryTime = default;
        lock (circuit)
        {
            if (circuit.State == CircuitState.Open)
            {
                if (DateTimeOffset.UtcNow < circuit.RecoveryTime)
                {
                    circuitRejected = true;
                    recoveryTime = circuit.RecoveryTime;
                }
                else
                {
                    circuit.TransitionToHalfOpen();
                    _logger.LogInformation("[DirectLlm] CIRCUIT_HALF_OPEN provider={Provider}", config.ProviderId);
                }
            }
        }

        if (circuitRejected)
        {
            sw.Stop();
            _logger.LogWarning("[DirectLlm] CIRCUIT_OPEN provider={Provider} recovery={RecoveryTime}",
                config.ProviderId, recoveryTime);
            await RecordActivityAsync(
                trace,
                operation: "chat",
                status: RuntimeActivityStatuses.Cancelled,
                startedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: sw.ElapsedMilliseconds,
                summary: $"Circuit breaker open for provider '{config.ProviderId}'.",
                metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                error: null,
                CancellationToken.None);
            throw new CircuitBreakerOpenException(config.ProviderId, recoveryTime);
        }

        // ── 重试循环 ──────────────────────────────────────
        var maxRetries = strategy.EffectiveMaxRetries;
        var retryDelay = TimeSpan.FromSeconds(strategy.EffectiveRetryDelaySeconds);
        var timeoutSeconds = strategy.EffectiveRequestTimeoutSeconds;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            CancellationToken effectiveCt = linkedCts.Token;

            try
            {
                var result = await gateway.ChatAsync(messages, toolSpecs, effectiveCt);
                sw.Stop();

                // 成功 → 重置熔断
                lock (circuit) { circuit.RecordSuccess(); }

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
                // 外部取消 — 不重试
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
            catch (Exception ex) when (IsTransientError(ex, timeoutCts.IsCancellationRequested))
            {
                // 瞬态错误 → 记录熔断失败
                var isTimeout = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
                lock (circuit)
                {
                    circuit.RecordFailure(
                        strategy.EffectiveCircuitBreakerFailureThreshold,
                        strategy.EffectiveCircuitBreakerRecoverySeconds);
                }

                if (attempt < maxRetries)
                {
                    _logger.LogWarning(ex,
                        "[DirectLlm] RETRY attempt={Attempt}/{Max} provider={Provider}",
                        attempt + 1, maxRetries, config.ProviderId);

                    await RecordActivityAsync(
                        trace,
                        operation: "chat",
                        status: RuntimeActivityStatuses.Retried,
                        startedAt,
                        endedAt: DateTimeOffset.UtcNow,
                        durationMs: sw.ElapsedMilliseconds,
                        summary: $"LLM call retry {attempt + 1}/{maxRetries}.",
                        metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                        error: ex,
                        CancellationToken.None);

                    await Task.Delay(retryDelay, ct);
                    continue;
                }

                // 重试耗尽
                sw.Stop();
                var errorCode = isTimeout ? "timeout" : ex.GetType().Name;
                _logger.LogError(ex, "[DirectLlm] FAILED provider={Provider} elapsed={Elapsed}ms errorCode={ErrorCode}",
                    config.ProviderId, sw.ElapsedMilliseconds, errorCode);

                await RecordActivityAsync(
                    trace,
                    operation: "chat",
                    status: RuntimeActivityStatuses.Failed,
                    startedAt,
                    endedAt: DateTimeOffset.UtcNow,
                    durationMs: sw.ElapsedMilliseconds,
                    summary: $"LLM call failed after {maxRetries} retries.",
                    metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                    error: ex,
                    CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                // 非瞬态错误（如 HTTP 4xx）→ 不重试
                sw.Stop();
                lock (circuit) { circuit.RecordSuccess(); } // 4xx 不是 provider 故障

                _logger.LogError(ex, "[DirectLlm] NON_RETRYABLE provider={Provider} elapsed={Elapsed}ms",
                    config.ProviderId, sw.ElapsedMilliseconds);

                await RecordActivityAsync(
                    trace,
                    operation: "chat",
                    status: RuntimeActivityStatuses.Failed,
                    startedAt,
                    endedAt: DateTimeOffset.UtcNow,
                    durationMs: sw.ElapsedMilliseconds,
                    summary: "Direct LLM chat request failed (non-retryable).",
                    metadata: BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                    error: ex,
                    CancellationToken.None);
                throw;
            }
        }

        // 理论上不会到这里（循环内必定抛出或返回）
        throw new InvalidOperationException("Retry loop exhausted unexpectedly.");
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
        var strategy = config.Strategy;

        // 流式超时：整个流生命周期
        using var streamTimeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(strategy.EffectiveStreamTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, streamTimeoutCts.Token);
        var effectiveCt = linkedCts.Token;

        _logger.LogInformation(
            "[DirectLlm] STREAM model={Model} endpoint={Endpoint} msgCount={Count} toolCount={ToolCount} thinkingMode={ThinkingMode} provider={Provider}",
            config.Model,
            config.Endpoint,
            messages.Count,
            toolSpecs.Count,
            config.ThinkingMode ?? "(null)",
            config.ProviderId);

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
        var enumerator = gateway.ChatStreamAsync(messages, toolSpecs, effectiveCt).GetAsyncEnumerator(effectiveCt);

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
                catch (OperationCanceledException ex) when (streamTimeoutCts.IsCancellationRequested)
                {
                    terminalRecorded = true;
                    sw.Stop();
                    _logger.LogError(ex, "[DirectLlm] STREAM TIMEOUT provider={Provider} elapsed={Elapsed}ms",
                        config.ProviderId, sw.ElapsedMilliseconds);
                    await RecordActivityAsync(
                        trace,
                        operation: "chat_stream",
                        status: RuntimeActivityStatuses.Failed,
                        startedAt,
                        endedAt: DateTimeOffset.UtcNow,
                        durationMs: sw.ElapsedMilliseconds,
                        summary: "Direct LLM streaming timeout.",
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
            defaultConfig?.Endpoint);
        var apiKey = FirstNonBlank(
            effectiveRequestConfig?.ApiKey,
            defaultConfig?.ApiKey);
        var model = FirstNonBlank(
            effectiveRequestConfig?.ModelId,
            defaultConfig?.ModelId);
        var reasoningEffort = FirstNonBlank(
            effectiveRequestConfig?.ReasoningEffort,
            defaultConfig?.ReasoningEffort);
        var thinkingMode = (string?)null;

        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("LLM endpoint not configured in file-backed LLM config.");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("LLM model not configured in file-backed LLM config.");

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("LLM API key not configured in file-backed LLM config.");

        // 通过 endpoint 匹配 provider，获取策略配置
        var providerId = "unknown";
        LlmProviderStrategy strategy = LlmProviderStrategy.Default;
        foreach (var p in _llmConfigService.GetEnabledProviders())
        {
            if (endpoint.StartsWith(p.BaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                providerId = p.ProviderId;
                strategy = _llmConfigService.GetProviderStrategy(p.ProviderId) ?? LlmProviderStrategy.Default;
                break;
            }
        }

        return new ResolvedGatewayConfig(
            endpoint,
            apiKey,
            model,
            reasoningEffort,
            thinkingMode,
            providerId,
            strategy);
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

    /// <summary>
    /// 判断是否为可重试的瞬态错误（HTTP 5xx、timeout、网络错误）。
    /// HTTP 4xx（认证失败、rate limit 等）不可重试。
    /// </summary>
    private static bool IsTransientError(Exception ex, bool isTimeout)
    {
        return ex switch
        {
            HttpRequestException hrex => (int?)hrex.StatusCode >= 500,
            OperationCanceledException => isTimeout, // timeout 可重试，外部取消不可重试
            _ => true, // 其他未知网络错误视为瞬态
        };
    }

    // ── Circuit Breaker Types ──────────────────────────────────────

    private enum CircuitState { Closed, Open, HalfOpen }

    private sealed class CircuitBreakerState
    {
        public CircuitState State { get; private set; } = CircuitState.Closed;
        public int FailureCount { get; private set; }
        public DateTimeOffset LastFailureTime { get; private set; }
        public DateTimeOffset RecoveryTime { get; private set; }

        public void RecordFailure(int failureThreshold, int recoverySeconds)
        {
            FailureCount++;
            LastFailureTime = DateTimeOffset.UtcNow;
            if (FailureCount >= failureThreshold)
            {
                State = CircuitState.Open;
                RecoveryTime = DateTimeOffset.UtcNow.AddSeconds(recoverySeconds);
            }
        }

        public void RecordSuccess()
        {
            State = CircuitState.Closed;
            FailureCount = 0;
        }

        public void TransitionToHalfOpen()
        {
            State = CircuitState.HalfOpen;
        }
    }

    private sealed class CircuitBreakerOpenException : InvalidOperationException
    {
        public string ProviderId { get; }
        public DateTimeOffset RecoveryTime { get; }

        public CircuitBreakerOpenException(string providerId, DateTimeOffset recoveryTime)
            : base($"Circuit breaker is open for provider '{providerId}'. Recovery expected at {recoveryTime:O}.")
        {
            ProviderId = providerId;
            RecoveryTime = recoveryTime;
        }
    }

    private sealed record ResolvedGatewayConfig(
        string Endpoint,
        string ApiKey,
        string Model,
        string? ReasoningEffort,
        string? ThinkingMode,
        string ProviderId,
        LlmProviderStrategy Strategy);

    private sealed class ProxyTool(LlmToolDefinition dto) : ITool
    {
        public string Name => dto.Name;
        public string Description => dto.Description;
        public ToolParameterSchema Parameters => dto.Parameters;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => throw new NotSupportedException("Proxy tool definitions are only for function schema transport.");
    }
}
