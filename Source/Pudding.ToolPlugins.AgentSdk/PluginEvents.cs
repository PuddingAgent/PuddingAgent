using System.Text.Json.Serialization;

namespace Pudding.ToolPlugins.AgentSdk;

// PA-07A 冻结的插件事件观察契约（示例 DS-PA07-A 只消费这里的类型与回调形状）。
// Owner/generation/subscription grant 由宿主绑定；插件只看到已授权、已过滤的交付。
// checkpoint 与幂等由宿主端口保证：同一 EventId 的审计写入固定使用 OperationId=EventId。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewCompletedEventPayload(string ReviewId, string ReviewJobId, string Finding,
    string EvidenceLevel, string? ReasonCode, long ReviewRevision);

/// <summary>A committed canonical event delivered to a bound plugin subscription.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginEventEnvelope(string EventId, string StreamId, long Sequence, string Kind,
    int SchemaVersion, DateTimeOffset OccurredAt, string WorkspaceId, string PayloadRef, string PayloadHash,
    string Producer, ReviewCompletedEventPayload? ReviewCompleted = null, string? CausationId = null,
    string? CorrelationId = null, string? BusinessRef = null)
{
    public bool IsValid => Valid(EventId) && Valid(StreamId) && Sequence > 0 && Valid(Kind) && SchemaVersion >= 1
        && OccurredAt != default && Valid(WorkspaceId) && Valid(PayloadRef) && Valid(PayloadHash) && Valid(Producer)
        && (ReviewCompleted is null || (Valid(ReviewCompleted.ReviewId) && Valid(ReviewCompleted.ReviewJobId)
            && Valid(ReviewCompleted.Finding) && Valid(ReviewCompleted.EvidenceLevel)));

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

/// <summary>Scoped host context: the plugin cannot select another workspace, generation or catalog.</summary>
public sealed record PluginEventContext(string PluginId, long Generation, string WorkspaceId,
    IPluginAuditApi Audit);

public interface IPluginEventContext
{
    string PluginId { get; }
    long Generation { get; }
    string WorkspaceId { get; }
    IPluginAuditApi Audit { get; }
}

/// <summary>Scoped audit sink. The host dedupes by OperationId, so a replayed event produces one logical record.</summary>
public interface IPluginAuditApi
{
    Task<PluginAuditReceipt> RecordAsync(PluginAuditRecord record, CancellationToken ct = default);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginAuditRecord(string OperationId, string EventId, string ReviewId, string Finding,
    string EvidenceLevel, string? ReasonCode)
{
    public bool IsValid => Valid(OperationId) && Valid(EventId) && Valid(ReviewId) && Valid(Finding)
        && Valid(EvidenceLevel) && (ReasonCode is null || ReasonCode.Length <= 128);

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginAuditReceipt(string AuditId, string OperationId, bool Replayed);

/// <summary>The frozen observer callback shape: a plugin formats one authorized event and records one audit entry.</summary>
public interface IPluginEventObserver
{
    Task HandleAsync(PluginEventEnvelope envelope, IPluginEventContext context, CancellationToken ct = default);
}

public sealed class PluginEventsException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
