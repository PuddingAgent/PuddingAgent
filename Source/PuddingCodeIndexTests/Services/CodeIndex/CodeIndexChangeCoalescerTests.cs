using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

[TestClass]
public sealed class CodeIndexChangeCoalescerTests
{
    /// <summary>§4.3 — Created + Changed on one path collapse into a single "re-read final state" path.</summary>
    [TestMethod]
    public void Created_And_Changed_For_Same_Path_Collapse_Into_One_Path_To_Reindex()
    {
        using var harness = new CoalescerHarness();

        harness.Publish("src/a.cs", IndexChangeKind.Created);
        harness.Advance(TimeSpan.FromMilliseconds(100));
        harness.Publish("src/a.cs", IndexChangeKind.Changed);

        // The silence window is still open, so nothing may be produced yet.
        Assert.IsFalse(harness.Drain(out _));

        harness.Advance(CodeIndexChangeCoalescer.DefaultSilenceWindow);
        Assert.IsTrue(harness.Drain(out var batch));
        Assert.IsNotNull(batch);

        Assert.AreEqual(1, batch!.PathsToReindex.Count);
        Assert.AreEqual(harness.FullPath("src/a.cs"), batch.PathsToReindex[0]);
        Assert.AreEqual(0, batch.PathsToRemove.Count);
        Assert.IsFalse(batch.ReconcileRequired);
        Assert.IsNull(batch.ReconcileReason);
        Assert.AreEqual(CodeIndexChangeTestFactory.WorkspaceId, batch.WorkspaceId);
        Assert.AreEqual(CodeIndexChangeTestFactory.ScopeId, batch.ScopeId);

        // The pending set is consumed exactly once.
        Assert.IsFalse(harness.Drain(out _));
    }

    /// <summary>§4.4 — a continuously busy tree is still flushed once the maximum wait elapses.</summary>
    [TestMethod]
    public void Batch_Is_Produced_When_Max_Wait_Elapses_Even_Though_Silence_Never_Does()
    {
        using var harness = new CoalescerHarness();
        var start = harness.Clock.GetUtcNow();

        Assert.AreEqual(TimeSpan.FromMilliseconds(500), harness.Coalescer.SilenceWindow);
        Assert.AreEqual(TimeSpan.FromSeconds(2), harness.Coalescer.MaxWait);

        // An observation every 300 ms keeps the 500 ms silence window permanently open.
        for (var i = 0; i < 7; i++)
        {
            harness.Publish($"src/generated/{i}.cs", IndexChangeKind.Changed);
            harness.Advance(TimeSpan.FromMilliseconds(300));
        }

        // Clock: last observation at 1800 ms, oldest pending at 0 ms, now at 2100 ms.

        harness.Clock.Set(start.AddMilliseconds(1999));
        Assert.IsFalse(harness.Drain(out _), "1999 ms is inside both windows");

        harness.Clock.Set(start.AddMilliseconds(2001));
        Assert.IsTrue(harness.Drain(out var batch), "the 2 s maximum wait must flush the batch");
        Assert.IsNotNull(batch);
        Assert.AreEqual(7, batch!.PathsToReindex.Count);
        Assert.AreEqual(7, batch.Version, "the batch version is the highest sequence it covers");
    }

    /// <summary>§4.5 — a rename yields both the removal of the old path and the re-read of the new path.</summary>
    [TestMethod]
    public void Renamed_Produces_Old_Path_Removal_And_New_Path_Reindex()
    {
        using var harness = new CoalescerHarness();

        var sequence = harness.Publish("src/new-name.cs", IndexChangeKind.Renamed, oldRelativePath: "src/old-name.cs");
        harness.Advance(CodeIndexChangeCoalescer.DefaultSilenceWindow);

        Assert.IsTrue(harness.Drain(out var batch));
        Assert.IsNotNull(batch);

        Assert.AreEqual(1, batch!.PathsToRemove.Count);
        Assert.AreEqual(harness.FullPath("src/old-name.cs"), batch.PathsToRemove[0]);
        Assert.AreEqual(1, batch.PathsToReindex.Count);
        Assert.AreEqual(harness.FullPath("src/new-name.cs"), batch.PathsToReindex[0]);
        Assert.AreEqual(sequence, batch.Version);
    }

    /// <summary>§4.6 — Deleted followed by Created on the same path means "re-read", not "remove".</summary>
    [TestMethod]
    public void Deleted_Followed_By_Created_On_Same_Path_Becomes_Reindex()
    {
        using var harness = new CoalescerHarness();

        harness.Publish("src/a.cs", IndexChangeKind.Deleted);
        harness.Advance(TimeSpan.FromMilliseconds(50));
        harness.Publish("src/a.cs", IndexChangeKind.Created);
        harness.Advance(CodeIndexChangeCoalescer.DefaultSilenceWindow);

        Assert.IsTrue(harness.Drain(out var batch));
        Assert.IsNotNull(batch);

        Assert.AreEqual(0, batch!.PathsToRemove.Count, "the path exists again, so it must not be removed");
        Assert.AreEqual(1, batch.PathsToReindex.Count);
        Assert.AreEqual(harness.FullPath("src/a.cs"), batch.PathsToReindex[0]);
    }

