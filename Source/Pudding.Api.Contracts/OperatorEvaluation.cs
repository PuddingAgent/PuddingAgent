using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

public sealed record OperatorScopeRef(string WorkspaceId, string AgentId, string SessionId);
public sealed record OperatorDescriptor(string SceneKey, string Primitive, string InputSchema, int InputVersion,
    string ImplementationVersion, bool UsesModel, int MaxInputBytes);
public sealed record OperatorCatalog(ImmutableArray<OperatorDescriptor> Items);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperatorCallOptions(string CallId, int TimeoutMilliseconds = 5000);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperatorScoreRequest(string SceneKey, string InputSchema, int InputVersion, JsonElement Input, OperatorCallOptions Options);

/// <summary>Caller-supplied observations. This API does not read or certify host telemetry.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillValueInput(string SkillId, int InjectionCount, int ReadFailureCount, long InjectedBytesTotal,
    int DistinctKeywordCount = 0, int DistinctAgentCount = 0, DateTimeOffset? WindowStartUtc = null, DateTimeOffset? WindowEndUtc = null);
public static class SkillValueApi
{
    public const string SceneKey = "skill.value";
    public const string InputSchema = "pudding.skill-value-input";
    public const int InputVersion = 1;
}
[JsonConverter(typeof(JsonStringEnumConverter<ScoreAvailability>))]
public enum ScoreAvailability { Unknown, Valid, InsufficientData, Degraded }
public sealed record OperatorEvidence(string Kind, string Reference, string? Note);
public sealed record OperatorThreshold(string PolicyId, int Version, double YesAtOrAbove, double NoAtOrBelow);
/// <summary>Original operator provenance; missing versions/confidence are not invented.</summary>
public sealed record OperatorProvenance(string JudgementId, string SceneKey, string OperatorId, string? OperatorVersion,
    string? ModelId, int? InstructionVersion, int SchemaVersion, string InputDigest, OperatorThreshold? Threshold,
    double? Confidence, string ConfidenceKind, string Reason, string? ReasonCode, ImmutableArray<OperatorEvidence> Evidence,
    bool Cached, double? LatencyMs, DateTimeOffset CreatedAtUtc);
/// <summary>Unavailable scores are null, never the internal operator's placeholder zero.</summary>
public sealed record OperatorScoreValue(ScoreAvailability Availability, double? Score, string Scale, OperatorProvenance Provenance);
public sealed record OperatorScoreReceipt(string CallId, string EvaluationId, OperatorScopeRef Scope, string InputFingerprint,
    string InputOrigin, OperatorScoreValue Value);
public sealed class OperatorApiException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }

/// <summary>Bound by a trusted host. No caller-selected credentials, identities or model instances.</summary>
public interface IOperatorApi
{
    Task<OperatorCatalog> ListAsync(CancellationToken ct = default);
    Task<OperatorScoreReceipt> ScoreAsync(OperatorScoreRequest request, CancellationToken ct = default);
}
