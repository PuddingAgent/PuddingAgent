using System.Diagnostics;
using PuddingCodeIntelligence.Services.CodeIndex;

namespace PuddingCodeIntelligenceTests.Services.CodeIndex;

[TestClass]
public sealed class CodeIndexChangeQueueTests
{
    /// <summary>§4.1 — a full queue rejects the observation instead of blocking the watcher callback.</summary>
    [TestMethod]
    public void TryPublish_Returns_False_When_Queue_Is_Full_And_Does_Not_Block()
    {
        using var temp = new TestDirectory();
        var queue = new CodeIndexChangeQueue(capacity: 2);

        Assert.IsTrue(queue.TryPublish(CodeIndexChangeTestFactory.CreateChange(temp.Root, "a.cs", IndexChangeKind.Created, 1)));
        Assert.IsTrue(queue.TryPublish(CodeIndexChangeTestFactory.CreateChange(temp.Root, "b.cs", IndexChangeKind.Created, 2)));
        Assert.AreEqual(2, queue.Depth);

        var stopwatch = Stopwatch.StartNew();
        var accepted = queue.TryPublish(CodeIndexChangeTestFactory.CreateChange(temp.Root, "c.cs", IndexChangeKind.Created, 3));
        stopwatch.Stop();

        Assert.IsFalse(accepted, "a full queue must reject the observation instead of waiting for room");
        Assert.AreEqual(2, queue.Depth, "the rejected observation must not consume a slot");
        Assert.AreEqual(1, queue.DroppedCount);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"TryPublish blocked for {stopwatch.Elapsed}");
    }

    /// <summary>§4.2 — the caller (watcher) records the overflow on scope state, which stays usable while the queue is full.</summary>
    [TestMethod]
    public void Overflow_Marks_Scope_Needs_Reconcile_And_Increments_Overflow_Count()
    {
        using var temp = new TestDirectory();
        var state = CodeIndexChangeTestFactory.CreateState();
        var queue = new CodeIndexChangeQueue(capacity: 1);
        using var watcher = new CodeIndexWatcher(CodeIndexChangeTestFactory.CreateScope(temp.Root), queue, state);

        // Saturate the queue so the next observation cannot be published.
        Assert.IsTrue(queue.TryPublish(CodeIndexChangeTestFactory.CreateChange(temp.Root, "seed.cs", IndexChangeKind.Created, 99)));
        Assert.IsFalse(state.NeedsReconcile);
        Assert.AreEqual(0, state.OverflowCount);

        var published = watcher.HandleCreated(new FileSystemEventArgs(WatcherChangeTypes.Created, temp.Root, "overflow.cs"));

        Assert.IsFalse(published);
        Assert.IsTrue(state.NeedsReconcile, "an overflow must be recorded on the scope state, not silently dropped");
        Assert.AreEqual(CodeIndexScopeState.ReconcileReasons.QueueOverflow, state.ReconcileReason);
        Assert.AreEqual(1, state.OverflowCount);
        Assert.AreEqual(1, state.DroppedChangeCount);
        Assert.AreEqual(1, queue.DroppedCount);
        Assert.AreEqual(1, queue.Depth, "the previously buffered observation must be untouched");
        Assert.IsTrue(state.Dirty, "the change was observed even though it could not be published");
    }

    [TestMethod]
    public void Queue_Default_Capacity_Matches_The_Design_Contract()
    {
        Assert.AreEqual(8192, new CodeIndexChangeQueue().Capacity);
    }

    [TestMethod]
    public void Capacity_Must_Be_Positive()
    {
        var threw = false;
        try
        {
            _ = new CodeIndexChangeQueue(capacity: 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        Assert.IsTrue(threw);
    }

    [TestMethod]
    public void TryPublish_Rejects_Null_Change()
    {
        var queue = new CodeIndexChangeQueue(4);
        var threw = false;
        try
        {
            queue.TryPublish(null!);
        }
        catch (ArgumentNullException)
        {
            threw = true;
        }

        Assert.IsTrue(threw);
    }

    [TestMethod]
    public void TryRead_Reports_Completion_When_The_Queue_Is_Empty()
    {
        var queue = new CodeIndexChangeQueue(4);

        Assert.IsFalse(queue.TryRead(out var change));
        Assert.IsNull(change);

        queue.Complete();
        Assert.IsFalse(queue.TryPublish(CodeIndexChangeTestFactory.CreateChange(Path.GetTempPath(), "x.cs", IndexChangeKind.Created, 1)));
    }
}
