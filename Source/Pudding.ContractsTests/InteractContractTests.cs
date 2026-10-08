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
    public void EmptyFillIsAllowedButEmptyPressIsNot()
    {
        // 缺口 #13：空文本是 fill/type 的合法输入（清空输入框）；press 的键不能为空。
        var fill = new BrowserInteractRequest(
            Target, DesktopInteractionAction.Fill, DesktopPageVersion.Require(3), Button, text: string.Empty);
        Assert.Equal(string.Empty, fill.Text);

        Assert.Throws<ArgumentException>(() => new BrowserInteractRequest(
            Target, DesktopInteractionAction.Press, DesktopPageVersion.Require(3), Button, text: string.Empty));
    }

    [Fact]
    public void ActionLineNames_AreFrozenAndUnknownIsRejected()
    {
        Assert.Equal(
            ["click", "fill", "type", "press", "check", "uncheck", "select", "hover", "scroll", "drag", "focus"],
            Enum.GetValues<DesktopInteractionAction>().Select(DesktopInteractionActionWire.NameOf).ToArray());

        Assert.True(DesktopInteractionActionWire.TryParse("uncheck", out var uncheck));
        Assert.Equal(DesktopInteractionAction.Uncheck, uncheck);

        Assert.False(DesktopInteractionActionWire.TryParse("drag-and-drop", out _));
        Assert.False(DesktopInteractionActionWire.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DesktopInteractionActionWire.NameOf((DesktopInteractionAction)99));
    }

    [Fact]
    public void InputModeLineNames_AreFrozenAndUnknownIsRejected()
    {
        // 设计 §7：inputMode 必须显式；未登记的线名 fail closed，不猜测。
        Assert.Equal(
            ["dom", "native"],
            Enum.GetValues<DesktopInputMode>().Select(DesktopInputModeWire.NameOf).ToArray());

        Assert.True(DesktopInputModeWire.TryParse("native", out var native));
        Assert.Equal(DesktopInputMode.Native, native);
        Assert.False(DesktopInputModeWire.TryParse("cdp", out _));
        Assert.False(DesktopInputModeWire.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopInputModeWire.NameOf((DesktopInputMode)99));
    }

    [Fact]
    public void Interaction_DefaultsToDomInput_AndRejectsUnregisteredModes()
    {
        var request = new BrowserInteractRequest(
            Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(3), Button);

        // 默认是 DOM 路径：调用方必须显式要求 native（而不是"默认当作 native"）。
        Assert.Equal(DesktopInputMode.Dom, request.InputMode);

        var native = new BrowserInteractRequest(
            Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(3), Button,
            inputMode: DesktopInputMode.Native);
        Assert.Equal(DesktopInputMode.Native, native.InputMode);

        Assert.Throws<ArgumentOutOfRangeException>(() => new BrowserInteractRequest(
            Target, DesktopInteractionAction.Click, DesktopPageVersion.Require(3), Button,
            inputMode: (DesktopInputMode)99));
    }

    [Fact]
    public void ScrollTarget_LineNamesAreFrozen_AndDerivedWhenOmitted()
    {
        // 设计 §7：滚动目标模型是 page/element/to_ref/to_top/to_bottom。
        Assert.Equal(
            ["page", "element", "to_ref", "to_top", "to_bottom"],
            Enum.GetValues<DesktopScrollTarget>().Select(DesktopScrollTargetWire.NameOf).ToArray());

        Assert.True(DesktopScrollTargetWire.TryParse("to_bottom", out var toBottom));
        Assert.Equal(DesktopScrollTarget.ToBottom, toBottom);
        Assert.False(DesktopScrollTargetWire.TryParse("wheel", out _));

        // 省略时按参数推导：无 locator ⇒ page；有 locator 有位移 ⇒ element；有 locator 无位移 ⇒ to_ref。
        Assert.Equal(DesktopScrollTarget.Page, Scroll(locator: null, deltaY: 100).EffectiveScrollTarget);
        Assert.Equal(DesktopScrollTarget.Element, Scroll(locator: Button, deltaY: 100).EffectiveScrollTarget);
        Assert.Equal(DesktopScrollTarget.ToRef, Scroll(locator: Button, deltaY: null).EffectiveScrollTarget);
        Assert.Equal(
            DesktopScrollTarget.ToBottom,
            Scroll(locator: null, deltaY: null, target: DesktopScrollTarget.ToBottom).EffectiveScrollTarget);
    }

    [Fact]
    public void ScrollTarget_ValidatesShapeInsteadOfGuessing()
    {
        // 元素类目标必须给定位。
        Assert.Throws<ArgumentException>(() => Scroll(locator: null, deltaY: null, target: DesktopScrollTarget.ToRef));
        Assert.Throws<ArgumentException>(() => Scroll(locator: null, deltaY: 10, target: DesktopScrollTarget.Element));

        // 明确要求滚文档时不得再给定位（"到底滚哪一个"不可判定 ⇒ 拒绝，而不是静默忽略）。
        Assert.Throws<ArgumentException>(() => Scroll(locator: Button, deltaY: 10, target: DesktopScrollTarget.Page));

        // 位移类目标需要非零位移；"到位"语义（to_ref/to_top/to_bottom）不需要。
        Assert.Throws<ArgumentException>(() => Scroll(locator: null, deltaY: null));
        _ = Scroll(locator: null, deltaY: null, target: DesktopScrollTarget.ToTop);
        _ = Scroll(locator: null, deltaY: null, target: DesktopScrollTarget.ToBottom);

        // 未登记的枚举值 fail closed。
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Scroll(locator: null, deltaY: null, target: (DesktopScrollTarget)99));
    }

    private static BrowserInteractRequest Scroll(
        DesktopLocator? locator,
        double? deltaY,
        DesktopScrollTarget? target = null) =>
        new(
            Target,
            DesktopInteractionAction.Scroll,
            DesktopPageVersion.Require(3),
            locator,
            deltaX: null,
            deltaY: deltaY,
            inputMode: DesktopInputMode.Dom,
            scrollTarget: target);

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
