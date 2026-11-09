using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pudding.Api.Contracts;
using PuddingOperatorAccess.Contracts;
using PuddingRuntime.Services.Improvement.SkillValue;

namespace PuddingRuntime.Operators.PublicApi;

/// <summary>Typed validation and result projection only. The existing scorer owns the formula and evidence.</summary>
public sealed class SkillValueAccessAdapter(SkillValueScorer scorer) : IOperatorBackend
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    public OperatorDescriptor Descriptor { get; } = new(SkillValueScene.SceneKey, "score", SkillValueApi.InputSchema, SkillValueApi.InputVersion,
        SkillValueScene.ValueScale, false, 8192);
    public async Task<OperatorScoreValue> ScoreAsync(JsonElement input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SkillValueInput value;
        try { value = input.Deserialize<SkillValueInput>(Json) ?? throw new JsonException(); }
        catch (JsonException) { throw Invalid(); }
        if (string.IsNullOrWhiteSpace(value.SkillId) || value.SkillId.Length > 256 || value.SkillId.Any(c => char.IsControl(c) || c == '|')
            || value.InjectionCount < 0 || value.ReadFailureCount < 0 || (long)value.InjectionCount + value.ReadFailureCount > int.MaxValue
            || value.InjectedBytesTotal < 0 || (value.InjectionCount == 0 && value.InjectedBytesTotal != 0)
            || value.DistinctKeywordCount < 0 || value.DistinctAgentCount < 0
            || value.WindowStartUtc.HasValue != value.WindowEndUtc.HasValue || value.WindowStartUtc > value.WindowEndUtc) throw Invalid();
        var context = new SkillValueOperatorContext { SkillId = value.SkillId, InjectionCount = value.InjectionCount,
            ReadFailureCount = value.ReadFailureCount, InjectedBytesTotal = value.InjectedBytesTotal,
            DistinctKeywordCount = value.DistinctKeywordCount, DistinctAgentCount = value.DistinctAgentCount,
            WindowStartUtc = value.WindowStartUtc, WindowEndUtc = value.WindowEndUtc };
        var result = await scorer.ScoreAsync(context, ct);
        var e = result.Envelope;
        var availability = result.Scale == SkillValueScene.InsufficientDataScale ? ScoreAvailability.InsufficientData
            : result.Scale == SkillValueScene.ValueScale ? ScoreAvailability.Valid : ScoreAvailability.Degraded;
        var threshold = e.Threshold is { } t ? new OperatorThreshold(t.PolicyId, t.Version, t.YesAtOrAbove, t.NoAtOrBelow) : null;
        return new(availability, availability == ScoreAvailability.Valid ? result.Score : null, result.Scale,
            new(e.JudgementId, e.SceneKey, e.OperatorId, e.OperatorVersion, e.ModelId, e.InstructionVersion, e.SchemaVersion,
                e.InputDigest, threshold, e.Confidence, e.ConfidenceKind.ToString(), e.Reason, e.ReasonCode,
                e.Evidence.Select(x => new OperatorEvidence(x.Kind, x.Reference, x.Note)).ToImmutableArray(), e.Cached, e.LatencyMs, e.CreatedAtUtc));
    }
    private static OperatorApiException Invalid() => new("request.invalid", "Expected bounded, nonnegative skill observations and a valid optional time window.");
}