    /// <summary>Mirror of §4.6 — Created followed by Deleted means "remove".</summary>
    [TestMethod]
    public void Created_Followed_By_Deleted_On_Same_Path_Becomes_Removal()
    {
        using var harness = new CoalescerHarness();

        harness.Publish("src/gone.cs", IndexChangeKind.Created);
        harness.Advance(TimeSpan.FromMilliseconds(50));
        harness.Publish("src/gone.cs", IndexChangeKind.Deleted);
        harness.Advance(CodeIndexChangeCoalescer.DefaultSilenceWindow);

        Assert.IsTrue(harness.Drain(out var batch));
        Assert.IsNotNull(batch);

        Assert.AreEqual(1, batch!.PathsToRemove.Count);
        Assert.AreEqual(harness.FullPath("src/gone.cs"), batch.PathsToRemove[0]);
        Assert.AreEqual(0, batch.PathsToReindex.Count);
    }

    /// <summary>§4.7 — exceeding the pending-path budget folds fine-grained work into a scope reconcile.</summary>
    [TestMethod]
    public void Path_Budget_Overflow_Collapses_Into_Reconcile_With_Empty_Path_Lists()
    {
        Assert.AreEqual(20000, CodeIndexChangeCoalescer.DefaultMaxPendingPaths);

        using var harness = new CoalescerHarness(maxPendingPaths: 4);

        for (var i = 0; i < 5; i++)
        {
            harness.Publish($"src/collapse-{i}.cs", IndexChangeKind.Changed);
            harness.Advance(TimeSpan.FromMilliseconds(10));
        }

        // Observations are merged lazily, so pump the queue before inspecting the collapse.
        Assert.AreEqual(5, harness.Coalescer.Accumulate());
        Assert.AreEqual(1, harness.Coalescer.CollapseCount);

        harness.Advance(CodeIndexChangeCoalescer.DefaultSilenceWindow);
        Assert.IsTrue(harness.Drain(out var batch));
        Assert.IsNotNull(batch);

        Assert.IsTrue(batch!.ReconcileRequired);
        Assert.AreEqual(CodeIndexScopeState.ReconcileReasons.PathLimitExceeded, batch.ReconcileReason);
        Assert.AreEqual(0, batch.PathsToReindex.Count, "collapsed work must release the path records");
        Assert.AreEqual(0, batch.PathsToRemove.Count);
        Assert.AreEqual(5, batch.Version);
        Assert.AreEqual(0, harness.Coalescer.PendingPathCount);

        Assert.IsTrue(harness.State.NeedsReconcile);
        Assert.AreEqual(CodeIndexScopeState.ReconcileReasons.PathLimitExceeded, harness.State.ReconcileReason);

        // The reconcile notice is one-shot: no empty batches afterwards.
        Assert.IsFalse(harness.Drain(out _));
    }

    [TestMethod]
    public void Removal_Paths_Are_Deduplicated_Independently_From_Reindex_Paths()
    {
        using var harness = new CoalescerHarness();

        harness.Publish("src/a.cs", IndexChangeKind.Deleted);
        harness.Advance(TimeSpan.FromMilliseconds(10));
        harness.Publish("src/b.cs", IndexChangeKind.Deleted);
        harness.Advance(TimeSpan.FromMilliseconds(10));
        harness.Publish("src/a.cs", IndexChangeKind.Deleted);
        harness.Publish("src/c.cs", IndexChangeKind.Changed);
        harness.Publish("src/c.cs", IndexChangeKind.Changed);
        harness.Advance(CodeIndexChangeCoalescer.DefaultSilenceWindow);

        Assert.IsTrue(harness.Drain(out var batch));
        Assert.IsNotNull(batch);

        Assert.AreEqual(2, batch!.PathsToRemove.Count);
        var removals = batch.PathsToRemove.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.AreEqual(harness.FullPath("src/a.cs"), removals[0]);
        Assert.AreEqual(harness.FullPath("src/b.cs"), removals[1]);
        Assert.AreEqual(1, batch.PathsToReindex.Count);
        Assert.AreEqual(harness.FullPath("src/c.cs"), batch.PathsToReindex[0]);
    }

    [TestMethod]
    public void Reconcile_Flag_From_Scope_State_Is_Reported_On_The_Next_Batch()
    {
        using var harness = new CoalescerHarness();

        harness.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.QueueOverflow);
        harness.Publish("src/a.cs", IndexChangeKind.Changed);
        harness.Advance(CodeIndexChangeCoalescer.DefaultSilenceWindow);

        Assert.IsTrue(harness.Drain(out var batch));
        Assert.IsNotNull(batch);

