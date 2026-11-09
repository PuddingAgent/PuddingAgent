using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.AgentSdk;
using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.AgentSdkTests;

[TestClass]
public sealed class PluginModelEvaluationTests
{
    [TestMethod]
    public async Task AcceptanceWithoutSupportingChecksCannotBecomeValid()
    {
        var ctx = new Context();
        Assert.AreEqual("transport.invalid_response", (await Assert.ThrowsExactlyAsync<ModelEvaluationException>(() => ctx.ModelEvaluations().ReviewAsync(
            new("task", [new("criterion", "build")], [new("evidence", "log")]), new("test")))).Code);
        Assert.AreEqual(1, ctx.Calls);
    }
    [TestMethod]
    public async Task ScopeFreeInputAndOptionsPreserved_AbstentionNotConvertedToSuccess()
    {
        var ctx = new Context(); var options = new ModelEvaluationOptions("test", 64, 2000);
        var result = await ctx.ModelEvaluations().ClassifyAsync(Input(), options);
        Assert.AreEqual(EvaluationAvailability.Abstain, result.Availability); Assert.IsNull(result.Label);
        Assert.AreEqual("operators.text.classify", ctx.Method); Assert.AreEqual(1, ctx.Calls);
        Assert.IsFalse(ctx.Last.TryGetProperty("scope", out _)); Assert.IsFalse(ctx.Last.TryGetProperty("parentRunId", out _));
        var request = ctx.Last.Deserialize<ClassificationRequest>(PluginJson.Options)!;
        Assert.AreEqual(options, request.Options); Assert.AreEqual(" 中文 ", request.Input.Text);
    }
    [TestMethod]
    public async Task ForeignScopeAndWrongCallCannotBecomeVerifiedReceipts_NoRetry()
    {
        foreach (var mode in new[] { "scope", "call", "label", "usage", "null" })
        {
            var ctx = new Context { Mode = mode };
            Assert.AreEqual("transport.invalid_response", (await Assert.ThrowsExactlyAsync<ModelEvaluationException>(() => ctx.ModelEvaluations().ClassifyAsync(Input(), new("test")))).Code);
            Assert.AreEqual(1, ctx.Calls);
        }
    }
    [TestMethod]
    public async Task TypedFailureAndPreCancellationArePreserved()
    {
        var ctx = new Context { Mode = "denied" };
        Assert.AreEqual("budget.exhausted", (await Assert.ThrowsExactlyAsync<ModelEvaluationException>(() => ctx.ModelEvaluations().ClassifyAsync(Input(), new("test")))).Code);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ctx.ModelEvaluations().ListAsync(stop.Token)); Assert.AreEqual(1, ctx.Calls);
    }
    private static ClassificationInput Input() => new(" 中文 ", [new("work", "work"), new("chat", "chat")]);
    private sealed class Context : IPluginContext, IPluginCoreApi, IPluginAgentApiTransport
    {
        public PluginScope Scope => new("w", "a", "s"); public IPluginCoreApi Core => this;
        public int Calls; public string? Mode, Method; public JsonElement Last;
        public ValueTask<JsonElement> InvokeAgentApiAsync(string method, JsonElement args, CancellationToken ct = default)
        {
            Calls++; Method = method; Last = args;
            if (Mode == "denied") throw new PluginException("budget.exhausted", "core_callback", "budget exhausted");
            if (Mode == "null") return ValueTask.FromResult(PluginJson.Value<object?>(null));
            var p = new ModelEvaluationProvenance(Mode == "call" ? "other" : "test", "lease", new(Mode == "scope" ? "other" : "w", "a", "s"), "run",
                "text.classification", 1, "model", "rev", "input", "response", new(10, Mode == "usage" ? -1 : 3, 0, .1m, "configured-price-unit"));
            if (method == "operators.delivery.review") return ValueTask.FromResult(PluginJson.Value(new AcceptanceReceipt(EvaluationAvailability.Valid,
                AcceptanceDecision.Accept, [], null, "yes", null, p with { SceneKey = "delivery.acceptance" })));
            return ValueTask.FromResult(PluginJson.Value(new ClassificationReceipt(EvaluationAvailability.Abstain, Mode == "label" ? "work" : null, null, "insufficient", null, p)));
        }
        public ValueTask<string> ReadWorkspaceTextAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string> ReadAssetTextAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadStateAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask WriteStateAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
