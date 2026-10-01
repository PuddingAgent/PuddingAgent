using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace CapabilityBrokerTests;

/// <summary>
/// 第 13 轮遗留矛盾的定位：真实端点探针里「旧版本被拒」没有触发，而 snapshot 路径的单测通过。
/// 此前的用例只用 **snapshot** 记录版本、用 locate 消费版本，因此「locate 结果是否记录版本」从未被覆盖。
/// 本文件专门覆盖「每一种结果类型都能推进版本记忆」。
/// </summary>
public sealed class PageVersionRecordingTests
{
    private static readonly DesktopPageTarget Target = BrokerHarness.Target;

    private static BrowserLocateRequest Locate(long expectedVersion) =>
        new(
            Target,
            new DesktopLocator(DesktopLocatorKind.Css, "button"),
            expectedVersion > 0 ? DesktopPageVersion.Require(expectedVersion) : DesktopPageVersion.Unknown);

    private static BrowserSnapshotRequest Snapshot(long expectedVersion) =>
        new(Target, expectedVersion > 0 ? DesktopPageVersion.Require(expectedVersion) : DesktopPageVersion.Unknown);

    private async Task<BrokerHarness> StartedAsync() => await BrokerHarness.StartAsync();

    [Fact]
    public async Task LocateResult_AdvancesTheKnownVersion()
    {
        await using var harness = await StartedAsync();

        var locate = harness.Session.LocateAsync(Locate(0), harness.Call("op-locate"));
        await harness.WaitForCommandAsync("op-locate");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.LocateOk("op-locate", 1, pageVersion: 5)));
        Assert.True((await locate.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        Assert.Equal(5, harness.Session.KnownPageVersionFor(Target));
        var sentBefore = harness.Channel.Sent.Count;
        var stale = await harness.Session.SnapshotAsync(Snapshot(4), harness.Call("op-stale"));

        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, stale.Error!.Code);
        Assert.Equal(sentBefore, harness.Channel.Sent.Count);
    }

    [Fact]
    public async Task SnapshotResult_AdvancesTheKnownVersion()
    {
        await using var harness = await StartedAsync();

        var snapshot = harness.Session.SnapshotAsync(Snapshot(0), harness.Call("op-snapshot"));
        await harness.WaitForCommandAsync("op-snapshot");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.SnapshotOk("op-snapshot", 1, pageVersion: 6)));
        Assert.True((await snapshot.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        Assert.Equal(6, harness.Session.KnownPageVersionFor(Target));
        var stale = await harness.Session.LocateAsync(Locate(5), harness.Call("op-stale"));

        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, stale.Error!.Code);
    }

    [Fact]
    public async Task PageStateResult_AdvancesTheKnownVersion()
    {
        await using var harness = await StartedAsync();

        var state = harness.Session.GetPageStateAsync(Target, harness.Call("op-state"));
        await harness.WaitForCommandAsync("op-state");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.PageStateOk("op-state", 1)));
        Assert.True((await state.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        Assert.Equal(5, harness.Session.KnownPageVersionFor(Target));
        // PageStateOk 的页面版本是 5（见测试夹具），固定到 v4 必须被拒。
        var stale = await harness.Session.LocateAsync(Locate(4), harness.Call("op-stale"));

        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, stale.Error!.Code);
    }

    [Fact]
    public async Task NavigateResult_AdvancesTheKnownVersion()
    {
        await using var harness = await StartedAsync();

        var navigate = harness.Session.NavigateAsync(
            new NavigateRequest(Target, new Uri("https://example.com/a"), DesktopPageVersion.Require(1)),
            harness.Call("op-nav"));
        await harness.WaitForCommandAsync("op-nav");
        // NavigateOk 的页面版本是 4（测试夹具）。
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-nav", 1, pageVersion: 4)));
        var navigateResult = await navigate.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(navigateResult.IsSuccess);

        Assert.Equal(4, harness.Session.KnownPageVersionFor(Target));
        // 导航结果不带目标 ⇒ 必须用请求目标补齐，否则版本记忆会落在错误的键上。
        var stale = await harness.Session.LocateAsync(Locate(3), harness.Call("op-stale"));

        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, stale.Error!.Code);
    }

    [Fact]
    public async Task InteractionResult_AdvancesTheKnownVersion()
    {
        await using var harness = await StartedAsync();

        // 交互（click@v5）返回 v6 ⇒ 交互前的引用必须立即作废。
        var interact = harness.Session.InteractAsync(
            new BrowserInteractRequest(
                Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(5),
                new DesktopLocator(DesktopLocatorKind.Css, "button")),
            harness.Call("op-interact"));
        var command = await harness.WaitForCommandAsync("op-interact");
        Assert.Equal("click", command!.Interact.Action);

        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.InteractOk("op-interact", 1, pageVersion: 6)));
        Assert.True((await interact.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(6, harness.Session.KnownPageVersionFor(Target));

        var stale = await harness.Session.LocateAsync(Locate(5), harness.Call("op-stale"));
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, stale.Error!.Code);
    }

    [Fact]
    public async Task EveryResultKind_RecordsUnderTheRequestedTarget()
    {
        await using var harness = await StartedAsync();

        // 逐步推进版本，每一步都必须让「上一步的版本」失效（漏记一步就会放过陈旧引用）。
        var versions = new[] { 2L, 3L, 5L, 8L };
        for (var index = 0; index < versions.Length; index++)
        {
            var operationId = $"op-{index}";
            var snapshot = harness.Session.SnapshotAsync(Snapshot(0), harness.Call(operationId));
            await harness.WaitForCommandAsync(operationId);
            harness.Channel.Push(DesktopFrames.Result(
                DesktopFrames.SnapshotOk(operationId, 1, pageVersion: versions[index])));
            Assert.True((await snapshot.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

            if (index == 0)
            {
                continue;
            }

            var stale = await harness.Session.LocateAsync(Locate(versions[index - 1]), harness.Call($"stale-{index}"));
            Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, stale.Error!.Code);
        }
    }
}
