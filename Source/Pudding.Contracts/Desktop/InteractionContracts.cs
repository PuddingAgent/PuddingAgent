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

    /// <summary>拖拽：从 <c>Locator</c> 拖到 <c>ToLocator</c>（两者都必须给）。</summary>
    Drag,
    Focus,
}

/// <summary>
/// 交互输入路径（设计方案 §7）：「驱动增加明确 <c>inputMode=dom/native</c>，面向用户网页优先使用
/// 真实浏览器输入路径，DOM 模拟用于已验证适用的动作；<b>native 不可用时明确 unsupported，
/// 不静默切换语义</b>」。
/// </summary>
public enum DesktopInputMode
{
    /// <summary>DOM 事件/属性写入。语义确定但只在已验证适用的动作上使用。</summary>
    Dom,

    /// <summary>真实浏览器输入（CDP 键鼠）。本切片尚未实现 ⇒ 请求它必须**明确失败**，不降级成 Dom。</summary>
    Native,
}

/// <summary>输入路径线名（真源在本文件）。</summary>
public static class DesktopInputModeWire
{
    public static string NameOf(DesktopInputMode mode) => mode switch
    {
        DesktopInputMode.Dom => "dom",
        DesktopInputMode.Native => "native",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Input mode is not registered."),
    };

    /// <summary>严格解析：不知道的线名不猜测、不回退（fail closed）。</summary>
    public static bool TryParse(string? name, out DesktopInputMode mode)
    {
        foreach (var candidate in Enum.GetValues<DesktopInputMode>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                mode = candidate;
                return true;
            }
        }

        mode = default;
        return false;
    }
}

/// <summary>
/// 滚动目标模型（设计方案 §7）：<c>page/element/to_ref/to_top/to_bottom</c>。
/// 「无 locator 时检查 <c>document.scrollingElement</c>；元素滚动优先明确指定，
/// 必要时定位最近可滚动祖先」——**不盲目滚另一个区域**。
/// </summary>
public enum DesktopScrollTarget
{
    Page,
    Element,
    ToRef,
    ToTop,
    ToBottom,
}

/// <summary>滚动目标线名（真源在本文件）。</summary>
public static class DesktopScrollTargetWire
{
    public static string NameOf(DesktopScrollTarget target) => target switch
    {
        DesktopScrollTarget.Page => "page",
        DesktopScrollTarget.Element => "element",
        DesktopScrollTarget.ToRef => "to_ref",
        DesktopScrollTarget.ToTop => "to_top",
        DesktopScrollTarget.ToBottom => "to_bottom",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Scroll target is not registered."),
    };

    /// <summary>严格解析：不知道的线名不猜测、不回退（fail closed）。</summary>
    public static bool TryParse(string? name, out DesktopScrollTarget target)
    {
        foreach (var candidate in Enum.GetValues<DesktopScrollTarget>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                target = candidate;
                return true;
            }
        }

        target = default;
        return false;
    }
}

/// <summary>
/// 一次滚动的**结果事实**（设计方案 §7）：目标容器、前后位置、实际消耗位移、是否到边界、内容是否变化。
///
/// 「无限滚动另等新增内容/条目数，**不能把无内容加载当滚动失败**」⇒
/// <see cref="ContentChanged"/> 为 <c>false</c> **不是失败**。
/// </summary>
public sealed record DesktopScrollReport
{
    public DesktopScrollReport(
        string container,
        double beforeX = 0,
        double beforeY = 0,
        double afterX = 0,
        double afterY = 0,
        double consumedDeltaX = 0,
        double consumedDeltaY = 0,
        bool atBoundary = false,
        bool contentChanged = false)
    {
        Container = string.IsNullOrWhiteSpace(container) ? "document" : container;
        BeforeX = beforeX;
        BeforeY = beforeY;
        AfterX = afterX;
        AfterY = afterY;
        ConsumedDeltaX = consumedDeltaX;
        ConsumedDeltaY = consumedDeltaY;
        AtBoundary = atBoundary;
        ContentChanged = contentChanged;
    }

    /// <summary>实际滚动的容器：<c>document</c> / <c>element</c> / <c>scrollable-ancestor</c>。</summary>
    public string Container { get; }

    public double BeforeX { get; }
    public double BeforeY { get; }
    public double AfterX { get; }
    public double AfterY { get; }

    /// <summary>实际消耗的位移；为 0 表示"滚不动"（配合 <see cref="AtBoundary"/> 判读）。</summary>
    public double ConsumedDeltaX { get; }
    public double ConsumedDeltaY { get; }

    public bool AtBoundary { get; }

