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
}
