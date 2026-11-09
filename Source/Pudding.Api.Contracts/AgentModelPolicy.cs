using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-06A 有效模型配置 DTO：每字段带来源/来源 revision/有效 revision；Clear 表示取消实例覆盖，
// 不是写默认模型。写入不提高运行中根账户额度。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EffectiveModelEntry(string Purpose, string? ModelId, string? ModelRevision, string? Source,
    long SourceRevision, string? MissingReason, bool InheritPrimary = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EffectiveAgentModelPolicy(string WorkspaceId, string AgentId, long EffectiveRevision,
    long AppliedRevision, long PolicyGeneration, DateTimeOffset ResolvedAt,
    ImmutableArray<EffectiveModelEntry> Purposes, ImmutableArray<string> PendingReasons);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelPolicyPatchDto(string Purpose, string? ModelId, bool Clear, bool InheritPrimary = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateAgentModelPolicy(string OperationId, AgentRef Agent, long ExpectedRevision,
    ImmutableArray<ModelPolicyPatchDto> Patches, string ApplyPolicy = "next-run");

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentModelPolicyReceipt(string OperationId, long PersistedRevision, long AppliedRevision,
    bool AppliesFromNextRun, ImmutableArray<string> PendingReasons, long PolicyGeneration);

public interface IAgentModelPolicyApi
{
    Task<EffectiveAgentModelPolicy?> GetEffectiveAsync(AgentRef agent, CancellationToken ct = default);
    Task<AgentModelPolicyReceipt> UpdateAsync(UpdateAgentModelPolicy request, CancellationToken ct = default);
}

public sealed class AgentModelPolicyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
