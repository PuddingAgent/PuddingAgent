using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;

namespace Pudding.ToolPlugins.RuntimeTests;

[TestClass]
public sealed class PluginRuntimeTests
{
    private static readonly PluginScope Scope = new("workspace", "agent", "session");
    private static readonly PluginAuthorization Authorized = new(true, new HashSet<string>(), true);
    private static PluginManifest Manifest => new()
    {
        Id = "sample.test", Name = "Test", Version = "1.0.0", Entry = new("bin/Sample.dll", "Sample.Plugin"),
        Tools = [new() { Id = "echo", Name = "Echo", Description = "Returns text", Category = "Query",
            InputSchema = PluginJson.Value(new { type = "object", properties = new { text = new { type = "string", maxLength = 32 } }, required = new[] { "text" }, additionalProperties = false }),
            OutputSchema = PluginJson.Value(new { type = "string" }) }],
        Hooks = [new("advice", "tool.result.advice")],
    };
    [TestMethod]
    public async Task Import_IsImmutable_ScansMetadata_AndRejectsTampering()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path);
        var one = await Import(store); var again = await Import(store);
        Assert.AreEqual(one.ContentHash, again.ContentHash);
        var two = await Import(store, Manifest with { Description = "Changed" });
        Assert.AreNotEqual(one.ContentHash, two.ContentHash);
        Assert.AreEqual(2, store.Scan().Packages.Count); Assert.IsTrue(store.Scan().Complete);
        await File.WriteAllTextAsync(System.IO.Path.Combine(one.Directory, "bin/Sample.dll"), "tampered");
        var error = Assert.ThrowsExactly<PluginException>(() => store.Verify(one));
        Assert.AreEqual("plugin.content_changed", error.Failure.Code);
    }
    [TestMethod]
    [DataRow("../escape")][DataRow("/absolute")][DataRow("a\\b")][DataRow("bin/CON.txt")][DataRow("a:stream")][DataRow("dir./file")]
    public async Task Import_RejectsUnsafeArchivePaths(string path)
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path);
        using var zip = Zip(Manifest, path);
        await Assert.ThrowsExactlyAsync<PluginException>(() => store.ImportAsync(zip, CancellationToken.None));
        Assert.AreEqual(0, store.Scan().Packages.Count);
    }
    [TestMethod]
    public void Manifest_RejectsUnknownPermissions_Hooks_SchemaAndDuplicateTools()
    {
        Assert.ThrowsExactly<PluginException>(() => PluginValidation.Manifest(Manifest with { RequestedCapabilities = ["core.admin"] }));
        Assert.ThrowsExactly<PluginException>(() => PluginValidation.Manifest(Manifest with { Hooks = [new("unsafe", "permission.override")] }));
        Assert.ThrowsExactly<PluginException>(() => PluginValidation.Manifest(Manifest with { Tools = [Manifest.Tools[0], Manifest.Tools[0]] }));
        Assert.ThrowsExactly<PluginException>(() => PluginValidation.Schema(PluginJson.Value(new { type = "object", unknown = true }), 0));
        Assert.ThrowsExactly<PluginException>(() => PluginValidation.Value(Manifest.Tools[0].InputSchema, PluginJson.Value(new { text = "ok", extra = true })));
    }
    [TestMethod]
    [DataRow("maxLength")][DataRow("minLength")][DataRow("minItems")][DataRow("maxItems")][DataRow("minimum")][DataRow("maximum")]
    public async Task Import_ReportsMalformedSchemaBound_WithoutPublishingPackage(string keyword)
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path);
        var schema = PluginJson.Value(new Dictionary<string, object> { ["type"] = "string", [keyword] = "not-a-number" });
        var manifest = Manifest with { Tools = [Manifest.Tools[0] with { OutputSchema = schema }] };
        using var zip = Zip(manifest);
        var error = await Assert.ThrowsExactlyAsync<PluginException>(() => store.ImportAsync(zip, CancellationToken.None));
        Assert.AreEqual("plugin.schema_invalid", error.Failure.Code);
        Assert.AreEqual(0, store.Scan().Packages.Count);
    }
    [TestMethod]
    public async Task DeniedLoad_DoesNotStartWorker_AndDuplicateOperationCannotChangeContent()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path); var p = await Import(store);
        var factory = new FakeFactory(); await using var manager = new PluginManager(store, factory);
        Assert.ThrowsExactly<PluginException>(() => manager.LoadAsync("op", p, Scope, Authorized with { ExecuteApproved = false }));
        Assert.AreEqual(0, factory.Starts);
        var loaded = await manager.LoadAsync("op", p, Scope, Authorized);
        Assert.AreEqual("Active", loaded.State);
        Assert.AreEqual(loaded.ActivationId, (await manager.LoadAsync("op", p, Scope, Authorized)).ActivationId);
        Assert.AreEqual(1, factory.Starts);
        var other = await Import(store, Manifest with { Description = "new" });
        Assert.ThrowsExactly<PluginException>(() => manager.LoadAsync("op", other, Scope, Authorized));
    }
    [TestMethod]
    public async Task Unload_RevokesScopeAndGeneration_BeforeReload()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path); var p = await Import(store);
        await using var manager = new PluginManager(store, new FakeFactory());
        var first = await manager.LoadAsync("first", p, Scope, Authorized);
        Assert.AreEqual(0, manager.Tools(Scope with { AgentId = "other" }).Count);
        var sibling = Scope with { SessionId = "other" };
        Assert.AreEqual(first.ActivationId, manager.Tools(sibling).Single().ActivationId);
        Assert.AreEqual(first.ActivationId, (await manager.LoadAsync("repeat", p, sibling, Authorized)).ActivationId);
        await Assert.ThrowsExactlyAsync<PluginException>(() => manager.UnloadAsync(first.ActivationId, Scope with { WorkspaceId = "other" }));
        Assert.AreEqual("hello", (await manager.ExecuteAsync(first.ToolNames[0], sibling, "call", PluginJson.Value(new { text = "hello" }), Callback)).Value.GetString());
        Assert.AreEqual("Unloaded", (await manager.UnloadAsync(first.ActivationId, sibling)).State);
        var second = await manager.LoadAsync("second", p, Scope, Authorized);
        Assert.AreNotEqual(first.ToolNames[0], second.ToolNames[0]);
        await Assert.ThrowsExactlyAsync<PluginException>(() => manager.ExecuteAsync(first.ToolNames[0], Scope, "old", PluginJson.Value(new { text = "hello" }), Callback));
    }
    [TestMethod]
    public async Task LoadUnloadRace_DoesNotPublishLateInitialize()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path); var p = await Import(store);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeFactory { BlockInitialize = blocker.Task };
        await using var manager = new PluginManager(store, factory);
        var loading = manager.LoadAsync("op", p, Scope, Authorized);
        var stopping = manager.UnloadAsync(manager.List().Single().ActivationId, Scope);
        blocker.SetResult();
        await loading; Assert.AreEqual("Unloaded", (await stopping).State);
        Assert.AreEqual(0, manager.Tools(Scope).Count); Assert.IsTrue(factory.Last!.HasExited);
    }
    [TestMethod]
    public async Task Advice_IsAttributed_AndFailureDoesNotEraseOriginalEvidence()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path); var p = await Import(store);
        var factory = new FakeFactory(); await using var manager = new PluginManager(store, factory);
        await manager.LoadAsync("op", p, Scope, Authorized);
        var call = new PluginHookCall("", "shell", "{}", false, "stderr evidence", "exit 1", 1, Scope);
        var advice = await manager.AdviseAsync(call, CancellationToken.None);
        Assert.AreEqual("sample.test", advice.Single().PluginId);
        factory.Last!.FailHook = true;
        Assert.AreEqual(0, (await manager.AdviseAsync(call, CancellationToken.None)).Count);
        Assert.AreEqual("stderr evidence", call.Output); Assert.IsFalse(call.Success);
        Assert.IsTrue(manager.Diagnostics(Scope).Count > 0);
    }
    [TestMethod]
    public async Task FailedInitialization_PublishesNothing_AndStopsWorker()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path); var p = await Import(store);
        var factory = new FakeFactory { WrongIds = true }; await using var manager = new PluginManager(store, factory);
        var result = await manager.LoadAsync("op", p, Scope, Authorized);
        Assert.AreEqual("Failed", result.State); Assert.AreEqual("plugin.descriptor_mismatch", result.Failure!.Code);
        Assert.AreEqual(0, manager.Tools(Scope).Count); Assert.IsTrue(factory.Last!.HasExited);
    }
    [TestMethod]
    public async Task CoreReadAndState_AreBoundedAndScoped()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(System.IO.Path.Combine(temp.Path, "packages")); var p = await Import(store);
        var core = new PluginCoreServices(System.IO.Path.Combine(temp.Path, "state"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "evidence.txt"), "真实输出");
        Assert.AreEqual("真实输出", (await core.CallAsync(p, Scope, temp.Path, "workspace.read", PluginJson.Value(new { path = "evidence.txt" }), default)).GetString());
        await Assert.ThrowsExactlyAsync<PluginException>(() => core.CallAsync(p, Scope, temp.Path, "workspace.read", PluginJson.Value(new { path = "../escape" }), default));
        await core.CallAsync(p, Scope, null, "state.write", PluginJson.Value(new { key = "count", value = "1" }), default);
        Assert.AreEqual("1", (await core.CallAsync(p, Scope, null, "state.read", PluginJson.Value(new { key = "count" }), default)).GetString());
        Assert.AreEqual(JsonValueKind.Null, (await core.CallAsync(p, Scope with { AgentId = "other" }, null, "state.read", PluginJson.Value(new { key = "count" }), default)).ValueKind);
    }
    [TestMethod]
    public void DependencyClosure_DoesNotLoadHostOrOsAdapter()
    {
        var references = typeof(PluginManager).Assembly.GetReferencedAssemblies().Select(a => a.Name).Where(n => n!.StartsWith("Pudding")).ToArray();
        CollectionAssert.AreEqual(new[] { "Pudding.ToolPlugins.Contracts" }, references);
    }
    [TestMethod]
    public async Task IdleWorkerExit_AdvancesRevision_AndRevokesPublishedTools()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path); var p = await Import(store);
        var factory = new FakeFactory(); await using var manager = new PluginManager(store, factory);
        var activation = await manager.LoadAsync("idle", p, Scope, Authorized);
        var before = manager.Revision;
        await factory.Last!.StopAsync(default);
        Assert.IsTrue(manager.Revision > before);
        Assert.AreEqual(0, manager.Tools(Scope).Count);
        Assert.AreEqual("Failed", manager.List().Single().State);
        Assert.AreEqual("plugin.worker_exited", manager.Diagnostics().Single().Failure.Code);
    }
    private static Task<JsonElement> Callback(string method, JsonElement args, CancellationToken ct) => Task.FromResult(PluginJson.Value("callback"));
    [TestMethod]
    public void Manifest_AllowsZeroToolSubscription_AndRejectsUnknownEvent()
    {
        var subscriptionOnly = Manifest with
        {
            Tools = [], Hooks = [], Subscriptions = [new("archive", PluginEvents.WorkspaceArtifactCreated)],
        };
        PluginValidation.Manifest(subscriptionOnly);
        var error = Assert.ThrowsExactly<PluginException>(() => PluginValidation.Manifest(
            subscriptionOnly with { Subscriptions = [new("archive", "workspace.unbounded")] }));
        Assert.AreEqual("plugin.manifest_invalid", error.Failure.Code);
    }

    [TestMethod]
    public async Task HostScopedSubscription_LoadsZeroToolPlugin_AndDeliversWorkspaceEvents()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path);
        var manifest = Manifest with
        {
            Tools = [], Hooks = [], Subscriptions = [new("archive", PluginEvents.WorkspaceArtifactCreated)],
        };
        var package = await Import(store, manifest);
        var factory = new FakeFactory(); await using var manager = new PluginManager(store, factory);
        var scope = new PluginScope("workspace", "", "", "host-subscriber");

        var activation = await manager.LoadAsync("op", package, scope,
            new PluginAuthorization(true, new HashSet<string>(), false, SubscriptionsApproved: true));
        Assert.AreEqual("Active", activation.State);
        Assert.AreEqual(0, activation.ToolNames.Count, "A subscription-only plugin exposes no tools.");

        var evt = new PluginWorkspaceEvent("evt-1", PluginEvents.WorkspaceArtifactCreated,
            new PluginScope("workspace", "", "", "host"), PluginJson.Value(new { workId = "w1", version = 1 }),
            DateTimeOffset.UtcNow);
        var first = await manager.PublishWorkspaceEventAsync(evt);
        Assert.AreEqual(1, first.Count);
        Assert.IsTrue(first[0].Success);
        Assert.AreEqual(1, factory.Last!.Events.Count);
        Assert.AreEqual("evt-1", factory.Last.Events[0].GetProperty("eventId").GetString());

        var again = await manager.PublishWorkspaceEventAsync(evt);
        Assert.AreEqual(1, again.Count);
        Assert.AreEqual("evt-1", factory.Last.Events[1].GetProperty("eventId").GetString(),
            "Redelivery keeps the same host idempotency key.");

        await manager.UnloadAsync(activation.ActivationId, scope);
        Assert.AreEqual(0, (await manager.PublishWorkspaceEventAsync(evt)).Count,
            "An unloaded subscriber receives no new events.");
    }

    [TestMethod]
    public async Task HostActivation_RequiresWorkspace_AndSkipsAgentSession()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path);
        var package = await Import(store);
        var factory = new FakeFactory(); await using var manager = new PluginManager(store, factory);
        var denied = await Assert.ThrowsExactlyAsync<PluginException>(async () => await manager.LoadAsync(
            "op", package, new PluginScope("", "", "", "host-subscriber"), Authorized));
        Assert.AreEqual("plugin.scope_denied", denied.Failure.Code);
        Assert.AreEqual(0, factory.Starts);
    }

    [TestMethod]
    public async Task ConcurrentLoads_SameContentShareOneActivation()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path);
        var package = await Import(store);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeFactory { BlockInitialize = release.Task };
        await using var manager = new PluginManager(store, factory);

        var first = manager.LoadAsync("op-1", package, Scope, Authorized);
        while (factory.Starts == 0)
            await Task.Delay(5);
        var second = manager.LoadAsync("op-2", package, Scope, Authorized);
        release.TrySetResult();
        var activations = await Task.WhenAll(first, second);

        Assert.AreEqual(1, factory.Starts, "Concurrent loads of the same content/scope share one worker.");
        Assert.AreEqual(activations[0].ActivationId, activations[1].ActivationId);
        Assert.AreEqual("Active", activations[0].State);
    }

    [TestMethod]
    public async Task UnloadedActivation_CannotReceiveTargetedEvent()
    {
        using var temp = new Temp(); var store = new PluginPackageStore(temp.Path);
        var manifest = Manifest with
        {
            Tools = [], Hooks = [], Subscriptions = [new("archive", PluginEvents.WorkspaceArtifactCreated)],
        };
        var package = await Import(store, manifest);
        var factory = new FakeFactory(); await using var manager = new PluginManager(store, factory);
        var scope = new PluginScope("workspace", "", "", "host-subscriber");
        var activation = await manager.LoadAsync("observer", package, scope,
            new PluginAuthorization(false, new HashSet<string>(), false, SubscriptionsApproved: true));
        var evt = new PluginWorkspaceEvent("evt-1", PluginEvents.WorkspaceArtifactCreated,
            new PluginScope("workspace", "", "", "host"), PluginJson.Value(new { payloadRef = "ref://x" }),
            DateTimeOffset.UtcNow);
        Assert.AreEqual(1, (await manager.PublishWorkspaceEventAsync(evt, "sample.test", activation.Revision)).Count);

        await manager.UnloadAsync(activation.ActivationId, scope);

        Assert.AreEqual(0, (await manager.PublishWorkspaceEventAsync(evt, "sample.test", activation.Revision)).Count,
            "A stale activation handle must not receive or produce side effects after unload.");
        Assert.AreEqual(0, (await manager.PublishWorkspaceEventAsync(evt)).Count);
    }

    private static async Task<PluginPackage> Import(PluginPackageStore store, PluginManifest? manifest = null)
    { using var stream = Zip(manifest ?? Manifest); return await store.ImportAsync(stream, CancellationToken.None); }
    private static MemoryStream Zip(PluginManifest manifest, string? extra = null)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open())) writer.Write(JsonSerializer.Serialize(manifest, PluginJson.Options));
            using (var writer = new StreamWriter(zip.CreateEntry("bin/Sample.dll").Open())) writer.Write("fixture");
            if (extra is not null) using (var writer = new StreamWriter(zip.CreateEntry(extra).Open())) writer.Write("invalid");
        }
        stream.Position = 0; return stream;
    }
    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT") ?? System.IO.Path.GetTempPath(), "plugins-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private sealed class FakeFactory : IPluginWorkerFactory
    {
        public int Starts; public FakeWorker? Last; public bool WrongIds; public Task? BlockInitialize;
        public Task<IPluginWorker> StartAsync(PluginPackage package, PluginScope scope, string activationId, CancellationToken ct)
        { Starts++; Last = new FakeWorker { WrongIds = WrongIds, BlockInitialize = BlockInitialize, Subscriptions = package.Manifest.Subscriptions.Select(s => s.Id).ToArray(), ToolIds = package.Manifest.Tools.Select(t => t.Id).ToArray(), HookIds = package.Manifest.Hooks.Select(h => h.Id).ToArray() }; return Task.FromResult<IPluginWorker>(Last); }
    }
    private sealed class FakeWorker : IPluginWorker
    {
        public bool WrongIds, FailHook; public Task? BlockInitialize; public string[] Subscriptions = []; public string[] ToolIds = ["echo"]; public string[] HookIds = ["advice"]; public List<JsonElement> Events { get; } = [];
        public int ProcessId => 123;
        public bool HasExited { get; private set; }
        public async Task<JsonElement> RequestAsync(PluginWorkerRequest request, Func<string, JsonElement, CancellationToken, Task<JsonElement>> callback, CancellationToken ct)
        {
            if (request.Kind == "initialize") { if (BlockInitialize is not null) await BlockInitialize.WaitAsync(ct); return PluginJson.Value(new { toolIds = WrongIds ? new[] { "wrong" } : ToolIds, hookIds = HookIds, subscriptionIds = Subscriptions }); }
            if (request.Kind == "hook") { if (FailHook) throw new PluginException("plugin.execution_failed", "hook", "injected failure"); return PluginJson.Value(new PluginHookResult("Check parameters.")); }
            if (request.Kind == "event") { Events.Add(request.Payload.Clone()); return PluginJson.Value(new { accepted = true }); }
            return PluginJson.Value(PluginResult.Ok(request.Payload.GetProperty("arguments").GetProperty("text").GetString()));
        }
        public Task<bool> StopAsync(CancellationToken ct) { HasExited = true; return Task.FromResult(true); }
        public ValueTask DisposeAsync() { HasExited = true; return ValueTask.CompletedTask; }
    }
}
