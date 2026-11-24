using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>Lifecycle state of a reusable child-agent session.</summary>
public enum PooledSubAgentStatus
{
    Idle,
    Busy,
    Sleeping,
    Dead,
}

/// <summary>Read-only snapshot of a reusable child-agent session.</summary>
public sealed record PooledSubAgent
{
    public required string Name { get; init; }
    public required string SubSessionId { get; init; }
    public required string TemplateId { get; init; }
    public string? Role { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastUsedAt { get; init; }
    public required PooledSubAgentStatus Status { get; init; }
    public required int TaskCount { get; init; }
    public bool? LastSuccess { get; init; }
}

/// <summary>
/// Runtime-facing boundary for reusable child-agent sessions.
/// The concrete pool remains in Platform; Runtime tools depend only on this Core contract.
/// </summary>
public interface ISubAgentPool
{
    int Count { get; }
    int MaxCapacity { get; }

    Task<PooledSubAgent> CreateAsync(
        string name,
        SubAgentSpawnRequest request,
        CancellationToken ct = default);

    Task<PooledSubAgent?> GetAsync(string name, CancellationToken ct = default);

    Task<SubAgentExecuteResult> ExecuteAsync(
        string name,
        SubAgentSpawnRequest request,
        CancellationToken ct = default);

    Task<bool> SleepAsync(string name, CancellationToken ct = default);
    Task<bool> DestroyAsync(string name, CancellationToken ct = default);
    IReadOnlyList<PooledSubAgent> List();
    Task<string?> EvictLeastRecentlyUsedAsync(CancellationToken ct = default);

    /// <summary>
    /// 池的就绪状态。NotStarted/Loading/Failed 都不可用；Failed 表示恢复失败。
    /// <para>
    /// 持久化之后「池里有什么」需要先从一个库读回来，于是多了一个「还没读回来」的状态。
    /// 调用方必须先看它（或直接用会等就绪的 <see cref="GetSnapshotAsync"/>），
    /// 不能把未就绪当成空池。
    /// </para>
    /// </summary>
    PoolReadiness Readiness { get; }

    /// <summary>异步取快照（会等待/触发加载）。未就绪且恢复失败时抛 <see cref="PoolNotReadyException"/>。</summary>
    Task<IReadOnlyList<PooledSubAgent>> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// 读取条目声明的模型路由（<c>provider/model</c>），用于「派活未显式指定 model 时继承」。
    /// <para>
    /// 默认实现返回 <c>null</c>（即「没有声明」），所以纯内存实现与测试替身不需要改：
    /// 「未声明」与「读不到」在调用方是同一个处置（走平台默认路由）。
    /// </para>
    /// <para>
    /// 不抛业务异常：条目不存在、库不可用都返回 <c>null</c>。
    /// 这条路径是增强而不是派活前置——做成可失败会让一次库抖动直接打断执行。
    /// </para>
    /// </summary>
    Task<string?> GetDeclaredModelIdAsync(string name, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
