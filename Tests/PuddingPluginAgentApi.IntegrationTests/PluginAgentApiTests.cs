using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Windows;
using PuddingCode.Configuration;
using PuddingCode.Tools;
using PuddingCode.Models;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Plugins;
using PuddingRuntime.Services.Plugins;

[assembly: DoNotParallelize]
namespace PuddingPluginAgentApi.IntegrationTests;

[TestClass]
public sealed class PluginAgentApiTests
{
    /// <summary>Skips instead of failing when the acceptance environment is absent (see PluginHostLoopTests).</summary>
    [TestInitialize]
    public void RequirePluginWorker()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER")))
            Assert.Inconclusive("Set PUDDING_PLUGIN_WORKER to a built plugin worker executable (plugin acceptance environment).");
    }

    private static readonly PluginScope Scope = new("workspace", "agent", "session");
    [TestMethod]
    public async Task RealWorker_UsesSdkAndCanonicalManifestWriter_CasDedupReloadAndScopeArePreserved()
    {
        await using var f = await Fixture.Create(); var pid = Environment.ProcessId;
        var activation = await f.Load(); Assert.AreEqual("Active", activation.State, activation.Failure?.Message);
        Assert.AreNotEqual(pid, activation.ProcessId);
        var tool = f.Tool(); Assert.AreEqual(ToolPermissionLevel.High, tool.Descriptor.PermissionLevel);
        var read = await f.Call(new { action = "read" }); Assert.IsTrue(read.Success, read.Error);
        var snapshot = JsonSerializer.Deserialize<AgentConfigurationSnapshot>(read.Output, PluginJson.Options)!;
        var request = new { action = "update", revision = snapshot.Revision, operationId = "rename", name = "插件命名" };
        var update = await f.Call(request); Assert.IsTrue(update.Success, update.Error);
        var receipt = JsonSerializer.Deserialize<ConfigurationChangeReceipt>(update.Output, PluginJson.Options)!;
        Assert.AreEqual("插件命名", (await f.Manifest()).DisplayName);
        Assert.AreEqual("keep this prompt", (await f.Manifest()).SystemPrompt);
        Assert.AreEqual("private-provider", (await f.Manifest()).PreferredProviderId);
        var replay = await f.Call(request); Assert.IsTrue(replay.Success, replay.Error);
        Assert.AreEqual(receipt, JsonSerializer.Deserialize<ConfigurationChangeReceipt>(replay.Output, PluginJson.Options));
        var changes = await f.Call(new { action = "changes" }); Assert.IsTrue(changes.Success, changes.Error);
        var page = JsonSerializer.Deserialize<ConfigurationChangePage>(changes.Output, PluginJson.Options)!;
        Assert.HasCount(1, page.Items); Assert.AreEqual("rename", page.Items[0].OperationId);
        Assert.AreEqual(receipt.PersistedRevision, page.ObservedRevision);
        var conflict = await f.Call(new { action = "update", revision = snapshot.Revision, operationId = "another", name = "stale" });
        Assert.IsFalse(conflict.Success); Assert.AreEqual("revision.conflict", conflict.Status);
        var forged = await f.Call(new { action = "forge" }); Assert.IsFalse(forged.Success); Assert.AreEqual("request.invalid", forged.Status);
        Assert.AreEqual("Unloaded", (await f.Plugins.Manager.UnloadAsync(activation.ActivationId, Scope)).State);
        AssertExited(activation.ProcessId!.Value); Assert.AreEqual(pid, Environment.ProcessId);
        var second = await f.Load("reload"); Assert.AreNotEqual(activation.ActivationId, second.ActivationId);
        var afterReload = await f.Call(request); Assert.IsTrue(afterReload.Success, afterReload.Error);
        Assert.AreEqual(receipt, JsonSerializer.Deserialize<ConfigurationChangeReceipt>(afterReload.Output, PluginJson.Options));
        var stale = await tool.ExecuteAsync(new() { ToolCallId = "old", ArgumentsJson = "{\"action\":\"read\"}", Context = f.Context });
        Assert.IsFalse(stale.Success); Assert.AreEqual("plugin.unloaded", stale.Status);
    }
    [TestMethod]
    public async Task DeferredCallback_CannotBorrowTheNextInvocationIdentity()
    {
        await using var f = await Fixture.Create(); Assert.AreEqual("Active", (await f.Load()).State);
        var deferred = await f.Call(new { action = "defer" }); Assert.IsTrue(deferred.Success, deferred.Error);
        var resumed = await f.Call(new { action = "resume" }); Assert.IsTrue(resumed.Success, resumed.Error);
        Assert.AreEqual("plugin.invocation_expired", JsonDocument.Parse(resumed.Output).RootElement.GetProperty("code").GetString());
        Assert.IsTrue((await f.Call(new { action = "read" })).Success, "Rejecting the stale callback must preserve the live invocation transport.");
    }
    [TestMethod]
    public async Task ReadOnlyTool_CannotWriteEvenWhenPackageRequestedTheCapability()
    {
        await using var f = await Fixture.Create(writeEffect: false); var loaded = await f.Load(); Assert.AreEqual("Active", loaded.State);
        var snapshot = JsonSerializer.Deserialize<AgentConfigurationSnapshot>((await f.Call(new { action = "read" })).Output, PluginJson.Options)!;
        var write = await f.Call(new { action = "update", revision = snapshot.Revision, operationId = "denied", name = "bad" });
        Assert.IsFalse(write.Success); Assert.AreEqual("plugin.core_callback_denied", write.Status);
        Assert.AreEqual("Old", (await f.Manifest()).DisplayName);
    }
    [TestMethod]
    public async Task MissingRequestedGrant_IsDeniedBeforeWorkerStarts()
    {
        await using var f = await Fixture.Create();
        var failure = Assert.ThrowsExactly<PluginException>(() => f.Plugins.Manager.LoadAsync("denied", f.Package, Scope, new(true, new HashSet<string>(), false)));
        Assert.AreEqual("plugin.capability_denied", failure.Failure.Code); Assert.HasCount(0, f.Plugins.Manager.List());
    }
    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.IsTrue(process.HasExited || process.WaitForExit(5000)); }
        catch (ArgumentException) { }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT")
            ?? throw new InvalidOperationException("An isolated test root is required."), "pudding-agentapi-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        public PuddingDataPaths Paths { get; }
        public ExecutablePluginService Plugins { get; private set; } = null!;
        public Pudding.ToolPlugins.Contracts.PluginPackage Package { get; private set; } = null!;
        private string ManifestPath => Path.Combine(Paths.AgentInstanceRoot("agent"), "manifest.json");
        public ToolExecutionContext Context => new() { WorkspaceId = Scope.WorkspaceId, AgentInstanceId = Scope.AgentId, SessionId = Scope.SessionId, WorkingDirectory = _root };
        private Fixture() { Paths = PuddingDataPaths.FromRoot(_root); }
        public static async Task<Fixture> Create(bool writeEffect = true)
        {
            var f = new Fixture();
            await AtomicFileWriter.WriteJsonAsync(f.ManifestPath, new AgentInstanceManifest { AgentInstanceId = "agent", WorkspaceId = "workspace",
                TemplateId = "template", DisplayName = "Old", SystemPrompt = "keep this prompt", PreferredProviderId = "private-provider" }, PluginJson.Options);
            await AtomicFileWriter.WriteJsonAsync(f.Paths.WorkspaceAgentRefFile("workspace", "agent"),
                new WorkspaceAgentRef { AgentInstanceId = "agent", WorkspaceId = "workspace" }, PluginJson.Options);
            var avatars = new AgentAvatarCatalog(NullLogger<AgentAvatarCatalog>.Instance);
            var files = new WorkspaceAgentFileService(f.Paths, new AgentTemplateFileService(f.Paths, avatars, NullLogger<AgentTemplateFileService>.Instance),
                avatars, f._services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkspaceAgentFileService>.Instance);
            var worker = Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER") ?? throw new InvalidOperationException("Test worker path is required.");
            f.Plugins = new(f.Paths, new WindowsPluginWorkerFactory(worker), new PluginAgentApiLeaseFactory(files));
            var manifest = new PluginManifest { Id = "sample.agentconfig", Name = "Configuration", Version = "1.0.0", CoreApi = "1.1",
                Entry = new("fixture.dll", "Sample.PluginFixture.AgentConfigurationPlugin"),
                RequestedCapabilities = ["agent.configuration.read", "agent.configuration.update", "agent.configuration.changes"],
                Tools = [new() { Id = "configuration", Name = "configuration", Description = "Fixture", Effects = writeEffect ? ["agent.configuration.write"] : ["none"],
                    InputSchema = PluginJson.Value(new { type = "object", additionalProperties = true }), OutputSchema = PluginJson.Value(new { type = "object", additionalProperties = true }) }] };
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
            {
                using (var output = new StreamWriter(zip.CreateEntry("plugin.json").Open())) output.Write(JsonSerializer.Serialize(manifest, PluginJson.Options));
                using var dll = zip.CreateEntry("fixture.dll").Open(); using var input = File.OpenRead(typeof(Sample.PluginFixture.AgentConfigurationPlugin).Assembly.Location); input.CopyTo(dll);
            }
            archive.Position = 0; f.Package = await f.Plugins.Packages.ImportAsync(archive, default); return f;
        }
        public Task<Pudding.ToolPlugins.Runtime.PluginActivation> Load(string operation = "load") => Plugins.Manager.LoadAsync(operation, Package, Scope, new(true, Package.Manifest.RequestedCapabilities.ToHashSet(StringComparer.Ordinal), false));
        public IPuddingTool Tool() => Plugins.ListTools(Context).Single();
        public Task<ToolExecutionResult> Call(object arguments) => Tool().ExecuteAsync(new() { Context = Context, ToolCallId = Guid.NewGuid().ToString("N"), ArgumentsJson = JsonSerializer.Serialize(arguments, PluginJson.Options) });
        public async Task<AgentInstanceManifest> Manifest() => (await AtomicFileWriter.ReadJsonAsync<AgentInstanceManifest>(ManifestPath, PluginJson.Options))!;
        public async ValueTask DisposeAsync()
        {
            await Plugins.DisposeAsync(); await _services.DisposeAsync();
            for (var attempt = 0; ; attempt++)
                try { Directory.Delete(_root, true); return; }
                catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException) { await Task.Delay(50); }
        }
    }
}
