using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelEvaluationOptions(string CallId, int MaxOutputTokens = 1024, int TimeoutMilliseconds = 15000);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClassificationCategory(string Id, string Description);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClassificationInput(string Text, ImmutableArray<ClassificationCategory> Categories);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClassificationRequest(ClassificationInput Input, ModelEvaluationOptions Options);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptanceCriterion(string Id, string Text);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptanceEvidence(string Id, string Text);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptanceInput(string Task, ImmutableArray<AcceptanceCriterion> Criteria, ImmutableArray<AcceptanceEvidence> Evidence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptanceRequest(AcceptanceInput Input, ModelEvaluationOptions Options);
[JsonConverter(typeof(JsonStringEnumConverter<EvaluationAvailability>))]
public enum EvaluationAvailability { Unknown, Valid, Abstain, Degraded }
[JsonConverter(typeof(JsonStringEnumConverter<AcceptanceDecision>))]
public enum AcceptanceDecision { Abstain, Accept, Reject }
[JsonConverter(typeof(JsonStringEnumConverter<CriterionOutcome>))]
public enum CriterionOutcome { Unknown, Supported, NotSupported }
public sealed record AcceptanceCheck(string CriterionId, CriterionOutcome Outcome, ImmutableArray<string> EvidenceIds);
public sealed record EvaluationModelUsage(long InputTokens, long OutputTokens, long CacheHitTokens, decimal Cost, string CostUnit);
public sealed record ModelEvaluationProvenance(string CallId, string LeaseId, OperatorScopeRef Scope, string ParentRunId,
    string SceneKey, int InstructionVersion, string ModelId, string ModelRevision, string InputFingerprint, string ResponseFingerprint,
    EvaluationModelUsage Usage, string InputOrigin = "caller-supplied", string ConfidenceKind = "ModelSelfReported");
public sealed record ClassificationReceipt(EvaluationAvailability Availability, string? Label, double? Confidence,
    string Reason, string? ReasonCode, ModelEvaluationProvenance Provenance);
public sealed record AcceptanceReceipt(EvaluationAvailability Availability, AcceptanceDecision Decision, ImmutableArray<AcceptanceCheck> Checks,
    double? Confidence, string Reason, string? ReasonCode, ModelEvaluationProvenance Provenance);
public interface IModelEvaluationApi
{
    Task<OperatorCatalog> ListAsync(CancellationToken ct = default);
    Task<ClassificationReceipt> ClassifyAsync(ClassificationRequest request, CancellationToken ct = default);
    Task<AcceptanceReceipt> ReviewAsync(AcceptanceRequest request, CancellationToken ct = default);
}
public sealed class ModelEvaluationException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }
