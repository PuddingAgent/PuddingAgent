using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-02A 工作观察与取消。Observe 绑定精确回执与事件序号；取消是持久业务命令，
// Requested 只代表已请求，不代表 provider 已停止。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObserveWorkRequest(AgentRef Agent, string OperationId, long AfterSequence, int MaxCount = 100);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkEvent(long Sequence, string Kind, DateTimeOffset OccurredAt, string? ReasonCode = null,
    string? PayloadRef = null, string? PayloadHash = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkObservation(string OperationId, string Status, ImmutableArray<WorkEvent> Events,
    long NextSequence, bool Complete, bool SnapshotRequired, long OldestAvailableSequence = 0);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CancelWorkRequest(string OperationId, AgentRef Agent, string TargetOperationId,
    long ExpectedRevision, string Reason = "user.cancelled");

public enum CancelWorkEffect { Requested, AlreadyTerminal, NotFound, Forbidden }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CancelWorkReceipt(string OperationId, string TargetOperationId, CancelWorkEffect Effect,
    long Revision, string? CurrentStatus);

public interface IAgentWorkControlApi
{
    Task<WorkObservation> ObserveAsync(ObserveWorkRequest request, CancellationToken ct = default);
    Task<CancelWorkReceipt> CancelAsync(CancelWorkRequest request, CancellationToken ct = default);
}

public sealed class AgentWorkControlException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