    /// <summary>滚动前后是否观察到内容变化（新增内容/条目）。<b>false 不是失败</b>。</summary>
    public bool ContentChanged { get; }
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
        DesktopInteractionAction.Drag => "drag",
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
        double? deltaY = null,
        DesktopInputMode inputMode = DesktopInputMode.Dom,
        DesktopScrollTarget? scrollTarget = null,
        DesktopLocator? toLocator = null)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Interaction action is not registered.");
        }

        if (!Enum.IsDefined(inputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(inputMode), inputMode, "Input mode is not registered.");
        }

        if (scrollTarget is { } requestedTarget && !Enum.IsDefined(requestedTarget))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scrollTarget), requestedTarget, "Scroll target is not registered.");
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
        InputMode = inputMode;
        ScrollTarget = scrollTarget;
        ToLocator = toLocator;

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

    /// <summary>
    /// 请求的输入路径（§7）。**它只是请求**：实际用哪条路径由驱动的能力决定，
    /// 并必须在回执里如实报告（见 <c>BrowserInteractionToolValue.InputBackend</c>）。
    /// 当前实现只有 <see cref="DesktopInputMode.Dom"/>；请求 <c>Native</c> 必须明确失败。
    /// </summary>
    public DesktopInputMode InputMode { get; }

    /// <summary>
    /// 滚动目标（§7，仅对 <see cref="DesktopInteractionAction.Scroll"/> 有意义）。
    /// <c>null</c> 表示按参数推导：有 locator ⇒ <c>to_ref</c>（无位移）或 <c>element</c>（有位移）；
    /// 无 locator ⇒ <c>page</c>。
    /// </summary>
    public DesktopScrollTarget? ScrollTarget { get; }

    /// <summary>拖拽终点（仅 <c>Drag</c> 使用）。</summary>
    public DesktopLocator? ToLocator { get; }

    /// <summary>实际生效的滚动目标（显式优先，否则按上面的规则推导）。</summary>
    public DesktopScrollTarget EffectiveScrollTarget => ScrollTarget ?? (Locator is null
        ? DesktopScrollTarget.Page
        : DeltaX is null && DeltaY is null ? DesktopScrollTarget.ToRef : DesktopScrollTarget.Element);

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

            case DesktopInteractionAction.Scroll:
                ValidateScroll();
                break;

            // 拖拽必须**两个端点都给**：只有一个端点无法表达"拖到哪儿"，猜一个目标是不诚实的。
            case DesktopInteractionAction.Drag when Locator is null || ToLocator is null:
                throw new ArgumentException(
                    "Action 'drag' requires both a source locator and a target locator.", nameof(ToLocator));
        }
    }

    /// <summary>
    /// 滚动目标的参数一致性（§7）：<b>不盲目滚另一个区域</b>，也不接受有歧义的组合。
    /// </summary>
    private void ValidateScroll()
    {
        var target = EffectiveScrollTarget;
        var name = DesktopScrollTargetWire.NameOf(target);

        // 元素类目标必须给定位（否则无从知道滚哪里）。
        if (target is DesktopScrollTarget.Element or DesktopScrollTarget.ToRef && Locator is null)
        {
            throw new ArgumentException($"scroll target '{name}' requires an element locator.", nameof(Locator));
        }

        // 明确要求滚**文档**时不得再给定位：那会让"到底滚哪一个"变得不可判定（静默忽略更是禁止）。
        if (target == DesktopScrollTarget.Page && Locator is not null)
        {
            throw new ArgumentException(
                "scroll target 'page' must not carry a locator; use a container target (element/to_ref/to_top) instead.",
                nameof(Locator));
        }

        // 位移类目标需要非零位移；to_ref/to_top/to_bottom 是"到位"语义，不需要位移。
        if (target is DesktopScrollTarget.Page or DesktopScrollTarget.Element
            && (DeltaX ?? 0) == 0 && (DeltaY ?? 0) == 0)
        {
            throw new ArgumentException($"scroll target '{name}' requires a non-zero delta.", nameof(DeltaX));
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
    public DesktopInteractionResult(
        DesktopPageTarget target,
        DesktopPageState page,
        DesktopElementRef? element = null,
        DesktopScrollReport? scroll = null)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Page = page ?? throw new ArgumentNullException(nameof(page));
        Element = element;
        Scroll = scroll;
    }

    public DesktopPageTarget Target { get; }

    public DesktopPageState Page { get; }

    public DesktopElementRef? Element { get; }

    /// <summary>滚动动作的结果事实（§7）；非滚动动作或驱动未报告时为 <c>null</c>。</summary>
    public DesktopScrollReport? Scroll { get; }

    public override string ToString() =>
        $"interact @{Target} → v{Page.Version.Value}{(Element is { } element ? $" ({element})" : string.Empty)}";
}
