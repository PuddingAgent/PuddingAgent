using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// SSE 订阅起点语义（结构性缺陷「实时通道失去时效语义」S4）。
///
/// 锁定三件事：
/// 1. 无游标＝客户端没有权威位置 → 只推实时帧（从 head 起），不再把整份事件日志
///    当实时帧下发；
/// 2. 显式 0＝**有意全量回放**（仅用于刚创建的新会话），仍从 0 回放；
/// 3. 正游标＝按该位置追赶快照之后的事件。
/// </summary>
[TestClass]
public sealed class SessionEventStreamStartTests
{
    [TestMethod]
    public void Resolve_IsLiveOnly_WhenCursorIsAbsent()
    {
        var start = SessionEventStreamStart.Resolve(explicitCursor: null, head: 1200);

        Assert.AreEqual(1200L, start.After, "无游标必须从 head 开始，避免伪实时历史回放。");
        Assert.AreEqual(SessionEventStreamStart.LiveOnlyPhase, start.Phase);
    }

    [TestMethod]
    public void Resolve_IsLiveOnly_WhenLogIsEmpty()
    {
        var start = SessionEventStreamStart.Resolve(explicitCursor: null, head: 0);

        Assert.AreEqual(0L, start.After);
        Assert.AreEqual(SessionEventStreamStart.LiveOnlyPhase, start.Phase);
    }

    [TestMethod]
    public void Resolve_ReplaysFromZero_WhenCursorIsExplicitlyZero()
    {
        var start = SessionEventStreamStart.Resolve(explicitCursor: 0, head: 1200);

        Assert.AreEqual(0L, start.After, "显式 0 是有意的全量回放意图，必须区别于无游标。");
        Assert.AreEqual(SessionEventStreamStart.ReplayFromZeroPhase, start.Phase);
    }

    [TestMethod]
    public void Resolve_ReplaysAfterCursor_WhenCursorIsPositive()
    {
        var start = SessionEventStreamStart.Resolve(explicitCursor: 9865, head: 12_000);

        Assert.AreEqual(9865L, start.After);
        Assert.AreEqual(SessionEventStreamStart.ReplayAfterPhase, start.Phase);
    }

    [TestMethod]
    public void Resolve_ClampsNegativeCursorToZero()
    {
        var start = SessionEventStreamStart.Resolve(explicitCursor: -5, head: 1200);

        Assert.AreEqual(0L, start.After, "负数游标不得被下传为负起点。");
        Assert.AreEqual(SessionEventStreamStart.ReplayFromZeroPhase, start.Phase);
    }

    [TestMethod]
    public void Resolve_KeepsExplicitCursorAboveHead()
    {
        // 客户端游标领先（例如本地已应用 head 之后的帧）时不得回退到 head，
        // 否则会重复回放已应用的事件。
        var start = SessionEventStreamStart.Resolve(explicitCursor: 1500, head: 1200);

        Assert.AreEqual(1500L, start.After);
        Assert.AreEqual(SessionEventStreamStart.ReplayAfterPhase, start.Phase);
    }
}
