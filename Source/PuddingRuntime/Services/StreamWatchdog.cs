using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services;

/// <summary>
/// 流式看门狗：滑动窗口检测 LLM 流式响应的"卡死"。
/// 与硬超时不同——只要模型持续产出 chunk（即使间隔很长），看门狗不会触发。
/// 仅在最后 chunk 后超过 idleThreshold 无新数据时判定卡死，取消流。
/// 
/// 使用方式：
///   using var watchdog = new StreamWatchdog(TimeSpan.FromSeconds(120), logger, provider);
///   watchdog.Start();
///   // 将 watchdog.Token 链接到流式请求的 CancellationToken
///   // 每次收到 chunk 时调用 watchdog.Feed()
/// </summary>
internal sealed class StreamWatchdog : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private readonly string _provider;
    private readonly long _idleThresholdTicks;
    private readonly int _pollIntervalMs;
    private long _lastFeedTimestamp;
    private Task? _pollTask;
    private bool _disposed;

    /// <summary>
    /// 看门狗的 CancellationToken——当 idleThreshold 内无 Feed() 时自动取消。
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <param name="idleThreshold">无 chunk 的最大容忍时间</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="provider">Provider 名称（用于日志）</param>
    /// <param name="pollIntervalMs">轮询间隔，默认 30 秒</param>
    public StreamWatchdog(TimeSpan idleThreshold, ILogger logger, string provider, int pollIntervalMs = 30_000)
    {
        if (idleThreshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleThreshold), "Idle threshold must be positive.");

        _idleThresholdTicks = idleThreshold.Ticks;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _provider = provider ?? "unknown";
        _pollIntervalMs = pollIntervalMs;
        _lastFeedTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>启动后台轮询。</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StreamWatchdog));
        _pollTask = PollAsync();
    }

    /// <summary>收到新 chunk 时调用，重置空闲计时器。</summary>
    public void Feed()
    {
        Volatile.Write(ref _lastFeedTimestamp, Stopwatch.GetTimestamp());
    }

    private async Task PollAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_pollIntervalMs, _cts.Token);

                var lastFeed = Volatile.Read(ref _lastFeedTimestamp);
                var elapsedTicks = Stopwatch.GetTimestamp() - lastFeed;

                if (elapsedTicks >= _idleThresholdTicks && !_cts.IsCancellationRequested)
                {
                    var elapsedSec = elapsedTicks / (double)Stopwatch.Frequency;
                    _logger.LogWarning(
                        "[StreamWatchdog] provider={Provider}: idle for {Elapsed:F1}s (threshold={Threshold:F0}s) — cancelling stream",
                        _provider, elapsedSec, _idleThresholdTicks / (double)Stopwatch.Frequency);

                    _cts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消（看门狗被 Dispose 或自己触发 Cancel）
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StreamWatchdog] provider={Provider}: unexpected error in poll loop", _provider);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}
