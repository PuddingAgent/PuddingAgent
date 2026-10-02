namespace Pudding.Contracts.Desktop;

/// <summary>
/// 交互动作（与既有 Bridge 的 <c>page.interact</c> 动作等价；线名为 kebab-case 真源）。
/// v1 不支持拖拽/键盘组合等复合动作：线缆上无法完整表达其参数，因此不接受。
/// </summary>
public enum DesktopInteractionAction
{
    Click,
    Fill,
    Type,
    Press,
    Check,
    Uncheck,
    Select,
    Hover,
    Scroll,
    Focus,
}

/// <summary>交互动作线名（真源在本文件，快照由契约测试断言）。</summary>
public static class DesktopInteractionActionWire
{
    public static string NameOf(DesktopInteractionAction action) => action switch
    {
        DesktopInteractionAction.Click => "click",
        DesktopInteractionAction.Fill => "fill",
        DesktopInteractionAction.Type => "type",
        DesktopInteractionAction.Press => "press",
        DesktopInteractionAction.Check => "check",
        DesktopInteractionAction.Uncheck => "uncheck",
        DesktopInteractionAction.Select => "select",
        DesktopInteractionAction.Hover => "hover",
        DesktopInteractionAction.Scroll => "scroll",
        DesktopInteractionAction.Focus => "focus",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Interaction action is not registered."),
    };

    /// <summary>严格解析：不知道的动作不猜测、不回退（fail closed）。</summary>
    public static bool TryParse(string? name, out DesktopInteractionAction action)
    {
        foreach (var candidate in Enum.GetValues<DesktopInteractionAction>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                action = candidate;
                return true;
            }
        }

        action = default;
        return false;
    }
}

/// <summary>
/// 交互请求（变更类，切片 D 唯一会改变页面状态的能力）。
///
/// 两条强制不变式（构造期即校验，不靠调用方自觉）：
/// ①<b>必须固定页面版本</b>：交互会改变页面状态，没有版本固定就无法判定「我操作的是哪一版」，
///   因此 <see cref="ExpectedPageVersion"/> 必须有效；
/// ②<b>按动作校验参数</b>：缺参数或多余参数一律拒绝——避免「点了按钮但文案被静默忽略」这类假成功。
/// </summary>
public sealed record BrowserInteractRequest
{
    public BrowserInteractRequest(
        DesktopPageTarget target,
        DesktopInteractionAction action,
        DesktopPageVersion expectedPageVersion,
        DesktopLocator? locator = null,
        string? text = null,
        IReadOnlyList<string>? values = null,
        bool? isChecked = null,
        double? deltaX = null,
        double? deltaY = null)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Interaction action is not registered.");
        }

        if (expectedPageVersion.Value <= 0)
        {
            throw new ArgumentException(
                "Interaction must pin the page version it acts on (mutating capability).",
                nameof(expectedPageVersion));
        }

        Target = target ?? throw new ArgumentNullException(nameof(target));
        Action = action;
        ExpectedPageVersion = expectedPageVersion;
        Locator = locator;
        Text = text;
        Values = values;
        IsChecked = isChecked;
        DeltaX = deltaX;
        DeltaY = deltaY;

        ValidateShape();
    }

    public DesktopPageTarget Target { get; }

    public DesktopInteractionAction Action { get; }

    public DesktopPageVersion ExpectedPageVersion { get; }

    public DesktopLocator? Locator { get; }

    public string? Text { get; }

    public IReadOnlyList<string>? Values { get; }

    public bool? IsChecked { get; }

    public double? DeltaX { get; }

    public double? DeltaY { get; }

    /// <summary>元素作用域动作必须有定位；<c>scroll</c> 可作用于页面本身。</summary>
    public bool RequiresLocator => Action != DesktopInteractionAction.Scroll;

    private void ValidateShape()
    {
        if (RequiresLocator && Locator is null)
        {
            throw new ArgumentException(
                $"Action '{DesktopInteractionActionWire.NameOf(Action)}' requires an element locator.", nameof(Locator));
        }

        switch (Action)
        {
            // fill/type 允许**空文本**（= 清空输入框，缺口 #13）；press 的「键」不能为空。
            case DesktopInteractionAction.Fill or DesktopInteractionAction.Type when Text is null:
                throw new ArgumentException($"Action '{DesktopInteractionActionWire.NameOf(Action)}' requires text.", nameof(Text));

            case DesktopInteractionAction.Press when string.IsNullOrEmpty(Text):
                throw new ArgumentException($"Action '{DesktopInteractionActionWire.NameOf(Action)}' requires text.", nameof(Text));

            case DesktopInteractionAction.Select when Values is null || Values.Count == 0:
                throw new ArgumentException("Action 'select' requires at least one value.", nameof(Values));

            case DesktopInteractionAction.Check when IsChecked != true:
                throw new ArgumentException("Action 'check' requires isChecked=true.", nameof(IsChecked));

            case DesktopInteractionAction.Uncheck when IsChecked != false:
                throw new ArgumentException("Action 'uncheck' requires isChecked=false.", nameof(IsChecked));

            case DesktopInteractionAction.Scroll when (DeltaX ?? 0) == 0 && (DeltaY ?? 0) == 0:
                throw new ArgumentException("Action 'scroll' requires a non-zero delta.", nameof(DeltaX));
        }
    }

    public override string ToString() =>
        $"{DesktopInteractionActionWire.NameOf(Action)}{(Locator is { } locator ? $" {locator}" : string.Empty)}"
        + $" @{Target} v{ExpectedPageVersion.Value}";
}

/// <summary>
/// 交互结果：受影响元素（可选）+ **交互后的页面状态**。
/// 交互会推进页面版本，因此返回的 <see cref="Page"/> 版本通常高于请求版本——
/// 调用方必须用它替换旧引用，不得复用交互前的 Ref。
/// </summary>
public sealed record DesktopInteractionResult
{
    public DesktopInteractionResult(DesktopPageTarget target, DesktopPageState page, DesktopElementRef? element = null)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Page = page ?? throw new ArgumentNullException(nameof(page));
        Element = element;
    }

    public DesktopPageTarget Target { get; }

    public DesktopPageState Page { get; }

    public DesktopElementRef? Element { get; }

    public override string ToString() =>
        $"interact @{Target} → v{Page.Version.Value}{(Element is { } element ? $" ({element})" : string.Empty)}";
}
