using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 标签页契约（切片 D 收尾）：动作线名冻结、**必须固定页面版本**、结果带剩余清单。
/// 本轮只落契约与准入；wire payload 与服务分支属下一步（因此它当前不在可授予能力集合里）。
/// </summary>
public sealed class TabsContractTests
{
    private static readonly DesktopPageTarget Target = new("ctx-1", "page-1");

    [Fact]
    public void TabActionLineNames_AreFrozenAndUnknownIsRejected()
    {
        Assert.Equal(
            ["new", "activate", "close"],
            Enum.GetValues<DesktopTabAction>().Select(DesktopTabActionWire.NameOf).ToArray());

        Assert.True(DesktopTabActionWire.TryParse("close", out var close));
        Assert.Equal(DesktopTabAction.Close, close);
        Assert.False(DesktopTabActionWire.TryParse("duplicate", out _));
        Assert.False(DesktopTabActionWire.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopTabActionWire.NameOf((DesktopTabAction)99));
    }

    [Fact]
    public void TabOperation_MustPinThePageVersion()
    {
        // 版本不符说明目标页在等待期间已变化，此时切换/关闭的可能是另一个页面。
        Assert.Throws<ArgumentException>(() => new BrowserTabsRequest(
            Target, DesktopTabAction.Activate, DesktopPageVersion.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrowserTabsRequest(
            Target, (DesktopTabAction)99, DesktopPageVersion.Require(4)));

        var activate = new BrowserTabsRequest(Target, DesktopTabAction.Activate, DesktopPageVersion.Require(4));
        Assert.False(activate.IsDestructive);
        Assert.Equal("activate @ctx-1/page-1 v4", activate.ToString());

        // 关闭是破坏性操作：调用方与审计据此区别对待。
        var close = new BrowserTabsRequest(Target, DesktopTabAction.Close, DesktopPageVersion.Require(4));
        Assert.True(close.IsDestructive);
    }

    [Fact]
    public void TabsResult_CarriesTheActivePageAndTheRemainingList()
    {
        var state = new DesktopPageState(
            Target, new Uri("https://example.com/next"), DesktopPageVersion.Require(9), DesktopPageReadiness.Complete);
        var remaining = new DesktopContexts(
        [
            new DesktopContextInfo("ctx-1", DesktopContextTrust.AgentAuthorized,
            [
                new DesktopPageInfo(Target, DesktopPageVersion.Require(9), title: "next", isActive: true),
            ]),
        ]);

        var closed = new DesktopTabsResult(Target, DesktopTabAction.Close, state, tabClosed: true, remaining);

        Assert.True(closed.TabClosed);
        Assert.Equal(1, closed.Remaining.PageCount);
        Assert.Equal(9, closed.Remaining.Contexts[0].Pages[0].Version.Value);
        Assert.Contains("closed", closed.ToString(), StringComparison.Ordinal);

        // 关闭动作不一定真的关掉（例如页面拒绝关闭）：必须如实区分。
        var notClosed = new DesktopTabsResult(Target, DesktopTabAction.Close, state, tabClosed: false, remaining);
        Assert.False(notClosed.TabClosed);
    }
}
