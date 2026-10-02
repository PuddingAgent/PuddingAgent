using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 交互契约：动作线名冻结、**必须固定页面版本**、按动作校验参数、联合暴露目标与版本
/// （最后一条是第 14 轮回归——联合漏项会让 Core 侧的版本保护静默失效）。
/// </summary>
public sealed class InteractContractTests
{
    private static readonly DesktopPageTarget Target = new("ctx-1", "page-1");

    private static DesktopLocator Button => new(DesktopLocatorKind.Css, "button");

    [Fact]
    public void ActionLineNames_AreFrozenAndUnknownIsRejected()
    {
        Assert.Equal(
            ["click", "fill", "type", "press", "check", "uncheck", "select", "hover", "scroll", "focus"],
            Enum.GetValues<DesktopInteractionAction>().Select(DesktopInteractionActionWire.NameOf).ToArray());

        Assert.True(DesktopInteractionActionWire.TryParse("uncheck", out var uncheck));
        Assert.Equal(DesktopInteractionAction.Uncheck, uncheck);

        Assert.False(DesktopInteractionActionWire.TryParse("drag-and-drop", out _));
        Assert.False(DesktopInteractionActionWire.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DesktopInteractionActionWire.NameOf((DesktopInteractionAction)99));
    }

    [Fact]
    public void Interaction_MustPinThePageVersion()
    {
        // 变更类能力没有版本固定就无法判定「操作的是哪一版」⇒ 构造期拒绝。
        Assert.Throws<ArgumentException>(() => new BrowserInteractRequest(
            Target, DesktopInteractionAction.Click, DesktopPageVersion.Unknown, Button));
        // Require(0) 本身即非法版本（ArgumentOutOfRange），未固定版本则被契约拒绝（Argument）。

        var pinned = new BrowserInteractRequest(
            Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(4), Button);
        Assert.Equal(4, pinned.ExpectedPageVersion.Value);
        Assert.True(pinned.RequiresLocator);
    }

    [Fact]
    public void Interaction_ValidatesParametersPerAction()
    {
        var version = DesktopPageVersion.Require(4);

        // 元素作用域动作必须有定位（scroll 例外）。
        Assert.Throws<ArgumentException>(() => new BrowserInteractRequest(
            Target, DesktopInteractionAction.Click, version));
        _ = new BrowserInteractRequest(
            Target, DesktopInteractionAction.Scroll, version, deltaY: -200);

        // 缺参数或多参数一律拒绝：避免「点了按钮但文案被静默忽略」这类假成功。
        Assert.Throws<ArgumentException>(() => new BrowserInteractRequest(
            Target, DesktopInteractionAction.Fill, version, Button));
        Assert.Throws<ArgumentException>(() => new BrowserInteractRequest(
            Target, DesktopInteractionAction.Select, version, Button));
        Assert.Throws<ArgumentException>(() => new BrowserInteractRequest(
            Target, DesktopInteractionAction.Check, version, Button));
        Assert.Throws<ArgumentException>(() => new BrowserInteractRequest(
            Target, DesktopInteractionAction.Uncheck, version, Button, isChecked: true));
        Assert.Throws<ArgumentException>(() => new BrowserInteractRequest(
            Target, DesktopInteractionAction.Scroll, version));

        _ = new BrowserInteractRequest(Target, DesktopInteractionAction.Fill, version, Button, text: "你好");
        _ = new BrowserInteractRequest(Target, DesktopInteractionAction.Select, version, Button, values: ["a"]);
        _ = new BrowserInteractRequest(Target, DesktopInteractionAction.Check, version, Button, isChecked: true);
        _ = new BrowserInteractRequest(Target, DesktopInteractionAction.Uncheck, version, Button, isChecked: false);
    }

    [Fact]
    public void RequestUnion_ExposesTheInteractionTargetAndVersion()
    {
        var request = DesktopCapabilityRequest.ForInteract(new BrowserInteractRequest(
            Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(6), Button));

        // 回归（第 14 轮）：联合若漏掉 Interact，Core 侧的「旧版本引用作废」保护会对交互静默失效。
        Assert.Equal(Target, request.Target);
        Assert.Equal(6, request.ExpectedPageVersion.Value);
        Assert.False(request.ShellStatus);
        Assert.Contains("interact", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResponseUnion_CarriesTheInteractionResult()
    {
        var state = new DesktopPageState(
            Target,
            new Uri("https://example.com/after"),
            DesktopPageVersion.Require(7),
            DesktopPageReadiness.Complete);
        var response = DesktopCapabilityResponse.FromInteract(new DesktopInteractionResult(Target, state));

        Assert.False(response.IsFailure);
        Assert.Equal(7, response.Interact!.Page.Version.Value);
        Assert.Contains("interact", response.ToString(), StringComparison.Ordinal);

        // 恰好一个分支非空：另一个分支必须为 null。
        Assert.Null(response.Locate);
        Assert.Null(response.Snapshot);
    }
}
