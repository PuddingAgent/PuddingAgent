using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-03 公开 Review Job DTO：Submit 只做有界校验与持久受理（返回票据与状态），不等待模型完成；
// 逻辑唯一键由 Workspace/父 Work 或 Goal/ActivationEpoch/IterationNo/目标版本/验收版本/产物 hash/策略版本组成。
// Review 结论是建议，不是 Goal 终态。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitReviewRequest(
    string OperationId,
    string WorkspaceId,
    string? ParentWorkId,
    string? GoalRunId,
    long ActivationEpoch,
    int IterationNo,
    string ObjectiveVersion,
    string AcceptanceContractVersion,
    string ArtifactSetHash,
    string ReviewPolicyVersion,
    bool Required = true,
    int TimeoutSeconds = 300);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewJobRef(string JobId, string ReviewId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewCriterionResult(string CriterionId, string Outcome, string EvidenceLevel,
    ImmutableArray<string> EvidenceIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewJobView(string JobId, string ReviewId, string State, string Finding,
    string EvidenceLevel, ImmutableArray<ReviewCriterionResult> Criteria, string? ReasonCode, long Revision,
    long AcceptedSequence, DateTimeOffset UpdatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CancelReviewRequest(string OperationId, ReviewJobRef Job, long ExpectedRevision);

public interface IReviewJobsApi
{
    Task<ReviewJobView> SubmitAsync(SubmitReviewRequest request, CancellationToken ct = default);
    Task<ReviewJobView?> GetAsync(ReviewJobRef job, CancellationToken ct = default);
    Task<ReviewJobView> CancelAsync(CancelReviewRequest request, CancellationToken ct = default);
}

public sealed class ReviewJobException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
