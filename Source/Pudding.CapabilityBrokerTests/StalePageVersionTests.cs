using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Audit;
using Pudding.Contracts.Desktop;

namespace CapabilityBrokerTests;

/// <summary>
/// 「交互提交后旧 Ref 作废」的 Core 侧强制：本会话已经观测到更新的版本时，
/// 固定到旧版本的请求<b>在发出命令之前</b>就被拒绝（page_version_mismatch，无副作用）。
/// </summary>
public sealed class StalePageVersionTests
{
    private static readonly DesktopPageTarget Target = BrokerHarness.Target;

    private static BrowserSnapshotRequest Snapshot(long expectedVersion) =>
        new(Target, expectedVersion > 0 ? DesktopPageVersion.Require(expectedVersion) : DesktopPageVersion.Unknown);

    private static BrowserLocateRequest Locate(long expectedVersion) =>
        new(
            Target,
            new DesktopLocator(DesktopLocatorKind.Css, "button"),
            expectedVersion > 0 ? DesktopPageVersion.Require(expectedVersion) : DesktopPageVersion.Unknown);

    [Fact]
    public async Task RequestPinnedToAnOlderVersion_IsRejectedBeforeSending()
    {
        await using var harness = await BrokerHarness.StartAsync();

        // 先观测到该目标已到 v7（result 里的版本被记下）。
        var first = harness.Session.SnapshotAsync(Snapshot(7), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.SnapshotOk("op-1", 1, pageVersion: 7)));
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        var sentBefore = harness.Channel.Sent.Count;
        var stale = await harness.Session.SnapshotAsync(Snapshot(5), harness.Call("op-2"));

        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, stale.Error!.Code);
        Assert.False(stale.Error.MayHaveSideEffects);
        Assert.True(stale.Error.Retryable);
        // 关键：命令没有发出去，Desktop 不会基于陈旧引用做任何事。
        Assert.Equal(sentBefore, harness.Channel.Sent.Count);

        var audit = Assert.Single(harness.Audit.ForOperation(new OperationId("op-2")));
        Assert.Equal(DesktopCapabilityOutcome.Rejected, audit.Outcome);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, audit.ErrorCode);
    }

    [Fact]
    public async Task RequestPinnedToTheCurrentOrNewerVersion_IsSentNormally()
    {
        await using var harness = await BrokerHarness.StartAsync();

        var first = harness.Session.SnapshotAsync(Snapshot(7), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.SnapshotOk("op-1", 1, pageVersion: 7)));
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        // 同版本：允许（调用方拿的就是当前版本的引用）。
        var current = harness.Session.SnapshotAsync(Snapshot(7), harness.Call("op-2"));
        var command = await harness.WaitForCommandAsync("op-2");
        Assert.Equal(7, command!.Snapshot.ExpectedPageVersion);
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.SnapshotOk("op-2", 1, pageVersion: 7)));
        Assert.True((await current.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        // 更新的版本：也允许（Desktop 会自行判定，Core 不做乐观猜测）。
        var newer = harness.Session.SnapshotAsync(Snapshot(9), harness.Call("op-3"));
        Assert.NotNull(await harness.WaitForCommandAsync("op-3"));
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.SnapshotOk("op-3", 1, pageVersion: 9)));
        Assert.True((await newer.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task UnknownVersionIsNeverTreatedAsStale()
    {
        await using var harness = await BrokerHarness.StartAsync();

        var first = harness.Session.SnapshotAsync(Snapshot(7), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.SnapshotOk("op-1", 1, pageVersion: 7)));
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        // 不要求版本约束 ≠ 旧版本：不得据此拒绝。
        var unconstrained = harness.Session.SnapshotAsync(Snapshot(0), harness.Call("op-2"));
        Assert.NotNull(await harness.WaitForCommandAsync("op-2"));
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.SnapshotOk("op-2", 1, pageVersion: 7)));
        Assert.True((await unconstrained.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task VersionTrackingIsPerTarget()
    {
        await using var harness = await BrokerHarness.StartAsync();
        var other = new DesktopPageTarget("ctx-other", "page-other");

        var first = harness.Session.SnapshotAsync(Snapshot(7), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.SnapshotOk("op-1", 1, pageVersion: 7)));
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        // 另一个目标从未被观测过：即使版本号更小也不得判为过期。
        var otherRequest = new BrowserLocateRequest(
            other, new DesktopLocator(DesktopLocatorKind.Css, "button"), DesktopPageVersion.Require(3));
        var locate = harness.Session.LocateAsync(otherRequest, harness.Call("op-2"));

        Assert.NotNull(await harness.WaitForCommandAsync("op-2"));
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.LocateOk("op-2", 1, pageVersion: 3)));
        Assert.True((await locate.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task FailedResults_DoNotAdvanceTheKnownVersion()
    {
        await using var harness = await BrokerHarness.StartAsync();

        // 失败结果里的版本不可信（可能是错误载荷），不得据此把引用判废。
        var failed = harness.Session.SnapshotAsync(Snapshot(3), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.Error("op-1", 1, "page_version_mismatch")));
        Assert.True((await failed.WaitAsync(TimeSpan.FromSeconds(5))).IsFailure);

        var next = harness.Session.SnapshotAsync(Snapshot(3), harness.Call("op-2"));
        Assert.NotNull(await harness.WaitForCommandAsync("op-2"));
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.SnapshotOk("op-2", 1, pageVersion: 3)));
        Assert.True((await next.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }
}
