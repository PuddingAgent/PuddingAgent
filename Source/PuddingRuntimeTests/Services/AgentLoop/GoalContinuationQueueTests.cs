using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 续跑队列：决策说「继续」之后，请求要有一个**能被宿主取走**的地方。
/// <para>
/// 这里只测生产者语义（入队/去重/取出/清除）。消费方尚未接线，
/// 所以刻意不写"它会自己续跑"这类测不出来的断言。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalContinuationQueueTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 22, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Enqueue_then_take_returns_the_request_once()
    {
        var queue = new GoalContinuationQueue();

        queue.Enqueue("s1", 3, GoalLoopDecider.ReasonProgress, Now);
        var taken = queue.TryTake("s1");

        Assert.IsNotNull(taken);
        Assert.AreEqual(3, taken!.Round);
        Assert.AreEqual(GoalLoopDecider.ReasonProgress, taken.ReasonCode);
        Assert.IsNull(queue.TryTake("s1"), "取出后不能再次取出");
        Assert.AreEqual(0, queue.Count);
    }

    [TestMethod]
    public void Same_session_is_deduplicated_and_keeps_latest()
    {
        var queue = new GoalContinuationQueue();

        queue.Enqueue("s1", 3, GoalLoopDecider.ReasonProgress, Now);
        queue.Enqueue("s1", 4, GoalLoopDecider.ReasonProgress, Now.AddMinutes(1));

        Assert.AreEqual(1, queue.Count, "同一会话只保留一条");
        Assert.AreEqual(4, queue.TryTake("s1")!.Round, "保留最新轮号");
    }

    [TestMethod]
    public void Sessions_are_isolated()
    {
        var queue = new GoalContinuationQueue();
        queue.Enqueue("s1", 1, GoalLoopDecider.ReasonProgress, Now);
        queue.Enqueue("s2", 2, GoalLoopDecider.ReasonProgress, Now);

        Assert.AreEqual(2, queue.Count);
        Assert.AreEqual(1, queue.TryTake("s1")!.Round);
        Assert.AreEqual(2, queue.TryTake("s2")!.Round);
    }

    [TestMethod]
    public void Clear_drops_the_pending_request()
    {
        var queue = new GoalContinuationQueue();
        queue.Enqueue("s1", 1, GoalLoopDecider.ReasonProgress, Now);

        Assert.IsTrue(queue.Clear("s1"));
        Assert.IsNull(queue.TryTake("s1"));
        Assert.IsFalse(queue.Clear("s1"), "重复清除返回 false 而不是抛错");
    }

    [TestMethod]
    public void Take_from_unknown_session_is_null()
    {
        var queue = new GoalContinuationQueue();

        Assert.IsNull(queue.TryTake("nope"));
        Assert.IsNull(queue.TryTake("   "));
    }

    [TestMethod]
    public void Invalid_input_is_rejected()
    {
        var queue = new GoalContinuationQueue();

        Assert.ThrowsExactly<ArgumentException>(() => queue.Enqueue("  ", 1, "r", Now));
        Assert.ThrowsExactly<ArgumentException>(() => queue.Enqueue("s1", 1, "  ", Now));
    }

    [TestMethod]
    public void Snapshot_exposes_pending_without_consuming()
    {
        var queue = new GoalContinuationQueue();
        queue.Enqueue("s1", 5, GoalLoopDecider.ReasonProgress, Now);

        var snapshot = queue.Snapshot();

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreEqual(1, queue.Count, "快照不应消费请求");
    }
}
