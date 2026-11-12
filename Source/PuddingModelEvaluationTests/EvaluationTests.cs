using Pudding.Api.Contracts;
using PuddingExecutionResources.Contracts;
using PuddingModelEvaluation;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
namespace PuddingModelEvaluationTests;

[TestClass]
public sealed class EvaluationTests
{
    private static ClassificationRequest Classification() => new(new("hello", [new("chat", "conversation"), new("task", "request for work")]), new("call"));
    private static AcceptanceRequest Review() => new(new("deliver", [new("c1", "build passes")], [new("e1", "build exit 0")]), new("call"));
    [TestMethod]
    public async Task ClassifiedAndAbstained_AreDifferent_AndProvenanceIsScoped()
    {
        var pool = new Pool { Text = "{\"label\":\"chat\",\"reason\":\"greeting\",\"confidence\":0.8}" }; var api = Api(pool);
        var value = await api.ClassifyAsync(Classification()); Assert.AreEqual(EvaluationAvailability.Valid, value.Availability); Assert.AreEqual("chat", value.Label);
        Assert.AreEqual("run", value.Provenance.ParentRunId); Assert.AreEqual("ModelSelfReported", value.Provenance.ConfidenceKind);
        pool.Text = "{\"label\":null,\"reason\":\"unclear\",\"confidence\":null}";
        Assert.AreEqual(EvaluationAvailability.Abstain, (await api.ClassifyAsync(Classification())).Availability); Assert.AreEqual(2, pool.Calls);
    }
    [TestMethod]
    public async Task MalformedUnknownLabelAndTruncation_DoNotBecomeValid()
    {
        var pool = new Pool(); var api = Api(pool);
        foreach (var text in new[] { "", "{}", "{\"label\":\"invented\",\"reason\":\"x\",\"confidence\":0.5}", "{\"label\":\"chat\",\"label\":\"task\",\"reason\":\"x\",\"confidence\":null}" })
        { pool.Text = text; Assert.AreEqual(EvaluationAvailability.Degraded, (await api.ClassifyAsync(Classification())).Availability); }
        pool.Finish = "length"; Assert.AreEqual("model.incomplete", (await api.ClassifyAsync(Classification())).ReasonCode);
    }
    [TestMethod]
    public async Task AcceptanceRequiresConsistentChecksAndKnownEvidence_NotJustModelYes()
    {
        var pool = new Pool { Text = Answer("accept", "supported", "e1") }; var api = Api(pool);
        Assert.AreEqual(AcceptanceDecision.Accept, (await api.ReviewAsync(Review())).Decision);
        foreach (var text in new[] { Answer("accept", "supported", "invented"), Answer("accept", "unknown", "e1"), Answer("accept", "not_supported", "e1") })
        { pool.Text = text; var result = await api.ReviewAsync(Review()); Assert.AreEqual(EvaluationAvailability.Degraded, result.Availability); Assert.AreEqual(AcceptanceDecision.Abstain, result.Decision); }
        pool.Text = Answer("abstain", "unknown", "e1"); Assert.AreEqual(EvaluationAvailability.Abstain, (await api.ReviewAsync(Review())).Availability);
        pool.Text = Answer("reject", "not_supported", "e1"); Assert.AreEqual(AcceptanceDecision.Reject, (await api.ReviewAsync(Review())).Decision);
    }
    [TestMethod]
    public async Task DenialInvalidInputAndCancellation_NeverCallPool_QuotaFailureNeverRetries()
    {
        var pool = new Pool();
        await Assert.ThrowsExactlyAsync<ModelEvaluationException>(() => Api(pool, false).ClassifyAsync(Classification()));
        await Assert.ThrowsExactlyAsync<ModelEvaluationException>(() => Api(pool).ClassifyAsync(Classification() with { Input = new("x", [new("same", "a"), new("same", "b")]) }));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Api(pool).ClassifyAsync(Classification(), stop.Token)); Assert.AreEqual(0, pool.Calls);
        pool.Failure = new("budget.exhausted", "no budget");
        Assert.AreEqual("budget.exhausted", (await Assert.ThrowsExactlyAsync<ModelEvaluationException>(() => Api(pool).ClassifyAsync(Classification()))).Code);
        Assert.AreEqual(1, pool.Calls);
    }
    private static string Answer(string decision, string outcome, string evidence) => $$"""{"decision":"{{decision}}","reason":"based on supplied record","confidence":null,"checks":[{"criterionId":"c1","outcome":"{{outcome}}","evidenceIds":["{{evidence}}"]}]}""";
    private static IModelEvaluationApi Api(Pool pool, bool granted = true) => new ModelEvaluationService(pool).Bind(new Parent(), new Model(), true, granted, true);
    private sealed class Pool : IModelResourcePool
    {
        public int Calls; public string Text = "{}"; public string Finish = "stop"; public ExecutionResourceException? Failure;
        public Task<ModelResourceReceipt> InvokeAsync(IExecutionResourceScope parent, ILeasedModel model, ModelResourceRequest request, CancellationToken ct = default)
        {
            Calls++; if (Failure is not null) throw Failure;
            return Task.FromResult(new ModelResourceReceipt("lease", request.CallId, parent.Identity, "model", "rev", new(100, 20), .001m, "configured-price-unit", Text, Finish));
        }
    }
    private sealed class Model : ILeasedModel
    {
        public ModelResourceDescriptor Descriptor => new("model", "rev", "model", 8192, 2048, 1, new(1, 1, 1));
        public Task<ModelCompletion> InvokeAsync(ModelResourceRequest request, Action dispatch, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Parent : IExecutionResourceScope
    {
        public ExecutionResourceIdentity Identity => new("w", "a", "s", "run"); public DateTimeOffset Deadline => DateTimeOffset.UtcNow.AddMinutes(1);
        public CancellationToken Cancellation => default;
        public void Dispose() { }
        public ExecutionResourceSnapshot Snapshot() => throw new NotSupportedException();
        public IExecutionResourceScope CreateChild(ExecutionResourceLimits limits, DateTimeOffset deadline) => throw new NotSupportedException();
        public IModelBudgetReservation Reserve(ModelResourceDescriptor model, int maxOutputTokens) => throw new NotSupportedException();
    }
}
