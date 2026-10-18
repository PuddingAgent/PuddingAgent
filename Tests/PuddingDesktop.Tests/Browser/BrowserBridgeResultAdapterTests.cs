using Pudding.Contracts.Desktop;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// I2-c 结果反投影第一片：执行器结果 → Bridge 线缆描述符。
/// 只覆盖发现读命令；字段缺口（UserDataDirectory 等）在适配器注释与日志里如实登记。
/// </summary>
public sealed class BrowserBridgeResultAdapterTests
{
    private static readonly DesktopPageTarget Target = new("ctx", "page");

    [Fact]
    public void PageState_MapsToThePageDescriptor()
    {
        var descriptor = BrowserBridgeResultAdapter.FromPageState(new DesktopPageState(
            Target,
            new Uri("https://example.com/a"),
            DesktopPageVersion.Require(7),
            DesktopPageReadiness.Complete,
            "Example"));

        Assert.Equal("ctx", descriptor.ContextId);
        Assert.Equal("page", descriptor.PageId);
        Assert.Equal("Example", descriptor.Title);
        Assert.Equal("https://example.com/a", descriptor.Url);
        Assert.Equal(7, descriptor.PageVersion);
        Assert.False(descriptor.IsActive);
        Assert.False(descriptor.IsLoading);
    }

    [Fact]
    public void PageInfo_MapsTabsFlags()
    {
        var descriptor = BrowserBridgeResultAdapter.FromPageInfo(new DesktopPageInfo(
            Target,
            DesktopPageVersion.Require(5),
            title: "Tab",
            url: new Uri("https://example.com/tab"),
            isActive: true,
            isAgentTarget: true,
            canGoBack: true,
            canGoForward: false,
            isLoading: false));

        Assert.Equal("Tab", descriptor.Title);
        Assert.True(descriptor.IsActive);
        Assert.True(descriptor.IsAgentTarget);
        Assert.True(descriptor.CanGoBack);
        Assert.False(descriptor.CanGoForward);
    }

    [Fact]
    public void Contexts_MapToTheContextListButTheUserDataDirectoryIsUnknownOnTheExecutorContract()
    {
        var contexts = new DesktopContexts(
        [
            new DesktopContextInfo(
                "ctx",
                DesktopContextTrust.AgentAuthorized,
                [new DesktopPageInfo(Target, DesktopPageVersion.Require(3), title: "T")])
            {
                Persistent = true,
            },
        ]);

        var descriptor = BrowserBridgeResultAdapter.FromContexts(contexts);

        var context = Assert.Single(descriptor.Contexts);
        Assert.Equal("ctx", context.ContextId);
        Assert.Equal(1, context.PageCount);
        // 已知缺口：执行器契约没有目录字段，不伪造路径。
        Assert.Equal(string.Empty, context.UserDataDirectory);
    }

    [Fact]
    public void PageList_FlattensAllContextPages()
    {
        var contexts = new DesktopContexts(
        [
            new DesktopContextInfo(
                "ctx-a",
                DesktopContextTrust.AgentAuthorized,
                [
                    new DesktopPageInfo(new DesktopPageTarget("ctx-a", "p1"), DesktopPageVersion.Require(1)),
                    new DesktopPageInfo(new DesktopPageTarget("ctx-a", "p2"), DesktopPageVersion.Require(2)),
                ]),
            new DesktopContextInfo(
                "ctx-b",
                DesktopContextTrust.Untrusted,
                [new DesktopPageInfo(new DesktopPageTarget("ctx-b", "p3"), DesktopPageVersion.Require(3))]),
        ]);

        var descriptor = BrowserBridgeResultAdapter.FromPageList(contexts);

        Assert.Equal(3, descriptor.Pages.Count);
        Assert.Equal(["p1", "p2", "p3"], descriptor.Pages.Select(page => page.PageId));
        Assert.Equal(["ctx-a", "ctx-a", "ctx-b"], descriptor.Pages.Select(page => page.ContextId));
    }

    [Fact]
    public void Locate_MapsElementsAndKeepsTheBoundingBoxGapVisible()
    {
        var result = new DesktopLocateResult(
            Target,
            new DesktopLocator(DesktopLocatorKind.Css, "#go"),
            [
                new DesktopElementRef(
                    "v5-n1",
                    "button",
                    DesktopPageVersion.Require(5),
                    role: "button",
                    name: "Go",
                    text: "Go",
                    enabled: true,
                    isChecked: false),
            ],
            truncated: true,
            pageVersion: DesktopPageVersion.Require(5));

        var descriptor = BrowserBridgeResultAdapter.FromLocate(result);

        Assert.True(descriptor.Truncated);
        var element = Assert.Single(descriptor.Elements);
        Assert.Equal("v5-n1", element.Ref);
        Assert.Equal("button", element.Tag);
        Assert.Equal("button", element.Role);
        Assert.Equal("Go", element.Name);
        Assert.Equal("Go", element.Text);
        Assert.True(element.Visible);
        Assert.True(element.Enabled);
        Assert.False(element.Checked);
        Assert.Equal(5, element.PageVersion);
        // 已知缺口：契约没有 bbox，线缆保持 null（不伪造坐标）。
        Assert.Null(element.BoundingBox);
    }
}
