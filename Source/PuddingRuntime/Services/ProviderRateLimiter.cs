using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// 基于 Provider + Model 的并发限流器。
/// 每个 (ProviderId, ModelId) 组合独立维护一个 SemaphoreSlim。
/// 因为同一服务商不同模型的 RPM/并发限制不同（如 DeepSeek V4 Flash vs Pro）。
/// 用于保护 LLM API 不触发 429 Too Many Requests。
/// </summary>
public sealed class ProviderRateLimiter
{
    private readonly ILlmConfigService _configService;
    private readonly ILogger<ProviderRateLimiter> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    private const int DefaultMaxConcurrent = 50;

    public ProviderRateLimiter(
        ILlmConfigService configService,
        ILogger<ProviderRateLimiter> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// 在指定 Provider + Model 的并发配额内执行异步操作。
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        string providerId,
        string modelId,
        Func<Task<T>> action,
        CancellationToken ct = default)
    {
        using var lease = await AcquireAsync(providerId, modelId, ct);
        return await action();
    }

    /// <summary>
    /// 获取并发配额并返回可释放的租约，适用于流式等长时间操作。
    /// 调用方用 using 包裹即可在流结束时自动释放。
    /// </summary>
    public async Task<IDisposable> AcquireAsync(
        string providerId,
        string modelId,
        CancellationToken ct = default)
    {
        var gate = GetOrCreateGate(providerId, modelId);
        var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        await gate.WaitAsync(ct);
        var waitMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;

        if (waitMs > 10)
        {
            _logger.LogInformation(
                "[Runtime:RateLimiter] {ProviderId}/{ModelId} acquired concurrency slot, waited {WaitMs:F1}ms",
                providerId, modelId, waitMs);
        }

        return new GateLease(gate);
    }

    /// <summary>向后兼容：仅指定 Provider 时使用默认模型级信号量。</summary>
    public Task<T> ExecuteAsync<T>(
        string providerId,
        Func<Task<T>> action,
        CancellationToken ct = default)
        => ExecuteAsync(providerId, "default", action, ct);

    /// <summary>向后兼容：仅指定 Provider 时使用默认模型级信号量。</summary>
    public Task ExecuteAsync(
        string providerId,
        Func<Task> action,
        CancellationToken ct = default)
        => ExecuteAsync(providerId, "default", action, ct);

    public async Task ExecuteAsync(
        string providerId,
        string modelId,
        Func<Task> action,
        CancellationToken ct = default)
    {
        using var lease = await AcquireAsync(providerId, modelId, ct);
        await action();
    }

    public (int maxConcurrent, int currentAvailable) GetStatus(string providerId, string modelId)
    {
        // max 从配置获取（热重载会刷新配置，但不会刷新已缓存的 gate）
        var strategy = _configService.GetModelStrategy(providerId, modelId) ?? _configService.GetProviderStrategy(providerId);
        var max = strategy?.EffectiveMaxConcurrentRequests ?? DefaultMaxConcurrent;

        var currentAvailable = _gates.TryGetValue(MakeKey(providerId, modelId), out var gate)
            ? gate.CurrentCount
            : max;
        return (max, currentAvailable);
    }

    private static string MakeKey(string providerId, string modelId)
        => $"{providerId.ToLowerInvariant()}/{modelId.ToLowerInvariant()}";

    private SemaphoreSlim GetOrCreateGate(string providerId, string modelId)
    {
        var key = MakeKey(providerId, modelId);
        return _gates.GetOrAdd(key, k =>
        {
            // 模型级优先，回退 Provider 级
            var strategy = _configService.GetModelStrategy(providerId, modelId)
                        ?? _configService.GetProviderStrategy(providerId);
            var maxConcurrent = strategy?.EffectiveMaxConcurrentRequests ?? DefaultMaxConcurrent;
            
            _logger.LogInformation(
                "[Runtime:RateLimiter] Created gate for {Key} with maxConcurrency={MaxConcurrent}",
                key, maxConcurrent);
            
            return new SemaphoreSlim(maxConcurrent, maxConcurrent);
        });
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
