using PuddingBrowser.AgentTools;
using Xunit;

namespace PuddingBrowser.AgentToolsTests;

/// <summary>
/// 指针动作的参数形状判定：<c>pointer_click</c>/<c>pointer_move</c> 给了 locator 就翻成
/// 元素动作（click/hover），没给就保持纯坐标。这条改写错了不会报错、只会点错地方，
/// 所以每条分支都钉死。
/// </summary>
public sealed class BrowserInteractArgumentPolicyTests
{
    [Fact]
    public void PointerClickWithLocatorBecomesAnElementClick()
    {
        var (action, error) = BrowserInteractArgumentPolicy.ResolvePointer(
            "pointer_click", hasLocator: true, hasPoint: false, hasDragTarget: false);

        Assert.Null(error);
        Assert.Equal("click", action);
    }

    [Fact]
    public void PointerMoveWithLocatorBecomesHover()
    {
        var (action, error) = BrowserInteractArgumentPolicy.ResolvePointer(
            "pointer_move", hasLocator: true, hasPoint: false, hasDragTarget: false);

        Assert.Null(error);
        Assert.Equal("hover", action);
    }

    [Fact]
    public void LocatorAndCoordinatesTogetherAreRejected()
    {
        var (_, error) = BrowserInteractArgumentPolicy.ResolvePointer(
            "pointer_click", hasLocator: true, hasPoint: true, hasDragTarget: false);

        Assert.NotNull(error);
        Assert.Contains("either locator or x/y", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerClickWithCoordinatesKeepsTheRawCoordinateAction()
    {
        var (action, error) = BrowserInteractArgumentPolicy.ResolvePointer(
            "pointer_click", hasLocator: false, hasPoint: true, hasDragTarget: false);

        Assert.Null(error);
        Assert.Equal("pointer_click", action);
    }

    [Fact]
    public void PointerActionWithoutLocatorOrCoordinatesIsRejected()
    {
        var (_, error) = BrowserInteractArgumentPolicy.ResolvePointer(
            "pointer_click", hasLocator: false, hasPoint: false, hasDragTarget: false);

        Assert.NotNull(error);
        Assert.Contains("require both x and y", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerDragWithBothEndpointsIsAccepted()
    {
        var (action, error) = BrowserInteractArgumentPolicy.ResolvePointer(
            "pointer_drag", hasLocator: false, hasPoint: true, hasDragTarget: true);

        Assert.Null(error);
        Assert.Equal("pointer_drag", action);
    }

    [Fact]
    public void PointerDragWithoutTargetIsRejected()
    {
        var (_, error) = BrowserInteractArgumentPolicy.ResolvePointer(
            "pointer_drag", hasLocator: false, hasPoint: true, hasDragTarget: false);

        Assert.NotNull(error);
        Assert.Contains("to_x and to_y", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerDragWithLocatorPointsAtTheElementDragAction()
    {
        var (_, error) = BrowserInteractArgumentPolicy.ResolvePointer(
            "pointer_drag", hasLocator: true, hasPoint: true, hasDragTarget: true);

        Assert.NotNull(error);
        Assert.Contains("action=drag", error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("fill")]
    [InlineData("type")]
    [InlineData("press")]
    [InlineData("hover")]
    [InlineData("scroll")]
    public void NonPointerActionsArePassedThroughUntouched(string action)
    {
        var (resolved, error) = BrowserInteractArgumentPolicy.ResolvePointer(
            action, hasLocator: true, hasPoint: true, hasDragTarget: true);

        Assert.Null(error);
        Assert.Equal(action, resolved);
    }
}
