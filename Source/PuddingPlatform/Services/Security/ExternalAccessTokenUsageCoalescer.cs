using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Services.Security;

/// <summary>
/// ADR-075 §5.5: last_used_at 合并写。认证正确性路径只读数据库；成功使用投递到本合并器，
/// 同一 Token 至少间隔 MinPersistInterval（默认 5 分钟）才持久化一次最新使用时间。
/// 进程崩溃最多丢失该窗口内的展示精度，不影响认证与审计正确性。
/// 内存以真实 Token 数为上界（RecordSuccess 仅在认证成功后调用）。
/// </summary>
public sealed class ExternalAccessTokenUsageCoalescer(
    ExternalAccessTokenStore store,
    ILogger<ExternalAccessTokenUsageCoalescer>? logger = null,
    TimeProvider? timeProvider = null) : IHostedService, IDisposable
{
    public static readonly TimeSpan MinPersistInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FlushPeriod = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, UsageState> _pending = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    private sealed record UsageState(DateTimeOffset Latest, DateTimeOffset LastPersisted);

    public void RecordSuccess(string tokenId, DateTimeOffset usedAtUtc)
    {
        _pending.AddOrUpdate(
            tokenId,
            _ => new UsageState(usedAtUtc, DateTimeOffset.MinValue),
            (_, existing) => existing.Latest >= usedAtUtc ? existing : existing with { Latest = usedAtUtc });
    }

    /// <summary>Pending 数量（诊断/测试用）。</summary>
    public int PendingCount => _pending.Count;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => FlushLoopAsync(_loopCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _loopCts?.Cancel();
        try
        {
            if (_loopTask is not null)
                await _loopTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        await FlushAsync(force: true, cancellationToken);
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(FlushPeriod, _time);
        try
        {
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
            {
                await FlushAsync(force: false, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>非 force：只持久化距上次落库超过 MinPersistInterval 的 Token；force：全部落库（停机路径）。</summary>
    public async Task FlushAsync(bool force, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var snapshot = _pending.ToArray();

        foreach (var (tokenId, state) in snapshot)
        {
            if (state.Latest <= state.LastPersisted)
                continue;
            if (!force && now - state.LastPersisted < MinPersistInterval)
                continue;

            try
            {
                await store.TouchLastUsedAsync(tokenId, state.Latest, ct);
                _pending[tokenId] = state with { LastPersisted = state.Latest };
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[ExternalAccessTokenUsage] TouchLastUsed {TokenId} 失败，下轮重试", tokenId);
            }
        }
    }

    public void Dispose()
    {
        _loopCts?.Dispose();
        _flushLock.Dispose();
    }
}
