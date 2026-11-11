using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;
using Pudding.ToolPlugins.Windows;

namespace PuddingToolPlugins.IntegrationTests;

[TestClass]
public sealed class RealWorkerTests
{
    private static readonly PluginScope Scope = new("test-workspace", "test-agent", "test-session");

    /// <summary>
    /// These tests drive a real worker process. Without the acceptance environment they must skip like the
    /// sibling classes (FoundationPrototypeTests / PluginWorkspaceEventDemoTests) instead of crashing with
    /// ArgumentNullException from Path.Combine(null, ...). Measured 2026-11-12: the commit gate now really
    /// runs Microsoft.Testing.Platform projects, and these seven tests failed only because the variable was
    /// absent, not because the product regressed.
    /// </summary>
    [TestInitialize]
    public void RequirePluginAcceptanceEnvironment()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT")))
            Assert.Inconclusive("Set PUDDING_PLUGIN_WORKER and PUDDING_PLUGIN_TEST_ROOT (plugin acceptance environment).");
    }

    [TestMethod]
    public async Task RealDll_LoadInvokeCoreCallbackHookUnload_WithoutRestartingController()
    {
        using var env = new TestEnvironment(); var pid = Environment.ProcessId;
        var package = await env.ImportAsync();
        await using var manager = env.Manager;
        Assert.AreEqual(0, manager.List().Count);
        var loaded = await manager.LoadAsync("one", package, Scope, env.Authorization);
        Assert.AreEqual("Active", loaded.State, loaded.Failure?.Message); Assert.AreNotEqual(pid, loaded.ProcessId);
        var sibling = Scope with { SessionId = "second-session" };
        var echo = manager.Tools(sibling).Single(t => t.Definition.Id == "echo");
        var result = await manager.ExecuteAsync(echo.FunctionName, sibling, "one-call", PluginJson.Value(new { text = "中文输入" }), env.Callback);
        Assert.AreEqual("中文输入", result.Value.GetString());
        await File.WriteAllTextAsync(Path.Combine(env.Root, "proof.txt"), "来自 Core broker");
        var read = manager.Tools(Scope).Single(t => t.Definition.Id == "read_file");
        result = await manager.ExecuteAsync(read.FunctionName, Scope, "read", PluginJson.Value(new { text = "proof.txt" }), env.Callback);
        Assert.AreEqual("来自 Core broker", result.Value.GetString());
        await Assert.ThrowsExactlyAsync<PluginException>(() => manager.ExecuteAsync(read.FunctionName, Scope, "escape", PluginJson.Value(new { text = "../outside" }), env.Callback));
        var advice = await manager.AdviseAsync(new("", "shell", "{}", false, "original stderr", "exit 1", 1, Scope), default);
        Assert.AreEqual(1, advice.Count);
        Assert.AreEqual("Unloaded", (await manager.UnloadAsync(loaded.ActivationId, Scope)).State);
        AssertExited(loaded.ProcessId!.Value); Assert.AreEqual(pid, Environment.ProcessId);
        Assert.AreEqual(0, manager.Tools(Scope).Count);
        var reloaded = await manager.LoadAsync("two", package, Scope, env.Authorization);
        Assert.AreNotEqual(loaded.ActivationId, reloaded.ActivationId);
        await Assert.ThrowsExactlyAsync<PluginException>(() => manager.ExecuteAsync(echo.FunctionName, Scope, "stale", PluginJson.Value(new { text = "stale" }), env.Callback));
    }
    [TestMethod]
    public async Task InitializationFailure_DoesNotPublish_AndReapsProcess()
    {
        using var env = new TestEnvironment(); var package = await env.ImportAsync("Sample.PluginFixture.FailedInitializationPlugin");
        await using var manager = env.Manager;
        var failed = await manager.LoadAsync("bad", package, Scope, env.Authorization);
        Assert.AreEqual("Failed", failed.State); Assert.AreEqual("plugin.initialize_failed", failed.Failure!.Code);
        Assert.AreEqual(0, manager.Tools(Scope).Count); AssertExited(failed.ProcessId!.Value);
    }
    [TestMethod]
    [DataRow("hang", "plugin.execution_timeout")][DataRow("flood", "plugin.output_limit")][DataRow("crash", "plugin.worker_exited")]
    public async Task FaultyPlugin_IsStopped_AndOtherAgentRemainsUsable(string tool, string code)
    {
        using var env = new TestEnvironment(); var package = await env.ImportAsync();
        await using var manager = env.Manager;
        var bad = await manager.LoadAsync("bad", package, Scope, env.Authorization);
        var otherScope = Scope with { AgentId = "healthy-agent" };
        var healthy = await manager.LoadAsync("healthy", package, otherScope, env.Authorization);
        var callable = manager.Tools(Scope).Single(t => t.Definition.Id == tool);
        var failure = await Assert.ThrowsExactlyAsync<PluginException>(() => manager.ExecuteAsync(callable.FunctionName, Scope, "fault", PluginJson.Value(new { text = "test" }), env.Callback));
        Assert.AreEqual(code, failure.Failure.Code);
        AssertExited(bad.ProcessId!.Value); Assert.AreEqual(0, manager.Tools(Scope).Count);
        var echo = manager.Tools(otherScope).Single(t => t.Definition.Id == "echo");
        Assert.AreEqual("alive", (await manager.ExecuteAsync(echo.FunctionName, otherScope, "healthy", PluginJson.Value(new { text = "alive" }), env.Callback)).Value.GetString());
    }
    [TestMethod]
    public async Task Timeout_ReapsChildProcessTree()
    {
        using var env = new TestEnvironment(); var package = await env.ImportAsync();
        await using var manager = env.Manager;
        var loaded = await manager.LoadAsync("tree", package, Scope, env.Authorization);
        var callable = manager.Tools(Scope).Single(t => t.Definition.Id == "child_hang");
        var marker = Path.Combine(env.Root, "child-pid.txt");
        await Assert.ThrowsExactlyAsync<PluginException>(() => manager.ExecuteAsync(callable.FunctionName, Scope, "tree", PluginJson.Value(new { text = marker }), env.Callback));
        Assert.IsTrue(File.Exists(marker), "The fixture must have started a real child before timeout.");
        AssertExited(int.Parse(await File.ReadAllTextAsync(marker))); AssertExited(loaded.ProcessId!.Value);
    }

    [TestMethod]
    public async Task ZeroToolSubscriptionPlugin_ReceivesWorkspaceEvent_AndUnloadStopsDelivery()
    {
        using var env = new TestEnvironment(); var package = await env.ImportObserverAsync();
        var scope = new PluginScope("test-workspace", "", "", "host-subscriber");
        await using var manager = env.Manager;

        var loaded = await manager.LoadAsync("observer", package, scope,
            new PluginAuthorization(false, new HashSet<string>(), false, SubscriptionsApproved: true));
        Assert.AreEqual("Active", loaded.State, loaded.Failure?.Message);
        Assert.AreEqual(0, manager.Tools(scope).Count, "A subscription-only plugin exposes no tools.");

        var workspaceEvent = new PluginWorkspaceEvent("evt-1", PluginEvents.WorkspaceArtifactCreated,
            new PluginScope("test-workspace", "", "", "host"), PluginJson.Value(new { payloadRef = "ref://artifact" }),
            DateTimeOffset.UtcNow);
        var deliveries = await manager.PublishWorkspaceEventAsync(workspaceEvent,
            callback: (_, _, _) => Task.FromResult(PluginJson.Value(new { accepted = true, archiveId = "archive-1" })));
        Assert.AreEqual(1, deliveries.Count);
        Assert.IsTrue(deliveries[0].Success);
        Assert.AreEqual("sample.observer", deliveries[0].PluginId);

        Assert.AreEqual("Unloaded", (await manager.UnloadAsync(loaded.ActivationId, scope)).State);
        Assert.AreEqual(0, (await manager.PublishWorkspaceEventAsync(workspaceEvent)).Count,
            "An unloaded observer receives no events.");
    }    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.IsTrue(process.HasExited || process.WaitForExit(5000), $"Process {pid} remains alive."); }
        catch (ArgumentException) { }
    }
    private sealed class TestEnvironment : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        public PluginPackageStore Store { get; }
        public PluginManager Manager { get; }
        public PluginCoreServices Core { get; }
        private PluginPackage? _package;
        public PluginAuthorization Authorization { get; } = new(true, new HashSet<string> { "workspace.read" }, true);
        public TestEnvironment()
        {
            Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "packages")); Core = new(Path.Combine(Root, "state"));
            Manager = new(Store, new WindowsPluginWorkerFactory(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER")!));
        }
        public Task<JsonElement> Callback(string method, JsonElement args, CancellationToken ct) => Core.CallAsync(_package!, Scope, Root, method, args, ct);
        public async Task<PluginPackage> ImportObserverAsync()
        {
            var manifest = new PluginManifest
            {
                Id = "sample.observer", Name = "Workspace observer", Version = "1.0.0",
                Entry = new("bin/Sample.PluginFixture.dll", "Sample.PluginFixture.WorkspaceObserverPlugin"),
                Limits = new(30, 1),
                Subscriptions = [new("archive", PluginEvents.WorkspaceArtifactCreated)],
            };
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                    writer.Write(JsonSerializer.Serialize(manifest, PluginJson.Options));
                using var target = zip.CreateEntry(manifest.Entry.Assembly).Open();
                using var source = File.OpenRead(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_FIXTURE")!);
                source.CopyTo(target);
            }
            stream.Position = 0;
            return _package = await Store.ImportAsync(stream, default);
        }

        public async Task<PluginPackage> ImportAsync(string entryType = "Sample.PluginFixture.FixturePlugin")
        {
            var manifest = new PluginManifest
            {
                Id = "sample.fixture", Name = "Fixture", Version = "1.0.0", Entry = new("bin/Sample.PluginFixture.dll", entryType),
                RequestedCapabilities = ["workspace.read"], Limits = new(2, 1), Hooks = [new("advice", "tool.result.advice")],
                Tools = new[] { "echo", "read_file", "hang", "flood", "crash", "child_hang" }.Select(id => new PluginToolDefinition
                {
                    Id = id, Name = id, Description = "Test fixture " + id,
                    InputSchema = PluginJson.Value(new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" }, additionalProperties = false }),
                    OutputSchema = PluginJson.Value(new { type = "string" }),
                }).ToArray(),
            };
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open())) writer.Write(JsonSerializer.Serialize(manifest, PluginJson.Options));
                using var target = zip.CreateEntry(manifest.Entry.Assembly).Open();
                using var source = File.OpenRead(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_FIXTURE")!); source.CopyTo(target);
            }
            stream.Position = 0; return _package = await Store.ImportAsync(stream, default);
        }
        public void Dispose()
        {
            // Process exit is asserted above. Windows can release executable image sections shortly
            // after its exit handle signals; bounded cleanup must not hide the original assertion.
            for (var attempt = 0; ; attempt++)
                try { Directory.Delete(Root, true); return; }
                catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
                { Thread.Sleep(50); }
        }
    }
}
