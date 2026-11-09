using PuddingExecutionResources;
using PuddingExecutionResources.Contracts;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
namespace PuddingExecutionResourcesTests;

[TestClass]
public sealed class ResourceTests
{
    [TestMethod]
    public async Task ConcurrentSiblingsCannotOverbookOneParentReservation()
    {
        using var root = Root(30, 16);
        var held = new System.Collections.Concurrent.ConcurrentBag<IModelBudgetReservation>();
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            using var child = root.CreateChild(new(), root.Deadline);
            try { held.Add(child.Reserve(Descriptor, 30)); return "reserved"; }
            catch (ExecutionResourceException ex) { return ex.Code; }
        }));
        var results = await Task.WhenAll(tasks);
        Assert.AreEqual(1, results.Count(x => x == "reserved")); Assert.AreEqual(15, results.Count(x => x == "budget.exhausted"));
        Assert.AreEqual(30L, root.Snapshot().ReservedOutputTokens);
        foreach (var reservation in held) reservation.Dispose();
        Assert.AreEqual(0L, root.Snapshot().ReservedOutputTokens);
    }
    private static readonly ModelResourceDescriptor Descriptor = new("provider/model", "v1", "model", 100, 100, 1, new(1, 2, .5m));
    private static ExecutionResourceScope Root(long output = 100, int concurrent = 2) => new(new("w", "a", "s", "run"), new(output, 1m, 8, concurrent), DateTimeOffset.UtcNow.AddMinutes(1));
    private static ModelResourceRequest Request(int output = 30, int timeout = 5000) => new("call", "instruction", "input", output, timeout);
    [TestMethod]
    public void SiblingsShareAtomicReservations_AndCannotResetRootBudget()
    {
        using var root = Root(50); using var a = root.CreateChild(new(), root.Deadline); using var b = root.CreateChild(new(), root.Deadline);
        using var first = a.Reserve(Descriptor, 30);
        Assert.AreEqual("budget.exhausted", Assert.ThrowsExactly<ExecutionResourceException>(() => b.Reserve(Descriptor, 30)).Code);
        first.MarkDispatched(); first.Settle(new(10, 15));
        Assert.AreEqual(15L, root.Snapshot().OutputTokens);
        using var next = b.Reserve(Descriptor, 35); Assert.AreEqual(35L, root.Snapshot().ReservedOutputTokens);
    }
    [TestMethod]
    public void ZeroIsExhausted_AndParentCancellationPropagates()
    {
        using var zero = Root(0); Assert.ThrowsExactly<ExecutionResourceException>(() => zero.Reserve(Descriptor, 1));
        using var root = Root(); using var child = root.CreateChild(new(), root.Deadline.AddMinutes(1));
        Assert.AreEqual("execution.revoked", Assert.ThrowsExactly<ExecutionResourceException>(() => root.CreateChild(new(), DateTimeOffset.UtcNow.AddSeconds(-1))).Code);
        Assert.AreEqual(root.Deadline, child.Deadline); root.Dispose(); Assert.IsTrue(child.Cancellation.IsCancellationRequested);
        Assert.AreEqual("execution.revoked", Assert.ThrowsExactly<ExecutionResourceException>(() => child.Reserve(Descriptor, 1)).Code);
    }
    [TestMethod]
    public void RefundOnlyBeforeDispatch_UnknownRetainsCeiling_OverrunKeepsActual()
    {
        using var root = Root(); using (var unused = root.Reserve(Descriptor, 30)) { }
        Assert.AreEqual(0L, root.Snapshot().UnknownOutputTokens); Assert.AreEqual(0L, root.Snapshot().ReservedOutputTokens);
        using (var uncertain = root.Reserve(Descriptor, 30)) uncertain.MarkDispatched();
        Assert.AreEqual(30L, root.Snapshot().UnknownOutputTokens);
        Assert.AreEqual("budget.usage_unknown", Assert.ThrowsExactly<ExecutionResourceException>(() => root.Reserve(Descriptor, 1)).Code);
        using var other = Root(); using var over = other.Reserve(Descriptor, 10); over.MarkDispatched();
        Assert.AreEqual("budget.provider_overrun", Assert.ThrowsExactly<ExecutionResourceException>(() => over.Settle(new(20, 11))).Code);
        Assert.AreEqual(11L, other.Snapshot().OutputTokens); Assert.AreEqual(0, other.Snapshot().ActiveLeases);
    }
    [TestMethod]
    public void SettlementAndDispatchCannotRepeat_ExternalUsageAlsoConsumesParent()
    {
        using var root = Root(50); root.RecordExternal(new(20, 20, 5), .1m);
        using var reservation = root.Reserve(Descriptor, 30); reservation.MarkDispatched();
        Assert.ThrowsExactly<ExecutionResourceException>(reservation.MarkDispatched);
        reservation.Settle(new(10, 5)); Assert.ThrowsExactly<ExecutionResourceException>(() => reservation.Settle(new(10, 5)));
        Assert.AreEqual(25L, root.Snapshot().OutputTokens); Assert.AreEqual(5L, root.Snapshot().CacheHitTokens);
    }
    [TestMethod]
    public async Task LateProviderRetainsSlotAndReservation_NoLateSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ModelCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Model(async (_, dispatch, _) => { dispatch(); entered.SetResult(); return await finish.Task; });
        var pool = new ModelResourcePool(); using var root = Root();
        using var stop = new CancellationTokenSource();
        var call = pool.InvokeAsync(root, model, Request(), stop.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => call);
        Assert.AreEqual(1, root.Snapshot().ActiveLeases); Assert.AreEqual(30L, root.Snapshot().ReservedOutputTokens);
        using var siblingRoot = Root(); var queued = pool.InvokeAsync(siblingRoot, model, Request(timeout: 30));
        Assert.AreEqual("model.timeout", (await Assert.ThrowsExactlyAsync<ExecutionResourceException>(() => queued)).Code);
        Assert.AreEqual(0L, siblingRoot.Snapshot().UnknownOutputTokens); Assert.AreEqual(1, model.Calls);
        finish.SetResult(new("late", new(10, 5), "stop"));
        for (var i = 0; i < 100 && root.Snapshot().ActiveLeases != 0; i++) await Task.Delay(10);
        Assert.AreEqual(0, root.Snapshot().ActiveLeases); Assert.AreEqual(5L, root.Snapshot().OutputTokens);
        Assert.AreEqual("budget.usage_unknown", root.Snapshot().FailureCode);
    }
    [TestMethod]
    public async Task ActualUsageSettled_AndMissingUsageCannotLookFree()
    {
        var pool = new ModelResourcePool(); using var root = Root();
        var model = new Model((_, dispatch, _) => { dispatch(); return Task.FromResult(new ModelCompletion("answer", new(20, 4, 5), "stop")); });
        var result = await pool.InvokeAsync(root, model, Request()); Assert.AreEqual(.0000255m, result.Cost);
        Assert.AreEqual(4L, root.Snapshot().OutputTokens); Assert.AreEqual(0, root.Snapshot().ActiveLeases);
        var missing = new Model((_, dispatch, _) => { dispatch(); return Task.FromResult(new ModelCompletion("answer", null, "stop")); });
        Assert.AreEqual("model.usage_unknown", (await Assert.ThrowsExactlyAsync<ExecutionResourceException>(() => pool.InvokeAsync(root, missing, Request()))).Code);
        Assert.AreEqual(30L, root.Snapshot().UnknownOutputTokens);
    }
    private sealed class Model(Func<ModelResourceRequest, Action, CancellationToken, Task<ModelCompletion>> run) : ILeasedModel
    {
        public int Calls;
        public ModelResourceDescriptor Descriptor => ResourceTests.Descriptor;
        public Task<ModelCompletion> InvokeAsync(ModelResourceRequest request, Action dispatch, CancellationToken ct) { Calls++; return run(request, dispatch, ct); }
    }
}