        Assert.IsTrue(batch!.ReconcileRequired);
        Assert.AreEqual(CodeIndexScopeState.ReconcileReasons.QueueOverflow, batch.ReconcileReason);
    }

    [TestMethod]
    public void Accumulate_Consumes_Everything_Currently_Queued()
    {
        using var harness = new CoalescerHarness();

        harness.Publish("src/a.cs", IndexChangeKind.Changed);
        harness.Publish("src/b.cs", IndexChangeKind.Changed);

        Assert.AreEqual(2, harness.Coalescer.Accumulate());
        Assert.AreEqual(0, harness.Queue.Depth);
        Assert.AreEqual(2, harness.Coalescer.PendingPathCount);
        Assert.AreEqual(0, harness.Coalescer.Accumulate());
    }

    [TestMethod]
    public void Constructor_Validates_Window_And_Budget_Parameters()
    {
        var state = CodeIndexChangeTestFactory.CreateState();
        var queue = new CodeIndexChangeQueue(8);

        Assert.IsTrue(Throws<ArgumentOutOfRangeException>(() => new CodeIndexChangeCoalescer(queue, state, silenceWindow: TimeSpan.Zero)));
        Assert.IsTrue(Throws<ArgumentOutOfRangeException>(() => new CodeIndexChangeCoalescer(queue, state, maxWait: TimeSpan.Zero)));
        Assert.IsTrue(Throws<ArgumentOutOfRangeException>(() => new CodeIndexChangeCoalescer(queue, state, maxPendingPaths: 0)));
        Assert.IsTrue(Throws<ArgumentNullException>(() => new CodeIndexChangeCoalescer(null!, state)));
        Assert.IsTrue(Throws<ArgumentNullException>(() => new CodeIndexChangeCoalescer(queue, null!)));

        var coalescer = new CodeIndexChangeCoalescer(queue, state);
        Assert.AreEqual(20000, CodeIndexChangeCoalescer.DefaultMaxPendingPaths);
        Assert.AreEqual(20000, coalescer.MaxPendingPaths);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), coalescer.SilenceWindow);
        Assert.AreEqual(TimeSpan.FromSeconds(2), coalescer.MaxWait);
    }

    [TestMethod]
    public async Task WaitForBatchAsync_Produces_A_Batch_On_The_Real_Clock()
    {
        using var temp = new TestDirectory();
        var state = CodeIndexChangeTestFactory.CreateState();
        var queue = new CodeIndexChangeQueue(256);
        var coalescer = new CodeIndexChangeCoalescer(
            queue,
            state,
            timeProvider: null,
            silenceWindow: TimeSpan.FromMilliseconds(50),
            maxWait: TimeSpan.FromMilliseconds(250));

        Assert.IsTrue(queue.TryPublish(CodeIndexChangeTestFactory.CreateChange(
            temp.Root, "src/live.cs", IndexChangeKind.Created, state.NextSequence())));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var batch = await coalescer.WaitForBatchAsync(cts.Token);

        Assert.IsNotNull(batch);
        Assert.AreEqual(1, batch!.PathsToReindex.Count);
        Assert.AreEqual(temp.Combine("src/live.cs"), batch.PathsToReindex[0]);
        Assert.AreEqual(1, batch.Version);
    }

    [TestMethod]
    public async Task WaitForBatchAsync_Returns_Null_When_The_Queue_Is_Completed_And_Empty()
    {
        var state = CodeIndexChangeTestFactory.CreateState();
        var queue = new CodeIndexChangeQueue(8);
        var coalescer = new CodeIndexChangeCoalescer(queue, state);

        queue.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.IsNull(await coalescer.WaitForBatchAsync(cts.Token));
    }

    private static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}

/// <summary>A coalescer wired to a temp root, a manual clock and a bounded queue.</summary>
internal sealed class CoalescerHarness : IDisposable
{
    public CoalescerHarness(
        int maxPendingPaths = CodeIndexChangeCoalescer.DefaultMaxPendingPaths,
        TimeSpan? silenceWindow = null,
        TimeSpan? maxWait = null)
    {
        Temp = new TestDirectory();
        Clock = new MutableTimeProvider();
        State = CodeIndexChangeTestFactory.CreateState();
        Queue = new CodeIndexChangeQueue(capacity: 4096);
        Coalescer = new CodeIndexChangeCoalescer(Queue, State, Clock, silenceWindow, maxWait, maxPendingPaths);
    }

    public TestDirectory Temp { get; }

    public MutableTimeProvider Clock { get; }

    public CodeIndexScopeState State { get; }

    public CodeIndexChangeQueue Queue { get; }

    public CodeIndexChangeCoalescer Coalescer { get; }

    public string FullPath(string relativePath) => Temp.Combine(relativePath);

    public void Advance(TimeSpan delta) => Clock.Advance(delta);

    public long Publish(string relativePath, IndexChangeKind kind, string? oldRelativePath = null)
    {
        var sequence = State.NextSequence();
        var accepted = Queue.TryPublish(CodeIndexChangeTestFactory.CreateChange(
            Temp.Root, relativePath, kind, sequence, oldRelativePath, Clock.GetUtcNow()));

        Assert.IsTrue(accepted, "the harness queue must never overflow");

        return sequence;
    }

    public bool Drain(out CodeIndexChangeBatch? batch) => Coalescer.TryDrainAt(Clock.GetUtcNow(), out batch);

    public void Dispose() => Temp.Dispose();
}
