using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;

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
    private readonly ITelemetryMetricSink? _telemetrySink;
    private readonly IRuntimeTraceAccessor? _traceAccessor;
    private readonly ProviderRateLimiter? _rateLimiter;
    private readonly ConcurrentDictionary<string, CircuitBreakerState> _circuitBreakers = new();
    private readonly IVisualArtifactResolver? _visualArtifactResolver;
    private readonly IRuntimeExecutionConfigService? _executionConfig;

    public DirectLlmClient(
    IHttpClientFactory httpClientFactory,
    ILlmConfigService llmConfigService,
    ILogger<DirectLlmClient> logger,
    IKeyVaultService? keyVaultService = null,
    IRuntimeActivitySink? activitySink = null,
    ITelemetryMetricSink? telemetrySink = null,
    IRuntimeTraceAccessor? traceAccessor = null,
    ProviderRateLimiter? rateLimiter = null,
    IVisualArtifactResolver? visualArtifactResolver = null,
    IRuntimeExecutionConfigService? executionConfig = null)
    {
        _httpClientFactory = httpClientFactory;
        _llmConfigService = llmConfigService;
        _logger = logger;
        _keyVaultService = keyVaultService;
        _activitySink = activitySink;
        _telemetrySink = telemetrySink;
        _traceAccessor = traceAccessor;
        _rateLimiter = rateLimiter;
        _visualArtifactResolver = visualArtifactResolver;
        _executionConfig = executionConfig;
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
        var gateway = CreateGateway(config, workspaceId);
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
        _logger.LogDebug(
            "[DirectLlm:Tools] Final chat request tools workspace={WorkspaceId} session={SessionId} template={TemplateId} provider={Provider} model={Model} toolCount={ToolCount} tools={Tools}",
            workspaceId,
            sessionId,
            agentTemplateId,
            config.ProviderId,
            config.Model,
            toolSpecs.Count,
            SummarizeToolSpecs(toolSpecs));

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
        var timeoutSeconds = (_executionConfig?.GetOptions().Turns ?? new TurnExecutionOptions())
            .LlmFirstChunkTimeoutSeconds;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            CancellationToken effectiveCt = linkedCts.Token;

            try
            {
                // ── 并发限流（按模型粒度，同一 Provider 不同模型独立限流）──
                var result = _rateLimiter is not null
                    ? await _rateLimiter.ExecuteAsync(config.ProviderId, config.Model,
                        () => gateway.ChatAsync(messages, toolSpecs, effectiveCt), effectiveCt)
                    : await gateway.ChatAsync(messages, toolSpecs, effectiveCt);
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
        var gateway = CreateGateway(config, workspaceId);
        var toolSpecs = ToToolSpecs(tools);
        var trace = ResolveTrace(workspaceId, sessionId);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var strategy = config.Strategy;

        // 首块预算覆盖并发队列、网络握手和第一次 provider 输出；收到首块后停止该计时器。
        // 后续只使用相邻块空闲窗口，不再设置固定流总时长。
        var turnOptions = _executionConfig?.GetOptions().Turns ?? new TurnExecutionOptions();
        var providerIdleCeilingSeconds = Math.Max(1, strategy.EffectiveStreamTimeoutSeconds);
        var firstChunkTimeoutSeconds = Math.Min(
            turnOptions.LlmFirstChunkTimeoutSeconds,
            providerIdleCeilingSeconds);
        var streamIdleTimeoutSeconds = Math.Min(
            turnOptions.LlmStreamIdleTimeoutSeconds,
            providerIdleCeilingSeconds);
        using var firstChunkTimeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(firstChunkTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            firstChunkTimeoutCts.Token);
        var effectiveCt = linkedCts.Token;

        _logger.LogInformation(
            "[DirectLlm] STREAM model={Model} endpoint={Endpoint} msgCount={Count} toolCount={ToolCount} thinkingMode={ThinkingMode} provider={Provider}",
            config.Model,
            config.Endpoint,
            messages.Count,
            toolSpecs.Count,
            config.ThinkingMode ?? "(null)",
            config.ProviderId);
        _logger.LogDebug(
            "[DirectLlm:Tools] Final streaming request tools workspace={WorkspaceId} session={SessionId} template={TemplateId} provider={Provider} model={Model} toolCount={ToolCount} tools={Tools}",
            workspaceId,
            sessionId,
            agentTemplateId,
            config.ProviderId,
            config.Model,
            toolSpecs.Count,
            SummarizeToolSpecs(toolSpecs));

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
        var streamDiagnostics = new LlmStreamDiagnosticsAccumulator();
        var maxRetries = strategy.EffectiveMaxRetries;
        var retryDelay = TimeSpan.FromSeconds(strategy.EffectiveRetryDelaySeconds);
        var retryAttempt = 0;
        var hasYieldedDelta = false;

        // ── 并发限流（流式请求在整个流生命周期内持有槽位）──
        var rateLimitLease = _rateLimiter is not null
            ? await _rateLimiter.AcquireAsync(config.ProviderId, config.Model, effectiveCt)
            : null;
        using var _ = rateLimitLease;

        IAsyncEnumerator<StreamDelta>? enumerator = null;

        try
        {
            while (true)
            {
                using var watchdog = new StreamWatchdog(
                    TimeSpan.FromSeconds(firstChunkTimeoutSeconds),
                    TimeSpan.FromSeconds(streamIdleTimeoutSeconds),
                    _logger,
                    config.ProviderId);
                using var watchdogLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(effectiveCt, watchdog.Token);
                watchdog.Start();

                enumerator = gateway.ChatStreamAsync(messages, toolSpecs, watchdogLinkedCts.Token)
                    .GetAsyncEnumerator(watchdogLinkedCts.Token);
                Exception? retryException = null;

                while (true)
                {
                    StreamDelta delta;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                            break;

                        delta = enumerator.Current;
                        streamDiagnostics.Observe(delta);
                    }
                    catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
                    {
                        terminalRecorded = true;
                        sw.Stop();
                        var metadata = MergeMetadata(
                            BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                            streamDiagnostics.ToMetadata());
                        await RecordActivityAsync(
                            trace,
                            operation: "chat_stream",
                            status: RuntimeActivityStatuses.Cancelled,
                            startedAt,
                            endedAt: DateTimeOffset.UtcNow,
                            durationMs: sw.ElapsedMilliseconds,
                            summary: "Direct LLM streaming request cancelled.",
                            metadata,
                            error: ex,
                            CancellationToken.None);
                        await RecordLlmStreamDiagnosticsMetricsAsync(trace, streamDiagnostics, RuntimeActivityStatuses.Cancelled, CancellationToken.None);
                        throw;
                    }
                    catch (OperationCanceledException ex) when (firstChunkTimeoutCts.IsCancellationRequested)
                    {
                        terminalRecorded = true;
                        sw.Stop();
                        _logger.LogError(ex, "[DirectLlm] STREAM FIRST CHUNK TIMEOUT provider={Provider} elapsed={Elapsed}ms",
                            config.ProviderId, sw.ElapsedMilliseconds);
                        var metadata = MergeMetadata(
                            BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                            streamDiagnostics.ToMetadata());
                        await RecordActivityAsync(
                            trace,
                            operation: "chat_stream",
                            status: RuntimeActivityStatuses.Failed,
                            startedAt,
                            endedAt: DateTimeOffset.UtcNow,
                            durationMs: sw.ElapsedMilliseconds,
                            summary: "Direct LLM stream produced no first chunk before its deadline.",
                            metadata,
                            error: ex,
                            CancellationToken.None);
                        await RecordLlmStreamDiagnosticsMetricsAsync(
                            trace,
                            streamDiagnostics,
                            RuntimeActivityStatuses.Failed,
                            CancellationToken.None);
                        throw;
                    }
                    catch (OperationCanceledException ex) when (
                        watchdog.Token.IsCancellationRequested &&
                        !hasYieldedDelta &&
                        retryAttempt < maxRetries)
                    {
                        retryException = new TimeoutException(
                            "LLM stream produced no first chunk before the watchdog deadline.",
                            ex);
                        break;
                    }
                    catch (OperationCanceledException ex) when (watchdog.Token.IsCancellationRequested)
                    {
                        terminalRecorded = true;
                        sw.Stop();
                        _logger.LogError(ex, "[DirectLlm] STREAM IDLE TIMEOUT provider={Provider} elapsed={Elapsed}ms",
                            config.ProviderId, sw.ElapsedMilliseconds);
                        var metadata = MergeMetadata(
                            BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                            streamDiagnostics.ToMetadata());
                        await RecordActivityAsync(
                            trace,
                            operation: "chat_stream",
                            status: RuntimeActivityStatuses.Failed,
                            startedAt,
                            endedAt: DateTimeOffset.UtcNow,
                            durationMs: sw.ElapsedMilliseconds,
                            summary: "Direct LLM stream idle watchdog timed out.",
                            metadata,
                            error: ex,
                            CancellationToken.None);
                        await RecordLlmStreamDiagnosticsMetricsAsync(trace, streamDiagnostics, RuntimeActivityStatuses.Failed, CancellationToken.None);
                        throw new TimeoutException(
                            "LLM stream stopped producing chunks before the watchdog deadline.",
                            ex);
                    }
                    catch (OperationCanceledException ex) when (
                        !hasYieldedDelta &&
                        retryAttempt < maxRetries &&
                        IsTransientError(ex, isTimeout: true))
                    {
                        retryException = ex;
                        break;
                    }
                    catch (OperationCanceledException ex)
                    {
                        // HttpClient.Timeout 触发的 TaskCanceledException
                        // （既不是外部取消也不是流式超时策略，归类为 HTTP 层超时）
                        terminalRecorded = true;
                        sw.Stop();
                        _logger.LogError(ex, "[DirectLlm] STREAM TRANSPORT CANCELLATION provider={Provider} elapsed={Elapsed}ms",
                            config.ProviderId, sw.ElapsedMilliseconds);
                        var metadata = MergeMetadata(
                            BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                            streamDiagnostics.ToMetadata());
                        await RecordActivityAsync(
                            trace,
                            operation: "chat_stream",
                            status: RuntimeActivityStatuses.Failed,
                            startedAt,
                            endedAt: DateTimeOffset.UtcNow,
                            durationMs: sw.ElapsedMilliseconds,
                            summary: "Direct LLM streaming transport was cancelled.",
                            metadata,
                            error: ex,
                            CancellationToken.None);
                        await RecordLlmStreamDiagnosticsMetricsAsync(trace, streamDiagnostics, RuntimeActivityStatuses.Failed, CancellationToken.None);
                        throw;
                    }
                    catch (Exception ex) when (
                        !hasYieldedDelta &&
                        retryAttempt < maxRetries &&
                        IsTransientError(ex, isTimeout: false))
                    {
                        retryException = ex;
                        break;
                    }
                    catch (Exception ex)
                    {
                        terminalRecorded = true;
                        sw.Stop();
                        _logger.LogError(ex, "[DirectLlm] STREAM ERROR elapsed={Elapsed}ms", sw.ElapsedMilliseconds);
                        var metadata = MergeMetadata(
                            BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                            streamDiagnostics.ToMetadata());
                        await RecordActivityAsync(
                            trace,
                            operation: "chat_stream",
                            status: RuntimeActivityStatuses.Failed,
                            startedAt,
                            endedAt: DateTimeOffset.UtcNow,
                            durationMs: sw.ElapsedMilliseconds,
                            summary: "Direct LLM streaming request failed.",
                            metadata,
                            error: ex,
                            CancellationToken.None);
                        await RecordLlmStreamDiagnosticsMetricsAsync(trace, streamDiagnostics, RuntimeActivityStatuses.Failed, CancellationToken.None);
                        throw;
                    }

                    if (!hasYieldedDelta)
                    {
                        _logger.LogDebug(
                            "[DirectLlm] STREAM FIRST_CHUNK provider={Provider} model={Model} ttftMs={TtftMs}",
                            config.ProviderId, config.Model, sw.ElapsedMilliseconds);
                    }
                    hasYieldedDelta = true;
                    firstChunkTimeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
                    watchdog.Feed();
                    yield return delta;
                }

                if (retryException is null)
                    break;

                await enumerator.DisposeAsync();
                enumerator = null;
                retryAttempt++;
                _logger.LogWarning(
                    retryException,
                    "[DirectLlm] STREAM RETRY before first delta attempt={Attempt}/{Max} provider={Provider}",
                    retryAttempt,
                    maxRetries,
                    config.ProviderId);
                var retryMetadata = MergeMetadata(
                    BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                    streamDiagnostics.ToMetadata());
                await RecordActivityAsync(
                    trace,
                    operation: "chat_stream",
                    status: RuntimeActivityStatuses.Retried,
                    startedAt,
                    endedAt: DateTimeOffset.UtcNow,
                    durationMs: sw.ElapsedMilliseconds,
                    summary: $"LLM stream retry before first delta {retryAttempt}/{maxRetries}.",
                    retryMetadata,
                    error: retryException,
                    CancellationToken.None);
                await Task.Delay(retryDelay, ct);
            }

            terminalRecorded = true;
            sw.Stop();
            var succeededMetadata = MergeMetadata(
                BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                streamDiagnostics.ToMetadata());
            await RecordActivityAsync(
                trace,
                operation: "chat_stream",
                status: RuntimeActivityStatuses.Succeeded,
                startedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: sw.ElapsedMilliseconds,
                summary: "Direct LLM streaming request completed.",
                succeededMetadata,
                error: null,
                CancellationToken.None);
            await RecordLlmStreamDiagnosticsMetricsAsync(trace, streamDiagnostics, RuntimeActivityStatuses.Succeeded, CancellationToken.None);
        }
        finally
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync();

            if (!terminalRecorded)
            {
                sw.Stop();
                var metadata = MergeMetadata(
                    BuildMetadata(config, agentTemplateId, messages.Count, toolSpecs.Count),
                    streamDiagnostics.ToMetadata());
                await RecordActivityAsync(
                    trace,
                    operation: "chat_stream",
                    status: RuntimeActivityStatuses.Cancelled,
                    startedAt,
                    endedAt: DateTimeOffset.UtcNow,
                    durationMs: sw.ElapsedMilliseconds,
                    summary: "Direct LLM streaming request ended before completion.",
                    metadata,
                    error: null,
                    CancellationToken.None);
                await RecordLlmStreamDiagnosticsMetricsAsync(trace, streamDiagnostics, RuntimeActivityStatuses.Cancelled, CancellationToken.None);
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

        var endpoint = effectiveRequestConfig?.Endpoint
            ?? throw new InvalidOperationException("Agent LLM endpoint is not configured. Ensure PreferredProviderId is set on the agent.");
        var apiKey = effectiveRequestConfig?.ApiKey
            ?? throw new InvalidOperationException("Agent LLM API key is not configured.");
        var model = effectiveRequestConfig?.ModelId
            ?? throw new InvalidOperationException("Agent LLM model is not configured.");
        var reasoningEffort = effectiveRequestConfig?.ReasoningEffort; // optional
        var thinkingMode = (string?)null;

        // 通过 endpoint 匹配 provider，获取策略配置
        var enabledProviders = _llmConfigService.GetEnabledProviders().ToList();
        var matched = enabledProviders.FirstOrDefault(p =>
            endpoint.StartsWith(p.BaseUrl, StringComparison.OrdinalIgnoreCase));
        if (matched is null)
        {
            throw new InvalidOperationException(
                $"Cannot identify LLM provider from endpoint '{endpoint}'. " +
                $"Ensure the endpoint matches one of the enabled providers in data/config/llm.providers.json. " +
                $"Available providers: {string.Join(", ", enabledProviders.Select(p => $"{p.ProviderId}({p.BaseUrl})"))}");
        }

        var strategy = _llmConfigService.GetProviderStrategy(matched.ProviderId) ?? LlmProviderStrategy.Default;
        var supportsVision = _llmConfigService.GetAllModels().Any(candidate =>
            string.Equals(candidate.ProviderId, matched.ProviderId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.ModelId, model, StringComparison.OrdinalIgnoreCase)
            && candidate.CapabilityTags.Contains("vision", StringComparer.OrdinalIgnoreCase));

        return new ResolvedGatewayConfig(
            endpoint,
            apiKey,
            model,
            reasoningEffort,
            thinkingMode,
            matched.ProviderId,
            strategy,
            supportsVision);
    }

    private OpenAiLlmGateway CreateGateway(ResolvedGatewayConfig config, string workspaceId)
    {
        var gateway = new OpenAiLlmGateway(
            _httpClientFactory.CreateClient("DirectLlm"),
            new PuddingCode.Platform.Options.LlmOptions(
                config.Endpoint,
                config.ApiKey,
                config.Model,
                ReasoningEffort: config.ReasoningEffort,
                ThinkingMode: config.ThinkingMode));
        gateway.Compat = config.Strategy.Compat;
        // Historical ChatMessage instances may still carry artifact ids after a
        // visual turn. Only a model explicitly tagged as vision-capable may
        // receive OpenAI image content parts; text-only providers keep string
        // content and therefore cannot be poisoned by an earlier image turn.
        gateway.VisualArtifactResolver = config.SupportsVision
            ? _visualArtifactResolver
            : null;
        gateway.WorkspaceId = workspaceId;
        return gateway;
    }

    private static List<ITool> ToToolSpecs(IReadOnlyList<LlmToolDefinition>? tools)
        => (tools ?? []).Select(t => (ITool)new ProxyTool(t)).ToList();

    private static string SummarizeToolSpecs(IReadOnlyList<ITool> tools)
        => tools.Count == 0
            ? ""
            : string.Join(",", tools.Select(t => t.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

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
        {
            await RecordLlmMetricAsync(trace, operation, status, startedAt, durationMs, summary, metadata, error, ct);
            return;
        }

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

            await RecordLlmMetricAsync(trace, operation, status, startedAt, durationMs, summary, metadata, error, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Request cancellation must not mask the original LLM cancellation.
        }
    }

    private async Task RecordLlmMetricAsync(
        RuntimeTraceContext trace,
        string operation,
        string status,
        DateTimeOffset occurredAtUtc,
        long? durationMs,
        string summary,
        IReadOnlyDictionary<string, string> metadata,
        Exception? error,
        CancellationToken ct)
    {
        if (_telemetrySink is null)
            return;

        try
        {
            await _telemetrySink.RecordAsync(new TelemetryMetric
            {
                Trace = trace,
                Source = "backend",
                Category = TelemetryMetricCategories.Llm,
                Name = $"llm.{operation}",
                Status = status,
                OccurredAtUtc = occurredAtUtc,
                DurationMs = durationMs,
                CountValue = 1,
                Unit = "call",
                Severity = error is null ? "info" : "error",
                Summary = summary,
                Dimensions = metadata,
                ErrorCode = error?.GetType().Name,
                ErrorMessage = error?.Message,
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Request cancellation must not mask the original LLM cancellation.
        }
    }

    private async Task RecordLlmStreamDiagnosticsMetricsAsync(
        RuntimeTraceContext trace,
        LlmStreamDiagnosticsAccumulator diagnostics,
        string status,
        CancellationToken ct)
    {
        if (_telemetrySink is null || diagnostics.ChunkCount == 0)
            return;

        foreach (var metric in diagnostics.ToMetrics(trace, status))
        {
            try
            {
                await _telemetrySink.RecordAsync(metric, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Telemetry must not mask the original cancellation.
            }
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
            ["provider_id"] = config.ProviderId,
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

    private static IReadOnlyDictionary<string, string> MergeMetadata(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        if (second.Count == 0)
            return first;

        var merged = new Dictionary<string, string>(first);
        foreach (var pair in second)
            merged[pair.Key] = pair.Value;
        return merged;
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
    internal static bool IsTransientError(Exception ex, bool isTimeout)
    {
        return ex switch
        {
            HttpRequestException { StatusCode: { } statusCode } => (int)statusCode >= 500,
            HttpRequestException hrex => hrex.InnerException is not null && IsTransientTransportError(hrex.InnerException),
            OperationCanceledException => isTimeout, // timeout 可重试，外部取消不可重试
            _ => IsTransientTransportError(ex),
        };
    }

    private static bool IsTransientTransportError(Exception ex)
        => ex is HttpIOException or IOException or SocketException
           || ex.InnerException is not null && IsTransientTransportError(ex.InnerException);

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
        LlmProviderStrategy Strategy,
        bool SupportsVision);

    private sealed class LlmStreamDiagnosticsAccumulator
    {
        private long _readTotalMs;
        private long _readMaxMs;
        private long _gapTotalMs;
        private long _gapMaxMs;
        private long _gapCount;
        private long _parseTotalMs;
        private long _parseMaxMs;
        private long _payloadChars;
        private long _contentChars;
        private long _reasoningChars;
        private long _toolDeltaCount;
                private long _usageChunkCount;
        private long _timeToFirstTokenMs;

        public long ChunkCount { get; private set; }

                public long TimeToFirstTokenMs => _timeToFirstTokenMs;

        public void Observe(StreamDelta delta)
        {
            if (ChunkCount == 0)
            {
                _timeToFirstTokenMs = delta.ProviderReadMs ?? 0;
            }
            ChunkCount++;
            ObserveValue(delta.ProviderReadMs, ref _readTotalMs, ref _readMaxMs);
            if (delta.ProviderChunkGapMs.HasValue)
            {
                _gapCount++;
                ObserveValue(delta.ProviderChunkGapMs, ref _gapTotalMs, ref _gapMaxMs);
            }
            ObserveValue(delta.GatewayParseMs, ref _parseTotalMs, ref _parseMaxMs);
            _payloadChars += delta.ProviderPayloadChars ?? 0;
            _contentChars += delta.ContentDelta?.Length ?? 0;
            _reasoningChars += delta.ReasoningDelta?.Length ?? 0;
            if (delta.ToolCallIndex.HasValue)
                _toolDeltaCount++;
            if (delta.Usage is not null)
                _usageChunkCount++;
        }

        public IReadOnlyDictionary<string, string> ToMetadata()
        {
            if (ChunkCount == 0)
                return new Dictionary<string, string>();

            return new Dictionary<string, string>
            {
                ["stream_chunk_count"] = ChunkCount.ToString(),
                ["stream_provider_read_avg_ms"] = Average(_readTotalMs, ChunkCount).ToString("0.###"),
                ["stream_provider_read_max_ms"] = _readMaxMs.ToString(),
                ["stream_provider_gap_avg_ms"] = Average(_gapTotalMs, _gapCount).ToString("0.###"),
                ["stream_provider_gap_max_ms"] = _gapMaxMs.ToString(),
                ["stream_gateway_parse_avg_ms"] = Average(_parseTotalMs, ChunkCount).ToString("0.###"),
                ["stream_gateway_parse_max_ms"] = _parseMaxMs.ToString(),
                ["stream_provider_payload_chars"] = _payloadChars.ToString(),
                ["stream_content_chars"] = _contentChars.ToString(),
                ["stream_reasoning_chars"] = _reasoningChars.ToString(),
                ["stream_tool_delta_count"] = _toolDeltaCount.ToString(),
                                ["stream_usage_chunk_count"] = _usageChunkCount.ToString(),
                ["stream_ttft_ms"] = _timeToFirstTokenMs.ToString(),
            };
        }

        public IEnumerable<TelemetryMetric> ToMetrics(RuntimeTraceContext trace, string status)
        {
            var metadata = ToMetadata();
            var debugJson = JsonSerializer.Serialize(metadata);
            yield return BuildMetric(
                trace,
                "llm.stream.provider_chunk_read",
                status,
                count: ChunkCount,
                numericValue: Average(_readTotalMs, ChunkCount),
                maxMs: _readMaxMs,
                summary: "Provider SSE chunk read latency.",
                debugJson);
            yield return BuildMetric(
                trace,
                "llm.stream.provider_chunk_gap",
                status,
                count: _gapCount,
                numericValue: Average(_gapTotalMs, _gapCount),
                maxMs: _gapMaxMs,
                summary: "Gap between provider SSE data chunks.",
                debugJson);
            yield return BuildMetric(
                trace,
                "llm.stream.gateway_parse",
                status,
                count: ChunkCount,
                numericValue: Average(_parseTotalMs, ChunkCount),
                maxMs: _parseMaxMs,
                summary: "Gateway provider chunk parse latency.",
                debugJson);
        }

        private static TelemetryMetric BuildMetric(
            RuntimeTraceContext trace,
            string name,
            string status,
            long count,
            double numericValue,
            long maxMs,
            string summary,
            string debugJson)
            => new()
            {
                Trace = trace,
                Source = "backend",
                Category = TelemetryMetricCategories.Llm,
                Name = name,
                Status = status,
                CountValue = count,
                NumericValue = numericValue,
                DurationMs = maxMs,
                Unit = "ms",
                Summary = summary,
                DebugJson = debugJson,
            };

        private static void ObserveValue(long? value, ref long total, ref long max)
        {
            if (!value.HasValue)
                return;

            total += value.Value;
            if (value.Value > max)
                max = value.Value;
        }

        private static double Average(long total, long count)
            => count <= 0 ? 0 : (double)total / count;
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
