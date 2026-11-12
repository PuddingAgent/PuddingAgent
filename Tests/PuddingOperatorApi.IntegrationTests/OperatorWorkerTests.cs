using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Windows;
using PuddingCode.Configuration;
using PuddingCode.Tools;
using PuddingOperatorAccess;
using PuddingRuntime.Operators;
using PuddingRuntime.Operators.PublicApi;
using PuddingRuntime.Services.Plugins;

[assembly: DoNotParallelize]
namespace PuddingOperatorApi.IntegrationTests;

[TestClass]
public sealed class OperatorWorkerTests
{

    [TestMethod]
    public async Task RealWorker_DiscoversScoresRejectsForgedScope_AndUnloads()
    {
        await using var f = await Fixture.Create(); var pid = Environment.ProcessId;
        var loaded = await f.Load(); Assert.AreEqual("Active", loaded.State, loaded.Failure?.Message); Assert.AreNotEqual(pid, loaded.ProcessId);
        var list = await f.Call(new { action = "list" }); Assert.IsTrue(list.Success, list.Error);
        var catalog = JsonSerializer.Deserialize<OperatorCatalog>(list.Output, PluginJson.Options)!;
        Assert.HasCount(1, catalog.Items); Assert.AreEqual("skill.value", catalog.Items[0].SceneKey); Assert.IsFalse(catalog.Items[0].UsesModel);
        var score = await f.Call(new { action = "score", input = new SkillValueInput("技能", 5, 0, 0) }); Assert.IsTrue(score.Success, score.Error);
        var receipt = JsonSerializer.Deserialize<OperatorScoreReceipt>(score.Output, PluginJson.Options)!;
        Assert.AreEqual(1d, receipt.Value.Score); Assert.AreEqual("caller-supplied", receipt.InputOrigin);
        Assert.AreEqual(new OperatorScopeRef("w", "a", "s"), receipt.Scope); Assert.IsNull(receipt.Value.Provenance.ModelId);
        var cold = await f.Call(new { action = "score", input = new SkillValueInput("技能", 0, 0, 0) }); Assert.IsTrue(cold.Success, cold.Error);
        Assert.IsNull(JsonSerializer.Deserialize<OperatorScoreReceipt>(cold.Output, PluginJson.Options)!.Value.Score);
        var forged = await f.Call(new { action = "forge" }); Assert.IsFalse(forged.Success); Assert.AreEqual("request.invalid", forged.Status);
        var stale = f.Tool(); Assert.AreEqual("Unloaded", (await f.Service.Manager.UnloadAsync(loaded.ActivationId, Fixture.Scope)).State);
        try { using var process = Process.GetProcessById(loaded.ProcessId!.Value); Assert.IsTrue(process.HasExited || process.WaitForExit(5000)); } catch (ArgumentException) { }
        Assert.AreEqual(pid, Environment.ProcessId);
        Assert.IsFalse((await stale.ExecuteAsync(new() { Context = f.Context, ToolCallId = "stale", ArgumentsJson = "{\"action\":\"list\"}" })).Success);
    }
    [TestMethod]
    public async Task CatalogOnlyCapability_CannotExecuteScoring()
    {
        await using var f = await Fixture.Create(score: false); Assert.AreEqual("Active", (await f.Load()).State);
        var list = await f.Call(new { action = "list" }); Assert.IsTrue(list.Success, list.Error);
        Assert.HasCount(0, JsonSerializer.Deserialize<OperatorCatalog>(list.Output, PluginJson.Options)!.Items);
        var denied = await f.Call(new { action = "score", input = new SkillValueInput("s", 1, 0, 0) });
        Assert.IsFalse(denied.Success); Assert.AreEqual("plugin.core_callback_denied", denied.Status);
    }
    [TestMethod]
    public async Task ScoringFailure_DoesNotCertifyEarlierToolEffectsAsNone()
    {
        await using var f = await Fixture.Create(state: true); Assert.AreEqual("Active", (await f.Load()).State);
        var failure = await f.Call(new { action = "write-then-score", input = new SkillValueInput("s", -1, 0, 0) });
        Assert.IsFalse(failure.Success); Assert.AreEqual("request.invalid", failure.Status);
        Assert.AreEqual("unknown", JsonSerializer.Deserialize<PluginFailure>(failure.Output, PluginJson.Options)!.EffectOutcome);
        var read = await f.Call(new { action = "read-state" }); Assert.IsTrue(read.Success, read.Error);
        Assert.AreEqual("written", JsonDocument.Parse(read.Output).RootElement.GetProperty("value").GetString());
    }
    [TestMethod]
    public async Task InvalidObservations_AreTypedFailuresThroughWorker()
    {
        await using var f = await Fixture.Create(); Assert.AreEqual("Active", (await f.Load()).State);
        var invalid = await f.Call(new { action = "score", input = new SkillValueInput("s", int.MaxValue, 1, 0) });
        Assert.IsFalse(invalid.Success); Assert.AreEqual("request.invalid", invalid.Status);
        Assert.IsTrue((await f.Call(new { action = "list" })).Success);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly PluginScope Scope = new("w", "a", "s");
        private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT") ?? throw new InvalidOperationException("Test root required."), "operator-" + Guid.NewGuid().ToString("N"));
        public ExecutablePluginService Service { get; private set; } = null!;
        private PluginPackage _package = null!;
        public ToolExecutionContext Context => new() { WorkspaceId = "w", AgentInstanceId = "a", SessionId = "s", WorkingDirectory = _root };
        public static async Task<Fixture> Create(bool score = true, bool state = false)
        {
            var f = new Fixture(); var access = new OperatorAccessService([new SkillValueAccessAdapter(new(new OperatorEnvironment()))]);
            var worker = Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER") ?? throw new InvalidOperationException("Worker required.");
            f.Service = new(PuddingDataPaths.FromRoot(f._root), new WindowsPluginWorkerFactory(worker), operatorApi: new PluginOperatorApiLeaseFactory(access));
            var manifest = new PluginManifest { Id = "sample.operator", Name = "Scoring", Version = "1.0.0", CoreApi = "1.2",
                Entry = new("fixture.dll", "Sample.PluginFixture.OperatorPlugin"),
                RequestedCapabilities = ["operators.list", .. score ? new[] { "operators.skill-value.score" } : [], .. state ? new[] { "state.read", "state.write" } : []],
                Tools = [new() { Id = "score", Name = "score", Description = "Fixture", Effects = state ? ["filesystem.write"] : ["none"],
                    InputSchema = PluginJson.Value(new { type = "object" }), OutputSchema = PluginJson.Value(new { type = "object" }) }] };
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open())) writer.Write(JsonSerializer.Serialize(manifest, PluginJson.Options));
                using var target = zip.CreateEntry("fixture.dll").Open(); using var dll = File.OpenRead(typeof(Sample.PluginFixture.OperatorPlugin).Assembly.Location); dll.CopyTo(target);
            }
            stream.Position = 0; f._package = await f.Service.Packages.ImportAsync(stream, default); return f;
        }
        public Task<Pudding.ToolPlugins.Runtime.PluginActivation> Load() => Service.Manager.LoadAsync("load", _package, Scope, new(true, _package.Manifest.RequestedCapabilities.ToHashSet(), false));
        public IPuddingTool Tool() => Service.ListTools(Context).Single();
        public Task<ToolExecutionResult> Call(object args) => Tool().ExecuteAsync(new() { Context = Context, ToolCallId = Guid.NewGuid().ToString("N"), ArgumentsJson = JsonSerializer.Serialize(args, PluginJson.Options) });
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            for (var attempt = 0; ; attempt++)
                try { Directory.Delete(_root, true); return; }
                catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException) { await Task.Delay(50); }
        }
    }
}
