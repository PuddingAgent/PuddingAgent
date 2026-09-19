using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// ADR-057 snapshot_required 判据（结构性缺陷「实时通道失去时效语义」S4 剩余项）。
///
/// 锁定「缺口」语义，而不是「低于最小可用序号」：
/// 显式游标 0 与 min=1 之间并无缺失（序号从 1 起），不得被判为需要快照；
/// 只有日志确实被裁剪过（min &gt; 1）时才要求客户端退回快照。
/// </summary>
[TestClass]
public sealed class SnapshotRequiredCheckTests
{
    [TestMethod]
    public void ExplicitZeroCursor_WithFullLog_DoesNotRequireSnapshot()
    {
        // 这是本次修复的核心回归：以前判定 cursor &lt; min 会把每一次
        // 合法的「显式 0 全量回放」都打成 410。
        Assert.IsFalse(
            SnapshotRequiredCheck.HasMissingEvents(cursor: 0, minAvailableSequence: 1),
            "cursor=0 与 min=1 之间没有缺失事件，显式 0 是有意的全量回放。");
    }

    [TestMethod]
    public void ExplicitZeroCursor_WithTrimmedLog_RequiresSnapshot()
    {
        // 日志已裁剪（最小可用 51 表示 1..50 已不存在）时，要「从头」的客户端
        // 拿不到完整历史，必须退回快照。
        Assert.IsTrue(
            SnapshotRequiredCheck.HasMissingEvents(cursor: 0, minAvailableSequence: 51),
            "tail 之前的序号已不可读，cursor=0 必须被要求重取快照。");
    }

    [TestMethod]
    public void CursorImmediatelyBeforeMin_DoesNotRequireSnapshot()
    {
        // 旧判据的假阳性：游标恰为 min-1，客户端已持有 min 之前的全部事件，
        // 150→151 是连续的，不需要退回快照。
        Assert.IsFalse(
            SnapshotRequiredCheck.HasMissingEvents(cursor: 50, minAvailableSequence: 51));
    }

    [TestMethod]
    public void CursorWithGapBeforeMin_RequiresSnapshot()
    {
        Assert.IsTrue(
            SnapshotRequiredCheck.HasMissingEvents(cursor: 49, minAvailableSequence: 51),
            "序号 50 已缺失。");
    }

    [TestMethod]
    public void ReconnectingCursorBelowMin_RequiresSnapshot()
    {
        Assert.IsTrue(
            SnapshotRequiredCheck.HasMissingEvents(cursor: 137, minAvailableSequence: 140));
    }

    [TestMethod]
    public void CursorAtOrAboveMin_DoesNotRequireSnapshot()
    {
        Assert.IsFalse(
            SnapshotRequiredCheck.HasMissingEvents(cursor: 140, minAvailableSequence: 140));
        Assert.IsFalse(
            SnapshotRequiredCheck.HasMissingEvents(cursor: 9865, minAvailableSequence: 140));
    }

    [TestMethod]
    public void EmptyLog_DoesNotRequireSnapshot()
    {
        // 会话尚无任何事件：没有可缺失的东西，不得把「空」当成「需要快照」。
        Assert.IsFalse(
            SnapshotRequiredCheck.HasMissingEvents(cursor: 0, minAvailableSequence: null));
    }
}
