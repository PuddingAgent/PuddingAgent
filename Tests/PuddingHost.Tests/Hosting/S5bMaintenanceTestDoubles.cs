using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PuddingAgent.Services;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// S5b（2026-09-27）宿主维护接线的测试替身：
/// ① 记录型 logger（断言「如实记录原因」与「默认关闭不记 Error」）；
/// ② 计数型维护器（断言 StartAsync / StopAsync 的**调用次数**与入参 scope）；
/// ③ 组合替身与记录型组合工厂（断言「默认关闭连组合都不构造」与「配置值原样流入」）。
/// <para>全部零副作用、零磁盘，仅作用于 <c>%TEMP%</c>（由 S5Fixture 保证）。</para>
/// </summary>
internal sealed class MaintenanceRecordingLogger : ILogger<FullTextIndexMaintenanceHostedService>
{
    private readonly object _gate = new();
    private readonly List<string> _entries = [];

    internal IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToArray();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
            _entries.Add($"{logLevel}: {formatter(state, exception)}");
    }
}

/// <summary>计数型维护器：只记调用次数与入参，不做任何事（零磁盘、零线程）。</summary>
internal sealed class CountingMaintenance : IFullTextIndexMaintenance
{
    internal int StartCalls;

    internal int StopCalls;

    internal ConcurrentQueue<IReadOnlyList<FullTextMaintenanceScope>> StartScopes { get; } = new();

    public Task StartAsync(
        IReadOnlyList<FullTextMaintenanceScope> scopes,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref StartCalls);
        StartScopes.Enqueue(scopes);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref StopCalls);
        return Task.CompletedTask;
    }

    public ValueTask RequestRecoveryScanAsync(
        string scopeRoot,
        FullTextRecoveryReason reason,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public FullTextMaintenanceSnapshot GetSnapshot() =>
        new(false, [], null, null, 0, 0, null);
}

/// <summary>
/// 记录型维护器装饰器：把调用**转发**给内层（生产/真实）维护器，同时计数并记录入参 ——
/// 「宿主启动恰好一次 / 宿主停止恰好一次」这条不变量靠它成立。
/// </summary>
internal sealed class RecordingMaintenance : IFullTextIndexMaintenance
{
    private readonly IFullTextIndexMaintenance _inner;

    internal RecordingMaintenance(IFullTextIndexMaintenance inner) => _inner = inner;

    internal int StartCalls;

    internal int StopCalls;

    internal ConcurrentQueue<IReadOnlyList<FullTextMaintenanceScope>> StartScopes { get; } = new();

    public async Task StartAsync(
        IReadOnlyList<FullTextMaintenanceScope> scopes,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref StartCalls);
        StartScopes.Enqueue(scopes);
        await _inner.StartAsync(scopes, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref StopCalls);
        return _inner.StopAsync(cancellationToken);
    }

    public ValueTask RequestRecoveryScanAsync(
        string scopeRoot,
        FullTextRecoveryReason reason,
        CancellationToken cancellationToken = default) =>
        _inner.RequestRecoveryScanAsync(scopeRoot, reason, cancellationToken);

    public FullTextMaintenanceSnapshot GetSnapshot() => _inner.GetSnapshot();
}

/// <summary>记录型维护组合：把聚合根换成记录型维护器，其余逐字转发（同一实例引用保持）。</summary>
internal sealed class RecordingMaintenanceComposition : IFullTextIndexMaintenanceComposition
{
    private readonly IFullTextIndexMaintenanceComposition _inner;

    internal RecordingMaintenanceComposition(IFullTextIndexMaintenanceComposition inner)
    {
        _inner = inner;
        Maintenance = new RecordingMaintenance(inner.Maintenance);
    }

    internal RecordingMaintenance Maintenance { get; }

    IFullTextIndexMaintenance IFullTextIndexMaintenanceComposition.Maintenance => Maintenance;

    public IReadOnlyList<FullTextMaintenanceScope> Scopes => _inner.Scopes;

    public MaintenanceOptions ComponentOptions => _inner.ComponentOptions;

