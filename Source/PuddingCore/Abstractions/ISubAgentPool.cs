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
    /// <summary>
    /// 稳定成员 id（<c>agent_pool.pool_id</c>）。
    /// <para>
    /// <b>为什么快照要带它：</b><c>name</c> 目前靠全局唯一索引兼作身份，而团队化要把 name 降为
    /// 「所属范围内唯一」的展示名 —— 那时名字就不能再当寻址键。稳定 id 不由名字派生，
    /// destroy → create 也不会复用，是唯一能跨改名追同一成员的键。
    /// </para>
    /// <para>
    /// 纯内存模式（未接存储）下与 <see cref="Name"/> 一样只在本进程内有效。
    /// </para>
    /// </summary>
    public required string PoolId { get; init; }

    public required string Name { get; init; }
    public required string SubSessionId { get; init; }
    public required string TemplateId { get; init; }
    public string? Role { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastUsedAt { get; init; }
    public required PooledSubAgentStatus Status { get; init; }
    public required int TaskCount { get; init; }
    public bool? LastSuccess { get; init; }

    /// <summary>
    /// 归属父会话（创建者的 <c>ParentSessionId</c>）。<c>null</c> 表示这一行没有记录归属
    /// （早期条目）。
    /// <para>
    /// 归属对快照的读者不是装饰：普通 Agent 的池操作只能作用于自己的条目，
    /// 而它要判断「是不是自己的」就只能看这个字段。
    /// </para>
    /// </summary>
    public string? OwnerSessionId { get; init; }

    /// <summary>归属工作区；<c>null</c> 同样表示未记录。</summary>
    public string? OwnerWorkspaceId { get; init; }
}

/// <summary>
/// 一次池操作声明「我代表谁」。非空字段参与比较，空字段不限定该维度。
/// <para>
/// <see cref="Unscoped"/> 专供管理面：它跨会话操作，语义上不属于任何会话。
/// 普通 Agent 一律用 <c>new PoolOwnerScope(request.SessionId, request.WorkspaceId)</c>。
/// </para>
/// </summary>
public readonly record struct PoolOwnerScope(string? SessionId, string? WorkspaceId)
{
    /// <summary>不限定归属。只有管理面（admin REST）该用它。</summary>
    public static PoolOwnerScope Unscoped => default;

    /// <summary>两个维度都不限定；此时归属校验退化为「不校验」。</summary>
    public bool IsUnscoped => string.IsNullOrEmpty(SessionId) && string.IsNullOrEmpty(WorkspaceId);
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

    /// <summary>
    /// 按稳定成员 id（<c>agent_pool.pool_id</c>）取快照。不存在时返回 <c>null</c>。
    /// <para>
    /// <b>为什么需要第二条寻址路径：</b>名字是展示名。计划中的「两个会话各有一个
    /// <c>reviewer</c>」一旦成立，名字就不再全局唯一，任何「按名找到唯一一条」的假设
    /// 都会变成竞态。id 与库里那一行一一对应，<c>destroy → create</c> 也换不掉它。
    /// </para>
    /// <para>
    /// 默认实现返回 <c>null</c>（即「没有这条 id」），所以纯内存实现与测试替身不需要改：
    /// 这条路径服务于「已经拿到 id 的调用方」，不是派活前置。做成必实现会影响所有替身，
    /// 而它们本来就不维护 id 索引。
    /// </para>
    /// </summary>
    Task<PooledSubAgent?> GetByPoolIdAsync(string poolId, CancellationToken ct = default)
        => Task.FromResult<PooledSubAgent?>(null);

    Task<SubAgentExecuteResult> ExecuteAsync(
        string name,
        SubAgentSpawnRequest request,
        CancellationToken ct = default);

    Task<bool> SleepAsync(string name, CancellationToken ct = default);
    Task<bool> DestroyAsync(string name, CancellationToken ct = default);
    IReadOnlyList<PooledSubAgent> List();

    /// <summary>
    /// 只列出 <paramref name="scope"/> 归属的条目。
    /// <para>
    /// 普通 Agent 必须用这个而不是 <see cref="List"/>：池是「按会话隔离」的（归属写库、
    /// 派活时校验），列表若无过滤就会与派活结论相反——看得见却驱动不了。
    /// </para>
    /// <para>
    /// 归属未记录的旧条目不会出现在任何非 <see cref="PoolOwnerScope.IsUnscoped"/> 的结果里：
    /// 「不知道属于谁」不能当成「属于当前会话」，否则校验会从这里被绕开。
    /// </para>
    /// <para>未就绪同样抛 <see cref="PoolNotReadyException"/>，不返回空集合（理由同 <see cref="List"/>）。</para>
    /// </summary>
    IReadOnlyList<PooledSubAgent> ListOwnedBy(PoolOwnerScope scope);

    /// <summary>
    /// 带归属校验的休眠。归属不符时抛 <see cref="PoolOwnershipMismatchException"/>，
    /// 而不是返回 <c>false</c>——<c>false</c> 的契约是「确认没有这个条目」，
    /// 而「存在、但不属于你」处置完全不同（前者去创建，后者去用对自己的名字）。
    /// </summary>
    Task<bool> SleepAsync(string name, PoolOwnerScope scope, CancellationToken ct = default);

    /// <summary>带归属校验的销毁；归属不符时抛 <see cref="PoolOwnershipMismatchException"/>。</summary>
    Task<bool> DestroyAsync(string name, PoolOwnerScope scope, CancellationToken ct = default);
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

    /// <summary>
    /// 读取条目声明的模板 id，用于「派活未显式指定模板时继承」。
    /// <para>
    /// <b>模板不能像模型那样退回默认值。</b>模型退回默认只是换个路由，模板决定子代理的
    /// 提示词、可用工具与能力上限：建的时候是 codex、派下去变成 workspace-task-agent，
    /// 等于换了一个人在干活，而结果里看不出差异。
    /// </para>
    /// <para>
    /// 与 <see cref="GetDeclaredModelIdAsync"/> 同一处置：默认实现返回 <c>null</c>，
    /// 纯内存实现与测试替身无需改动；「未声明」与「读不到」在调用方合流为「用工具默认模板」。
    /// </para>
    /// </summary>
    Task<string?> GetDeclaredTemplateIdAsync(string name, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
