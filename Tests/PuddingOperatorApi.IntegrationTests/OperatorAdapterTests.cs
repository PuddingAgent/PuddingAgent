using System.Text.Json;
using Pudding.Api.Contracts;
using PuddingRuntime.Operators;
using PuddingRuntime.Operators.PublicApi;
using PuddingRuntime.Services.Improvement.SkillValue;

namespace PuddingOperatorApi.IntegrationTests;

[TestClass]
public sealed class OperatorAdapterTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [TestMethod]
    public async Task OriginalScorerAndAdapter_AgreeOnScoreAndProvenance()
    {
        var scorer = new SkillValueScorer(new OperatorEnvironment()); var adapter = new SkillValueAccessAdapter(scorer);
        var result = await adapter.ScoreAsync(JsonSerializer.SerializeToElement(new SkillValueInput("skill", 5, 0, 0), Json), default);
        var original = await scorer.ScoreAsync(new SkillValueOperatorContext { SkillId = "skill", InjectionCount = 5, ReadFailureCount = 0 });
        Assert.AreEqual(1d, result.Score); Assert.AreEqual(original.Score, result.Score);
        Assert.AreEqual(original.Envelope.InputDigest, result.Provenance.InputDigest);
        Assert.AreEqual(original.Envelope.OperatorId, result.Provenance.OperatorId);
        Assert.AreEqual(original.Envelope.JudgementId, result.Provenance.JudgementId);
        Assert.AreEqual(original.Envelope.OperatorVersion, result.Provenance.OperatorVersion);
        Assert.AreEqual(original.Envelope.InstructionVersion, result.Provenance.InstructionVersion);
        Assert.AreEqual(original.Envelope.Reason, result.Provenance.Reason);
        Assert.AreEqual(original.Envelope.Evidence[0].Reference, result.Provenance.Evidence[0].Reference);
        Assert.IsNull(result.Provenance.ModelId); Assert.IsNull(result.Provenance.Confidence);
        Assert.AreEqual("Unknown", result.Provenance.ConfidenceKind);
    }
    [TestMethod]
    public async Task NoDataIsNull_WhileObservedFailureIsARealZero()
    {
        var adapter = new SkillValueAccessAdapter(new(new OperatorEnvironment()));
        var cold = await adapter.ScoreAsync(JsonSerializer.SerializeToElement(new SkillValueInput("s", 0, 0, 0), Json), default);
        var zero = await adapter.ScoreAsync(JsonSerializer.SerializeToElement(new SkillValueInput("s", 0, 2, 0), Json), default);
        Assert.AreEqual(ScoreAvailability.InsufficientData, cold.Availability); Assert.IsNull(cold.Score);
        Assert.AreEqual(SkillValueScene.InsufficientDataReasonCode, cold.Provenance.ReasonCode);
        Assert.AreEqual(ScoreAvailability.Valid, zero.Availability); Assert.AreEqual(0d, zero.Score);
    }
    [TestMethod]
    public async Task InvalidInputDoesNotBecomeAClampedOrInventedScore()
    {
        var adapter = new SkillValueAccessAdapter(new(new OperatorEnvironment()));
        foreach (var input in new[] { new SkillValueInput("s", -1, 0, 0), new("s", int.MaxValue, 1, 0), new("s", 0, 0, 1),
            new("s|ambiguous", 1, 0, 0), new("s", 1, 0, 0, WindowStartUtc: DateTimeOffset.UtcNow) })
            Assert.AreEqual("request.invalid", (await Assert.ThrowsExactlyAsync<OperatorApiException>(() => adapter.ScoreAsync(JsonSerializer.SerializeToElement(input, Json), default))).Code);
        await Assert.ThrowsExactlyAsync<OperatorApiException>(() => adapter.ScoreAsync(JsonSerializer.SerializeToElement(new { skillId = "s" }), default));
        await Assert.ThrowsExactlyAsync<OperatorApiException>(() => adapter.ScoreAsync(JsonSerializer.SerializeToElement(new { skillId = "s", injectionCount = 1, readFailureCount = 0, injectedBytesTotal = 0, actor = "admin" }), default));
    }
}
