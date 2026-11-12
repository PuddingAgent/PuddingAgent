using System.IO.Compression;
using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Windows;
using PuddingCode.Configuration;
using PuddingCode.Tools;
using PuddingExecutionResources;
using PuddingExecutionResources.Contracts;
using PuddingModelEvaluation;
using PuddingRuntime.Operators.PublicApi;
using PuddingRuntime.Services.Plugins;

[assembly: DoNotParallelize]
namespace PuddingModelEvaluation.IntegrationTests;

[TestClass]
public sealed class ModelWorkerTests
{

    [TestMethod]
    public async Task ParentCancellationEndsWorkerCall_LateProviderKeepsSlotThroughUnload()
    {
        await using var f = await Fixture.Create(); Assert.AreEqual("Active", (await f.Load()).State); f.Model.Block = true;
        var call = f.Call("classify"); await f.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Parent.Dispose();
        var result = await call.WaitAsync(TimeSpan.FromSeconds(5)); Assert.IsFalse(result.Success);
        Assert.AreEqual("execution.revoked", result.Status); Assert.AreEqual(1, f.Parent.Snapshot().ActiveLeases);
        var loaded = f.Service.Manager.List().Single();
        Assert.AreEqual("Unloaded", (await f.Service.Manager.UnloadAsync(loaded.ActivationId, Fixture.Scope)).State);
        Assert.AreEqual(1, f.Parent.Snapshot().ActiveLeases);
        f.Model.Finish.TrySetResult(new("late", new(100, 10), "stop"));
        for (var i = 0; i < 100 && f.Parent.Snapshot().ActiveLeases != 0; i++) await Task.Delay(10);
        Assert.AreEqual(0, f.Parent.Snapshot().ActiveLeases); Assert.AreEqual(10L, f.Parent.Snapshot().OutputTokens);
        Assert.AreEqual("budget.usage_unknown", f.Parent.Snapshot().FailureCode);
    }
    [TestMethod]
    public async Task RealWorker_ClassifiesReviewsAndAccounts_RejectsForgedParent_ThenUnloads()
    {
        await using var f = await Fixture.Create(); Assert.AreEqual("Active", (await f.Load()).State);
        var list = await f.Call("list"); Assert.IsTrue(list.Success, list.Error);
        Assert.HasCount(2, JsonSerializer.Deserialize<OperatorCatalog>(list.Output, PluginJson.Options)!.Items);
        var result = await f.Call("classify"); Assert.IsTrue(result.Success, result.Error);
        var receipt = JsonSerializer.Deserialize<ClassificationReceipt>(result.Output, PluginJson.Options)!;
        Assert.AreEqual("work", receipt.Label); Assert.AreEqual("run", receipt.Provenance.ParentRunId);
        Assert.AreEqual(new OperatorScopeRef("w", "a", "s"), receipt.Provenance.Scope);
        var review = await f.Call("review"); Assert.IsTrue(review.Success, review.Error);
        Assert.AreEqual(AcceptanceDecision.Accept, JsonSerializer.Deserialize<AcceptanceReceipt>(review.Output, PluginJson.Options)!.Decision);
        Assert.AreEqual(2, f.Model.Calls); Assert.AreEqual(20L, f.Parent.Snapshot().OutputTokens);
        Assert.AreEqual(.00022m, f.Parent.Snapshot().Cost);
        Assert.AreEqual("request.invalid", (await f.Call("forge")).Status); Assert.AreEqual(2, f.Model.Calls);
        var loaded = f.Service.Manager.List().Single();
        Assert.AreEqual("Unloaded", (await f.Service.Manager.UnloadAsync(loaded.ActivationId, Fixture.Scope)).State);
    }
    [TestMethod]
    public async Task ParentCallsAndOutputCannotResetAcrossPluginReload_ExternalUsageCounts()
    {
        await using var f = await Fixture.Create(maxCalls: 1); Assert.AreEqual("Active", (await f.Load()).State);
        f.Parent.RecordExternal(new(10, 20), .1m);
        Assert.IsTrue((await f.Call("classify")).Success);
        var loaded = f.Service.Manager.List().Single(); await f.Service.Manager.UnloadAsync(loaded.ActivationId, Fixture.Scope);
        Assert.AreEqual("Active", (await f.Load()).State);
        Assert.AreEqual("budget.exhausted", (await f.Call("classify")).Status);
        Assert.AreEqual(1, f.Model.Calls); Assert.AreEqual(30L, f.Parent.Snapshot().OutputTokens);
        Assert.AreEqual(.10011m, f.Parent.Snapshot().Cost);
    }
    [TestMethod]
    public async Task MissingParentAndMissingCapabilityAreDeniedBeforeModelDispatch()
    {
        await using var f = await Fixture.Create(classify: false); Assert.AreEqual("Active", (await f.Load()).State);
        Assert.AreEqual("plugin.core_callback_denied", (await f.Call("classify")).Status);
        Assert.AreEqual("execution.parent_required", (await f.Call("list", parent: false)).Status);
        Assert.AreEqual(0, f.Model.Calls);
    }
    [TestMethod]
    public async Task UpperComponent_CancellationRevokesLease_UnknownUsageBlocksSibling()
    {
        using var parent = Fixture.ParentScope(); var model = new FakeModel();
        var factory = new ModelEvaluationApiFactory(new(new ModelResourcePool()), model);
        var context = new ToolExecutionContext { WorkspaceId = "w", AgentInstanceId = "a", SessionId = "s", ExecutionResources = parent };
        var lease = factory.Open(context, parent.Deadline, true, true, true);
        model.MissingUsage = true;
        var request = new ClassificationRequest(new("text", [new("work", "work"), new("chat", "chat")]), new("test", 64));
        Assert.AreEqual("model.usage_unknown", (await Assert.ThrowsExactlyAsync<ModelEvaluationException>(() => lease.ClassifyAsync(request))).Code);
        Assert.AreEqual(64L, parent.Snapshot().UnknownOutputTokens);
        await lease.DisposeAsync();
        Assert.AreEqual("execution.revoked", (await Assert.ThrowsExactlyAsync<ModelEvaluationException>(() => lease.ListAsync())).Code);
        await using var sibling = factory.Open(context, parent.Deadline, true, true, true);
        Assert.AreEqual("budget.usage_unknown", (await Assert.ThrowsExactlyAsync<ModelEvaluationException>(() => sibling.ClassifyAsync(request))).Code);
        Assert.AreEqual(1, model.Calls); parent.Dispose();
        Assert.AreEqual("execution.revoked", (await Assert.ThrowsExactlyAsync<ModelEvaluationException>(() => sibling.ListAsync())).Code);
    }
    private sealed class FakeModel : IExecutionModelBinder, ILeasedModel
    {
        public int Calls; public bool MissingUsage, Block;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ModelCompletion> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelResourceDescriptor Descriptor => new("fixture/model", "revision", "model", 8192, 2048, 2, new(1, 1, 1));
        public Task<ILeasedModel> BindAsync(ToolExecutionContext context, CancellationToken ct) => Task.FromResult<ILeasedModel>(this);
        public Task<ModelCompletion> InvokeAsync(ModelResourceRequest request, Action dispatch, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); dispatch(); Calls++;
            if (Block) { Entered.TrySetResult(); return Finish.Task; }
            var text = request.Instruction.StartsWith("Classify", StringComparison.Ordinal)
                ? "{\"label\":\"work\",\"reason\":\"requires work\",\"confidence\":0.8}"
                : "{\"decision\":\"accept\",\"reason\":\"supplied log supports criterion\",\"confidence\":0.8,\"checks\":[{\"criterionId\":\"test\",\"outcome\":\"supported\",\"evidenceIds\":[\"log\"]}]}";
            return Task.FromResult(new ModelCompletion(text, MissingUsage ? null : new(100, 10), "stop"));
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly PluginScope Scope = new("w", "a", "s");
        internal static ExecutionResourceScope ParentScope(int calls = 8) => new(new("w", "a", "s", "run"), new(512, 1m, calls), DateTimeOffset.UtcNow.AddMinutes(2));
        private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT") ?? throw new InvalidOperationException("Test root required."), "model-" + Guid.NewGuid().ToString("N"));
        public ExecutionResourceScope Parent = null!; public FakeModel Model = new();
        public ExecutablePluginService Service = null!; private PluginPackage _package = null!;
        public static async Task<Fixture> Create(int maxCalls = 8, bool classify = true)
        {
            var f = new Fixture { Parent = ParentScope(maxCalls) };
            var worker = Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER") ?? throw new InvalidOperationException("Worker required.");
            f.Service = new(PuddingDataPaths.FromRoot(f._root), new WindowsPluginWorkerFactory(worker), modelApi: new(new(new ModelResourcePool()), f.Model));
            var manifest = new PluginManifest { Id = "sample.model", Name = "Model", Version = "1.0.0", CoreApi = "1.3",
                Entry = new("fixture.dll", "Sample.PluginFixture.ModelEvaluationPlugin"),
                RequestedCapabilities = ["operators.model.list", "operators.delivery.review", .. classify ? new[] { "operators.text.classify" } : []],
                Tools = [new() { Id = "evaluate", Name = "evaluate", Description = "Fixture", Effects = ["none"], InputSchema = PluginJson.Value(new { type = "object" }), OutputSchema = PluginJson.Value(new { type = "object" }) }] };
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open())) writer.Write(JsonSerializer.Serialize(manifest, PluginJson.Options));
                using var target = zip.CreateEntry("fixture.dll").Open(); using var dll = File.OpenRead(typeof(Sample.PluginFixture.ModelEvaluationPlugin).Assembly.Location); dll.CopyTo(target);
            }
            stream.Position = 0; f._package = await f.Service.Packages.ImportAsync(stream, default); return f;
        }
        public Task<Pudding.ToolPlugins.Runtime.PluginActivation> Load() => Service.Manager.LoadAsync(Guid.NewGuid().ToString("N"), _package, Scope, new(true, _package.Manifest.RequestedCapabilities.ToHashSet(), false));
        public Task<ToolExecutionResult> Call(string action, bool parent = true)
        {
            var context = new ToolExecutionContext { WorkspaceId = "w", AgentInstanceId = "a", SessionId = "s", WorkingDirectory = _root, ExecutionResources = parent ? Parent : null };
            return Service.ListTools(context).Single().ExecuteAsync(new() { Context = context, ToolCallId = Guid.NewGuid().ToString("N"), ArgumentsJson = JsonSerializer.Serialize(new { action }) });
        }
        public async ValueTask DisposeAsync() { Parent.Dispose(); Model.Finish.TrySetResult(new("late", new(100, 10), "stop")); await Service.DisposeAsync(); }
    }
}
