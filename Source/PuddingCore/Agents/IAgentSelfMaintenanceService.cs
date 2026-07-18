namespace PuddingCode.Agents;

/// <summary>
/// 当前 Agent 自维护边界。
/// 实现只能访问调用上下文指定 Agent 的白名单配置文档，不接受任意物理路径。
/// </summary>
public interface IAgentSelfMaintenanceService
{
    Task<AgentSelfStateSnapshot> InspectAsync(
        string agentInstanceId,
        CancellationToken ct = default);

    Task<AgentSelfStateDocument> ReadDocumentAsync(
        string agentInstanceId,
        string document,
        CancellationToken ct = default);

    Task<AgentSelfStateUpdateResult> UpdateDocumentAsync(
        string agentInstanceId,
        string document,
        string content,
        string? expectedSha256 = null,
        CancellationToken ct = default);
}

public static class AgentSelfStateDocuments
{
    public const string Soul = "soul";
    public const string Agents = "agents";
    public const string Tools = "tools";
    public const string Bootstrap = "bootstrap";
    public const string Memory = "memory";
    public const string Heartbeat = "heartbeat";

    public static readonly IReadOnlyList<string> All =
    [
        Soul,
        Agents,
        Tools,
        Bootstrap,
        Memory,
        Heartbeat,
    ];
}

public sealed record AgentSelfStateSnapshot
{
    public required string AgentInstanceId { get; init; }
    public required string TemplateId { get; init; }
    public string? DisplayName { get; init; }
    public bool IsEnabled { get; init; }
    public required IReadOnlyList<AgentSelfStateDocumentInfo> Documents { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }
    public bool IsHealthy => Issues.Count == 0;
}

public sealed record AgentSelfStateDocumentInfo
{
    public required string Document { get; init; }
    public required string FileName { get; init; }
    public bool ReferencedByManifest { get; init; }
    public bool Exists { get; init; }
    public int Length { get; init; }
    public string? Sha256 { get; init; }
    public DateTimeOffset? LastModifiedAt { get; init; }
}

public sealed record AgentSelfStateDocument
{
    public required string AgentInstanceId { get; init; }
    public required string Document { get; init; }
    public required string FileName { get; init; }
    public required string Content { get; init; }
    public required string Sha256 { get; init; }
    public required DateTimeOffset LastModifiedAt { get; init; }
}

public sealed record AgentSelfStateUpdateResult
{
    public required string AgentInstanceId { get; init; }
    public required string Document { get; init; }
    public required string FileName { get; init; }
    public required string PreviousSha256 { get; init; }
    public required string Sha256 { get; init; }
    public required int Length { get; init; }
    public bool ManifestReferenceRepaired { get; init; }
    public bool EffectiveOnNextTurn { get; init; } = true;
}

public sealed class AgentSelfStateConflictException : Exception
{
    public AgentSelfStateConflictException(string document, string expectedSha256, string actualSha256)
        : base(
            $"Agent state document '{document}' changed since it was read. " +
            $"Expected SHA-256 '{expectedSha256}', actual '{actualSha256}'. Read it again before updating.")
    {
        Document = document;
        ExpectedSha256 = expectedSha256;
        ActualSha256 = actualSha256;
    }

    public string Document { get; }
    public string ExpectedSha256 { get; }
    public string ActualSha256 { get; }
}
