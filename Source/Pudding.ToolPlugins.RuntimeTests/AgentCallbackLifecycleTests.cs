using System.IO.Compression;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;

namespace Pudding.ToolPlugins.RuntimeTests;

[TestClass]
public sealed class AgentCallbackLifecycleTests
{
    private static readonly PluginScope Scope = new("workspace", "agent", "session");
    [TestMethod]
    public void NewCapabilities_RequireExplicitCoreVersion_AndDoNotExposeWorkSubmission()
    {
        var manifest = Manifest(); PluginValidation.Manifest(manifest);
        Assert.ThrowsExactly<PluginException>(() => PluginValidation.Manifest(manifest with { CoreApi = "1.0" }));
        Assert.ThrowsExactly<PluginException>(() => PluginValidation.Manifest(manifest with { RequestedCapabilities = ["agent.work.submit"] }));
    }
    [TestMethod]
    public async Task CallerCannotMutatePublishedCapabilitySet()
    {
        using var fixture = new Fixture(); var package = await fixture.Import(); await using var manager = fixture.Manager;
        var caps = new HashSet<string> { "agent.configuration.read" };
        var loaded = await manager.LoadAsync("load", package, Scope, new(true, caps, false)); caps.Clear();
        var result = await manager.ExecuteAsync(loaded.ToolNames[0], Scope, "read", PluginJson.Value(new { }),
            (_, _, _) => Task.FromResult(PluginJson.Value("read")));
        Assert.AreEqual("read", result.Value.GetString());
    }
    [TestMethod]
    public async Task ToolWithoutWriteEffect_CannotUseApprovedPackageWriteCapability()
    {
        using var fixture = new Fixture { Method = "agent.configuration.update" };
        var package = await fixture.Import(Manifest() with { RequestedCapabilities = ["agent.configuration.update"] });
        await using var manager = fixture.Manager;
        var loaded = await manager.LoadAsync("load", package, Scope, new(true, new HashSet<string> { "agent.configuration.update" }, false));
        var reached = false;
        var error = await Assert.ThrowsExactlyAsync<PluginException>(() => manager.ExecuteAsync(loaded.ToolNames[0], Scope, "write", PluginJson.Value(new { }),
            (_, _, _) => { reached = true; return Task.FromResult(PluginJson.Value("bad")); }));
        Assert.AreEqual("plugin.core_callback_denied", error.Failure.Code); Assert.IsFalse(reached);
    }
    [TestMethod]
    public async Task Unload_DoesNotReportUnloadedWhileAdmittedHostCallbackIsRunning()
    {
        using var fixture = new Fixture(); var package = await fixture.Import(); await using var manager = fixture.Manager;
        var loaded = await manager.LoadAsync("load", package, Scope, new(true, new HashSet<string> { "agent.configuration.read" }, false));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = manager.ExecuteAsync(loaded.ToolNames[0], Scope, "call", PluginJson.Value(new { }), async (_, _, _) =>
        { entered.TrySetResult(); await release.Task; return PluginJson.Value("finished"); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var unload = manager.UnloadAsync(loaded.ActivationId, Scope);
        Assert.IsFalse(unload.IsCompleted); Assert.HasCount(0, manager.Tools(Scope));
        release.TrySetResult(); await call;
        Assert.AreEqual("Unloaded", (await unload).State);
        await Assert.ThrowsExactlyAsync<PluginException>(() => manager.ExecuteAsync(loaded.ToolNames[0], Scope, "stale", PluginJson.Value(new { }), (_, _, _) => throw new Exception()));
    }
    [TestMethod]
    public async Task UnconfirmedCallbackDrain_RemainsStopFailed_AndConsumesCapacity()
    {
        using var fixture = new Fixture(); var package = await fixture.Import(); await using var manager = fixture.Manager;
        var auth = new PluginAuthorization(true, new HashSet<string> { "agent.configuration.read" }, false);
        var loaded = await manager.LoadAsync("load", package, Scope, auth);
        for (var i = 0; i < 3; i++) await manager.LoadAsync("load", package, Scope with { ActivationKey = "other" + i }, auth);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = manager.ExecuteAsync(loaded.ToolNames[0], Scope, "call", PluginJson.Value(new { }), async (_, _, _) =>
        { entered.TrySetResult(); await release.Task; return PluginJson.Value("finished"); });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stopped = await manager.UnloadAsync(loaded.ActivationId, Scope);
            Assert.AreEqual("StopFailed", stopped.State); Assert.AreEqual("unknown", stopped.Failure!.EffectOutcome);
            var blocked = Assert.ThrowsExactly<PluginException>(() => manager.LoadAsync("extra", package, Scope with { ActivationKey = "extra" }, auth));
            Assert.AreEqual("plugin.resource_limit", blocked.Failure.Code);
        }
        finally { release.TrySetResult(); await call; }
        Assert.AreEqual("Unloaded", (await manager.UnloadAsync(loaded.ActivationId, Scope)).State);
        Assert.AreEqual("Active", (await manager.LoadAsync("extra", package, Scope with { ActivationKey = "extra" }, auth)).State);
    }
    private static PluginManifest Manifest() => new()
    {
        Id = "sample.agentapi", Name = "Agent API", Version = "1.0.0", CoreApi = "1.1", Entry = new("plugin.dll", "Fixture"),
        RequestedCapabilities = ["agent.configuration.read"],
        Tools = [new() { Id = "test", Name = "test", Description = "test", InputSchema = PluginJson.Value(new { type = "object" }), OutputSchema = PluginJson.Value(new { type = "string" }) }]
    };
    private sealed class Fixture : IPluginWorkerFactory, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pudding-agent-callback-" + Guid.NewGuid().ToString("N"));
        private readonly PluginPackageStore _store;
        public string Method = "agent.configuration.read";
        public PluginManager Manager { get; }
        public Fixture() { _store = new(_root); Manager = new(_store, this); }
        public async Task<PluginPackage> Import(PluginManifest? manifest = null)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open())) writer.Write(JsonSerializer.Serialize(manifest ?? Manifest(), PluginJson.Options));
                using var writer2 = new StreamWriter(zip.CreateEntry("plugin.dll").Open()); writer2.Write("fake DLL: factory does not load code");
            }
            stream.Position = 0; return await _store.ImportAsync(stream, default);
        }
        public Task<IPluginWorker> StartAsync(PluginPackage package, PluginScope scope, string activationId, CancellationToken ct) => Task.FromResult<IPluginWorker>(new Worker(this));
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    private sealed class Worker(Fixture fixture) : IPluginWorker
    {
        public int ProcessId => 1; public bool HasExited { get; private set; }
        public async Task<JsonElement> RequestAsync(PluginWorkerRequest request, Func<string, JsonElement, CancellationToken, Task<JsonElement>> callback, CancellationToken ct)
            => request.Kind == "initialize" ? PluginJson.Value(new { toolIds = new[] { "test" }, hookIds = Array.Empty<string>(), subscriptionIds = Array.Empty<string>() })
                : PluginJson.Value(PluginResult.Ok(await callback(fixture.Method, PluginJson.Value(new { }), ct)));
        public Task<bool> StopAsync(CancellationToken ct) { HasExited = true; return Task.FromResult(true); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