    public FullTextIndexOptions IndexOptions => _inner.IndexOptions;

    public IFullTextIndexRootedEngine LiveEngine => _inner.LiveEngine;
}

/// <summary>记录型组合工厂：包住**生产**工厂，记录每次 Create 的入参并交出记录型组合。</summary>
internal sealed class RecordingMaintenanceCompositionFactory : IFullTextIndexMaintenanceCompositionFactory
{
    private readonly IFullTextIndexMaintenanceCompositionFactory _inner;

    internal RecordingMaintenanceCompositionFactory(IFullTextIndexMaintenanceCompositionFactory inner) =>
        _inner = inner;

    internal int CreateCalls;

    internal ConcurrentQueue<MaintenanceOptions> CreatedWithOptions { get; } = new();

    internal ConcurrentQueue<IReadOnlyList<string>> CreatedWithScopes { get; } = new();

    internal ConcurrentQueue<RecordingMaintenanceComposition> Created { get; } = new();

    public IFullTextIndexMaintenanceComposition Create(
        MaintenanceOptions maintenanceOptions,
        IReadOnlyList<string> acceptedScopes)
    {
        Interlocked.Increment(ref CreateCalls);
        CreatedWithOptions.Enqueue(maintenanceOptions);
        CreatedWithScopes.Enqueue(acceptedScopes);

        var composition = new RecordingMaintenanceComposition(
            _inner.Create(maintenanceOptions, acceptedScopes));

        Created.Enqueue(composition);
        return composition;
    }
}

/// <summary>脚本化组合替身：零磁盘、零线程、零组件构造（断言「默认关闭连组合都不构造」）。</summary>
internal sealed class StubMaintenanceComposition : IFullTextIndexMaintenanceComposition
{
    internal StubMaintenanceComposition(
        CountingMaintenance maintenance,
        IReadOnlyList<FullTextMaintenanceScope> scopes,
        MaintenanceOptions componentOptions,
        FullTextIndexOptions indexOptions,
        IFullTextIndexRootedEngine liveEngine)
    {
        Maintenance = maintenance;
        Scopes = scopes;
        ComponentOptions = componentOptions;
        IndexOptions = indexOptions;
        LiveEngine = liveEngine;
    }

    internal CountingMaintenance Maintenance { get; }

    IFullTextIndexMaintenance IFullTextIndexMaintenanceComposition.Maintenance => Maintenance;

    public IReadOnlyList<FullTextMaintenanceScope> Scopes { get; }

    public MaintenanceOptions ComponentOptions { get; }

    public FullTextIndexOptions IndexOptions { get; }

    public IFullTextIndexRootedEngine LiveEngine { get; }
}

/// <summary>脚本化组合工厂：计数 + 交回固定替身（用于默认关闭 / fail-closed 路径）。</summary>
internal sealed class StubMaintenanceCompositionFactory : IFullTextIndexMaintenanceCompositionFactory
{
    private readonly StubMaintenanceComposition _composition;

    internal StubMaintenanceCompositionFactory(StubMaintenanceComposition composition) =>
        _composition = composition;

    internal int CreateCalls;

    internal ConcurrentQueue<MaintenanceOptions> CreatedWithOptions { get; } = new();

    public IFullTextIndexMaintenanceComposition Create(
        MaintenanceOptions maintenanceOptions,
        IReadOnlyList<string> acceptedScopes)
    {
        Interlocked.Increment(ref CreateCalls);
        CreatedWithOptions.Enqueue(maintenanceOptions);
        return _composition;
    }
}

/// <summary>
/// 确定性压力探针替身：**恒定返回 null = 不可采样**（组件契约：量不到就按「系统繁忙」退避）。
/// 刻意不采真实 CPU —— 宿主接线测试不得依赖机器负载，也不得触发 PDH/内核计数器。
/// </summary>
internal sealed class UnavailablePressureProbe : IResourcePressureProbe
{
    internal int SampleCalls;

    public ResourcePressureSample? Sample()
    {
        Interlocked.Increment(ref SampleCalls);
        return null;
    }
}
