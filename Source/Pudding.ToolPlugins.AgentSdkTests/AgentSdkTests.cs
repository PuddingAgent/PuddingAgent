using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.AgentSdk;
using Pudding.ToolPlugins.Contracts;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
namespace Pudding.ToolPlugins.AgentSdkTests;

[TestClass]
public sealed class AgentSdkTests
{
    private static readonly AgentRef Agent = new("workspace", "agent");
    [TestMethod]
    public async Task ConfigurationFacade_UsesScopedTransport_AndPublicContracts()
    {
        var context = new Context(); var config = context.AgentConfiguration();
        var snapshot = await config.GetAsync(); Assert.AreEqual(Agent, snapshot.Agent);
        var receipt = await config.UpdateAsync(new() { DisplayName = "中文名称" }, new(snapshot.Revision, "op"));
        Assert.AreEqual("op", receipt.OperationId);
        Assert.AreEqual("agent.configuration.update", context.LastMethod);
        var payload = context.LastArgs.Deserialize<ConfigurationUpdateRequest>(PluginJson.Options)!;
        Assert.AreEqual("中文名称", payload.Patch.DisplayName.Value); Assert.AreEqual("initial", payload.Options.ExpectedRevision);
        Assert.IsFalse(context.LastArgs.TryGetProperty("agent", out _));
        Assert.IsFalse(context.LastArgs.TryGetProperty("actor", out _));
    }
    [TestMethod]
    public async Task LostWriteReply_ReportsUnknown_WithoutRetry()
    {
        var context = new Context { Error = new IOException() };
        var ex = await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => context.AgentConfiguration().UpdateAsync(new() { DisplayName = "new" }, new("initial", "op")));
        Assert.AreEqual("unknown", ex.EffectOutcome); Assert.AreEqual(1, context.Calls);
    }
    [TestMethod]
    public async Task ForeignReceipt_ReportsUnknown()
    {
        var context = new Context { ForeignReceipt = true };
        Assert.AreEqual("unknown", (await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => context.AgentConfiguration().UpdateAsync(new() { DisplayName = "new" }, new("initial", "op")))).EffectOutcome);
    }
    [TestMethod]
    public async Task ServerDenial_RemainsDenial_AndDoesNotExposeUserCredentials()
    {
        var context = new Context { Error = new PluginException("permission.denied", "core_callback", "Denied") };
        var error = await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => context.AgentConfiguration().GetAsync());
        Assert.AreEqual("permission.denied", error.Code); Assert.AreEqual(1, context.Calls);
    }
    private sealed class Context : IPluginContext, IPluginCoreApi, IPluginAgentApiTransport
    {
        public PluginScope Scope => new("workspace", "agent", "session"); public IPluginCoreApi Core => this;
        public int Calls; public string? LastMethod; public JsonElement LastArgs; public Exception? Error; public bool ForeignReceipt;
        public ValueTask<JsonElement> InvokeAgentApiAsync(string method, JsonElement arguments, CancellationToken ct = default)
        {
            Calls++; LastMethod = method; LastArgs = arguments;
            if (Error is not null) throw Error;
            if (method == "agent.configuration.read") return ValueTask.FromResult(PluginJson.Value(new AgentConfigurationSnapshot(Agent, "initial", "Old", null, "template", DateTimeOffset.UtcNow)));
            var request = arguments.Deserialize<ConfigurationUpdateRequest>(PluginJson.Options)!;
            return ValueTask.FromResult(PluginJson.Value(new ConfigurationChangeReceipt(request.Options.OperationId,
                ForeignReceipt ? new("other", "agent") : Agent, "initial", "next", "next", "configuration-source", true, 1, DateTimeOffset.UtcNow)));
        }
        public ValueTask<string> ReadWorkspaceTextAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string> ReadAssetTextAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<string?> ReadStateAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask WriteStateAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
