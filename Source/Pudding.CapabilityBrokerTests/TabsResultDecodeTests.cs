using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Xunit;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.CapabilityBrokerTests;

/// <summary>
/// 标签页结果**解码侧**证据：<c>new</c> 是唯一「请求里没有目标」的动作
/// （页面由该操作创建），因此目标只能来自结果本身。
///
/// 实测背景（2026-10-26）：Desktop 侧确实建出了标签页（第二次 list 能看到抖音页），
/// 但 Core 侧解码一律要求 <c>requestedTarget</c>，于是把一次**成功**的操作报成
/// 「tabs result cannot be correlated without the requested target」。
/// 这里锁住三件事：new 可以从结果取目标、缺目标仍失败关闭、其余动作不放宽。
/// </summary>
public sealed class TabsResultDecodeTests
{
    private const string ContextId = "ctx-tabs-1";
    private const string PageId = "page-tabs-1";

    private static Proto.OperationResult Wire(
        string action,
        string contextId,
        string pageId,
        string url = "https://www.douyin.com/",
        long pageVersion = 2)
    {
        return new Proto.OperationResult
        {
            OperationId = "op-tabs-1",
            Generation = 1,
            Tabs = new Proto.TabsOutcome
            {
                Action = action,
                TabClosed = false,
                ContextId = contextId,
                PageId = pageId,
                Page = new Proto.PageStateOutcome
                {
                    Url = url,
                    PageVersion = pageVersion,
                    Readiness = "complete",
                    Title = "抖音-记录美好生活",
                },
            },
        };
    }

    [Fact]
    public void CreatingATabTakesItsTargetFromTheResult()
    {
        var decoded = DesktopResultDecoder.Decode(
            Wire("new", ContextId, PageId), DesktopCapability.BrowserTabs, requestedTarget: null);

        Assert.True(decoded.IsSuccess, decoded.IsFailure ? decoded.Error!.Message : null);

        var tabs = decoded.Value.Tabs!;
        Assert.Equal(DesktopTabAction.New, tabs.Action);
        Assert.Equal(ContextId, tabs.Target.ContextId);
        Assert.Equal(PageId, tabs.Target.PageId);

        // 页状态必须挂在同一个目标上——否则工具层拿到的 pageId 会是空的。
        Assert.Equal(ContextId, tabs.Page.Target.ContextId);
        Assert.Equal(PageId, tabs.Page.Target.PageId);
        Assert.Equal(2, tabs.Page.Version.Value);
        Assert.Equal("抖音-记录美好生活", tabs.Page.Title);
    }

    [Fact]
    public void CreatingATabWithoutACreatedTargetIsRefused()
    {
        var decoded = DesktopResultDecoder.Decode(
            Wire("new", contextId: string.Empty, pageId: string.Empty),
            DesktopCapability.BrowserTabs,
            requestedTarget: null);

        Assert.True(decoded.IsFailure);
        Assert.Contains("no created page target", decoded.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnActionOtherThanCreateStillNeedsTheRequestedTarget()
    {
        var decoded = DesktopResultDecoder.Decode(
            Wire("activate", ContextId, PageId), DesktopCapability.BrowserTabs, requestedTarget: null);

        Assert.True(decoded.IsFailure);
        Assert.Contains("cannot be correlated", decoded.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRequestedTargetStillWinsWhenItIsKnown()
    {
        // 回归保护：给了请求目标就走原路，不被结果里回带的目标覆盖。
        var requested = new DesktopPageTarget(ContextId, "page-from-request");
        var decoded = DesktopResultDecoder.Decode(
            Wire("activate", ContextId, PageId), DesktopCapability.BrowserTabs, requested);

        Assert.True(decoded.IsSuccess, decoded.IsFailure ? decoded.Error!.Message : null);
        Assert.Equal("page-from-request", decoded.Value.Tabs!.Target.PageId);
    }
}
