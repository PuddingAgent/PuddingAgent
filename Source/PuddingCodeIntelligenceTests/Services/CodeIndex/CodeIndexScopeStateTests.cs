using System.Collections.Concurrent;
using PuddingCodeIntelligence.Services.CodeIndex;

namespace PuddingCodeIntelligenceTests.Services.CodeIndex;

[TestClass]
public sealed class CodeIndexScopeStateTests
{
    [TestMethod]
    public void NextSequence_Is_Monotonic_And_Unique_Under_Concurrency()
    {
        var state = CodeIndexChangeTestFactory.CreateState();
        var sequences = new ConcurrentBag<long>();

        Parallel.For(0, 1000, _ => sequences.Add(state.NextSequence()));

        Assert.AreEqual(1000, sequences.Count);
        Assert.AreEqual(1000, sequences.Distinct().Count(), "a sequence must never be handed out twice");
        Assert.AreEqual(1000, sequences.Max());
        Assert.AreEqual(1000, state.ObservedVersion);
    }

    [TestMethod]
    public void MarkObserved_Sets_Dirty_And_Last_Observed_Timestamp()
    {
        var state = CodeIndexChangeTestFactory.CreateState();
        var observedAtUtc = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

        Assert.IsFalse(state.Dirty);
        Assert.IsNull(state.LastObservedAtUtc);

        state.MarkObserved(observedAtUtc);

        Assert.IsTrue(state.Dirty);
        Assert.AreEqual(observedAtUtc, state.LastObservedAtUtc);
    }

    [TestMethod]
    public void MarkNeedsReconcile_Reports_Transition_And_Keeps_The_Latest_Reason()
    {
        var state = CodeIndexChangeTestFactory.CreateState();

        Assert.IsTrue(state.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.QueueOverflow));
        Assert.IsTrue(state.NeedsReconcile);
        Assert.AreEqual(CodeIndexScopeState.ReconcileReasons.QueueOverflow, state.ReconcileReason);

        Assert.IsFalse(state.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError));
        Assert.IsTrue(state.NeedsReconcile);
        Assert.AreEqual(CodeIndexScopeState.ReconcileReasons.WatcherError, state.ReconcileReason);
    }

    [TestMethod]
    public void Counters_Accumulate_Independently()
    {
        var state = CodeIndexChangeTestFactory.CreateState();

        Assert.AreEqual(1, state.RecordOverflow());
        Assert.AreEqual(3, state.RecordOverflow(2));
        Assert.AreEqual(1, state.RecordDroppedChange());
        Assert.AreEqual(2, state.RecordDroppedChange());

        Assert.AreEqual(3, state.OverflowCount);
        Assert.AreEqual(2, state.DroppedChangeCount);
    }

    [TestMethod]
    public void Reset_Clears_Flags_But_Keeps_Cumulative_Counters_And_Version()
    {
        var state = CodeIndexChangeTestFactory.CreateState();
        state.NextSequence();
        state.MarkObserved(DateTimeOffset.UnixEpoch);
        state.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.PathLimitExceeded);
        state.RecordOverflow();
        state.RecordDroppedChange();

        state.Reset();

        Assert.IsFalse(state.Dirty);
        Assert.IsFalse(state.NeedsReconcile);
        Assert.IsNull(state.ReconcileReason);
        Assert.AreEqual(1, state.ObservedVersion, "ObservedVersion is monotonic for the lifetime of the scope");
        Assert.AreEqual(1, state.OverflowCount, "observability counters survive a reconcile");
        Assert.AreEqual(1, state.DroppedChangeCount);

        Assert.AreEqual(2, state.NextSequence());
    }

    [TestMethod]
    public void Reset_With_Identity_Restamp_Keeps_Version_Monotonic()
    {
        var state = CodeIndexChangeTestFactory.CreateState();
        state.NextSequence();

        state.Reset("workspace-two", "scope-two");

        Assert.AreEqual("workspace-two", state.WorkspaceId);
        Assert.AreEqual("scope-two", state.ScopeId);
        Assert.IsFalse(state.NeedsReconcile);
        Assert.AreEqual(2, state.NextSequence());
    }

    [TestMethod]
    public void Snapshot_Is_An_Immutable_Copy()
    {
        var state = CodeIndexChangeTestFactory.CreateState();
        state.NextSequence();
        state.MarkObserved(DateTimeOffset.UnixEpoch);
        state.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
        state.RecordOverflow();
        state.RecordDroppedChange();

        var snapshot = state.Snapshot();

        state.Reset();
        state.NextSequence();

        Assert.AreEqual(CodeIndexChangeTestFactory.WorkspaceId, snapshot.WorkspaceId);
        Assert.AreEqual(CodeIndexChangeTestFactory.ScopeId, snapshot.ScopeId);
        Assert.IsTrue(snapshot.Dirty);
        Assert.AreEqual(1, snapshot.ObservedVersion);
        Assert.IsTrue(snapshot.NeedsReconcile);
        Assert.AreEqual(CodeIndexScopeState.ReconcileReasons.WatcherError, snapshot.ReconcileReason);
        Assert.AreEqual(1, snapshot.OverflowCount);
        Assert.AreEqual(1, snapshot.DroppedChangeCount);
        Assert.AreEqual(DateTimeOffset.UnixEpoch, snapshot.LastObservedAtUtc);

        Assert.IsFalse(state.NeedsReconcile);
        Assert.AreEqual(2, state.ObservedVersion);
    }
}
