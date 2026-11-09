using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.AgentSdk;
using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.AgentSdkTests;

[TestClass]
public sealed class PluginOperatorsTests
{
    [TestMethod]
    public async Task ScopeFreeWire_AndTypedInputReachOneTransportCall()
    {
        var context = new Context(); var api = context.Operators();
        Assert.HasCount(1, (await api.ListAsync()).Items);
        var result = await api.SkillValue.ScoreAsync(new("中文", 1, 0, 2), new("test"));
        Assert.AreEqual("test", result.CallId); Assert.AreEqual(2, context.Calls);
        Assert.IsFalse(context.Last.TryGetProperty("scope", out _)); Assert.IsFalse(context.Last.TryGetProperty("actor", out _));
    }
    [TestMethod]
    public async Task ForeignScope_AndDenialsAreNotSilentlyAcceptedOrRetried()
    {
        var context = new Context { Foreign = true };
        Assert.AreEqual("transport.invalid_response", (await Assert.ThrowsExactlyAsync<OperatorApiException>(() => context.Operators().SkillValue.ScoreAsync(new("s", 1, 0, 0), new("test")))).Code);
        Assert.AreEqual(1, context.Calls);
        context.Failure = new PluginException("permission.denied", "core_callback", "Denied");
        Assert.AreEqual("permission.denied", (await Assert.ThrowsExactlyAsync<OperatorApiException>(() => context.Operators().ListAsync())).Code);
        Assert.AreEqual(2, context.Calls);
    }
    [TestMethod]
    public async Task MalformedCatalogAndPlaceholderZero_AreNotAccepted()
    {
        var context = new Context { Response = PluginJson.Value(new { items = new object?[] { null } }) };
        Assert.AreEqual("transport.invalid_response", (await Assert.ThrowsExactlyAsync<OperatorApiException>(() => context.Operators().ListAsync())).Code);
        context.Response = null; context.Placeholder = true;
        Assert.AreEqual("transport.invalid_response", (await Assert.ThrowsExactlyAsync<OperatorApiException>(() => context.Operators().SkillValue.ScoreAsync(new("s", 0, 0, 0), new("test")))).Code);
        Assert.AreEqual(2, context.Calls);
    }
    [TestMethod]
    public async Task PreCancelledCallDoesNotReachTransport()
    {
        var context = new Context(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => context.Operators().ListAsync(stop.Token)); Assert.AreEqual(0, context.Calls);
    }
    private sealed class Context : IPluginContext, IPluginCoreApi, IPluginAgentApiTransport
    {
        public PluginScope Scope => new("w", "a", "s"); public IPluginCoreApi Core => this;
        public int Calls; public bool Foreign, Placeholder; public Exception? Failure; public JsonElement Last; public JsonElement? Response;
        public ValueTask<JsonElement> InvokeAgentApiAsync(string method, JsonElement arguments, CancellationToken ct = default)
        {
            Calls++; Last = arguments; if (Failure is not null) throw Failure;
            if (Response is { } response) return ValueTask.FromResult(response);
            if (method == "operators.list") return ValueTask.FromResult(PluginJson.Value(new OperatorCatalog([new("skill.value", "score", "schema", 1, "v1", false, 4096)])));
            var request = arguments.Deserialize<OperatorScoreRequest>(PluginJson.Options)!;
            var provenance = new OperatorProvenance("j", request.SceneKey, "rule", null, null, 1, 1, "input", null, null, "Unknown", "ok", null, [], false, 0, DateTimeOffset.UtcNow);
            return ValueTask.FromResult(PluginJson.Value(new OperatorScoreReceipt(request.Options.CallId, "evaluation", new(Foreign ? "other" : "w", "a", "s"),
                "fingerprint", "caller-supplied", new(Placeholder ? ScoreAvailability.InsufficientData : ScoreAvailability.Valid, Placeholder ? 0 : .5, "0..1", provenance))));
        }
        public ValueTask<string> ReadWorkspaceTextAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string> ReadAssetTextAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadStateAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask WriteStateAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
