using System.Text;
using System.Text.Json;

namespace PuddingBrowser.Automation;

/// <summary>
/// 原生输入（CDP 键鼠）的**调用序列规划**（设计方案 §7：<c>inputMode=dom/native</c>）。
///
/// 为什么把"序列"单独做成纯函数：原生输入真正难的不是发一条 CDP 命令，而是
/// ① **按下必须成对抬起**（否则页面里留下按住不放的键/键位状态，下一个动作就出错）；
/// ② **修饰键按 CDP 位掩码**正确表达（Alt=1、Control=2、Meta=4、Shift=8）；
/// ③ **取消时清理按键状态**（任务取消不能把页面卡在"Ctrl 一直按着"）。
/// 这些都能在进程内逐条验证，因此不放进只能靠真机碰运气的驱动代码里。
///
/// 计划本身是"要发哪些 CDP 调用"的清单：驱动按顺序执行即可，不需要再判断语义。
/// </summary>
public static class NativeInputSequence
{
    /// <summary>CDP 修饰键位掩码（官方定义，不是我们自己编的）。</summary>
    public const int AltModifier = 1;
    public const int ControlModifier = 2;
    public const int MetaModifier = 4;
    public const int ShiftModifier = 8;

    /// <summary>拖拽默认分几步移动（中间必须有 mousemove 序列，见 PlanDrag）。</summary>
    public const int DefaultDragSteps = 8;

    /// <summary>分步上限（防调用方给出离谱步数把一次拖拽变成几千次 CDP 调用）。</summary>
    public const int MaxDragSteps = 64;

    /// <summary>
    /// 原生输入的目标几何（**本组件不引用 Abstractions**，因此用自己的最小形状；
    /// 调用方从元素包围盒映射进来）。
    /// </summary>
    public readonly record struct InputTarget(double X, double Y, double Width, double Height);

    /// <summary>一次 CDP 调用：方法名 + 已序列化的参数。</summary>
    public sealed record CdpCall(string Method, string ParametersJson)
    {
        public override string ToString() => $"{Method}({ParametersJson})";
    }

    /// <summary>解析后的按键：CDP <c>dispatchKeyEvent</c> 需要的全部字段。</summary>
    public sealed record KeyStroke(
        string Key,
        string Code,
        int WindowsVirtualKeyCode,
        string? Text,
        int Modifiers);

    /// <summary>鼠标点击：**分步移动**→按下→抬起（**成对**，永远不留按住状态）。
    ///
    /// 为什么不能瞬移：一次 <c>mouseMoved</c> 直接落在目标中心，在风控看来就是“没有轨迹”。
    /// 真实人手的移动是一条连续的、带加减速的曲线，因此这里发多个中间点
    /// （smoothstep 缓动：先慢后快再慢），且轨迹由目标坐标**确定性**推出 —— 同一个目标
    /// 永远同一条轨迹，可断言、可重放。
    /// </summary>
    public static IReadOnlyList<CdpCall> PlanClick(InputTarget target, int clickCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clickCount, 1);

        var (x, y) = Center(target);
        return PlanPointerClick(x, y, clickCount);
    }

    /// <summary>悬停：同样带轨迹（真人把鼠标移到元素上也是一条曲线），但不按下。</summary>
    public static IReadOnlyList<CdpCall> PlanHover(InputTarget target)
    {
        var (x, y) = Center(target);
        var calls = new List<CdpCall>(DefaultPointerSteps);
        foreach (var (sx, sy) in ApproachPath(x, y))
        {
            calls.Add(Mouse("mouseMoved", sx, sy, button: "none", buttons: 0, clickCount: 0));
        }

        return calls;
    }

    /// <summary>
    /// **拖拽**：移动到起点 → 按下 → 分步移动到终点（全程 buttons=1）→ 抬起。
    ///
    /// 为什么**分步**而不是"起点按下、终点抬起"：很多拖拽实现（HTML5 drag&drop、画布、滑块）依赖中间的
    /// mousemove 序列，只发一次跳跃移动会被忽略或落错位置；而**按下必然抬起**（即使中途取消）是这条链上
    /// 最硬的约束 —— 留下按住状态会让后续动作全部走样。
    /// </summary>
    public static IReadOnlyList<CdpCall> PlanDrag(InputTarget from, InputTarget to, int steps = DefaultDragSteps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(steps, MaxDragSteps);

        var (fromX, fromY) = Center(from);
        var (toX, toY) = Center(to);

        var calls = new List<CdpCall>(steps + 3)
        {
            Mouse("mouseMoved", fromX, fromY, button: "none", buttons: 0, clickCount: 0),
            Mouse("mousePressed", fromX, fromY, button: "left", buttons: 1, clickCount: 1),
        };

        for (var step = 1; step <= steps; step++)
        {
            var ratio = (double)step / steps;
            calls.Add(Mouse(
                "mouseMoved",
                Math.Round(fromX + ((toX - fromX) * ratio), 2),
                Math.Round(fromY + ((toY - fromY) * ratio), 2),
                button: "left",
                buttons: 1,
                clickCount: 0));
        }

        calls.Add(Mouse("mouseReleased", toX, toY, button: "left", buttons: 0, clickCount: 1));
        return calls;
    }
    /// <summary>滚动：在元素位置发滚轮事件（<c>deltaY&gt;0</c> 向下）。</summary>
    public static IReadOnlyList<CdpCall> PlanScroll(InputTarget target, double deltaX, double deltaY)
    {
        
        var (x, y) = Center(target);

        return
        [
            new CdpCall("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
            {
                type = "mouseWheel",
                x,
                y,
                deltaX,
                deltaY,
                button = "none",
                buttons = 0,
                clickCount = 0,
            })),
        ];
    }

    /// <summary>
    /// 填入文本：用 <c>Input.insertText</c>（一次写入，不模拟逐键）。
    /// <c>fill</c> 的语义本来就是"设置值"，不需要键位状态。
    /// </summary>
    public static IReadOnlyList<CdpCall> PlanFill(string? text)
    {
        var value = text ?? string.Empty;
        if (value.Length == 0)
        {
            // 清空输入框：原生路径下用全选 + 删除，否则 insertText("") 什么也不做。
            return
            [
                .. PlanPress("Control+A"),
                .. PlanPress("Delete"),
            ];
        }

        return
        [
            new CdpCall("Input.insertText", JsonSerializer.Serialize(new { text = value })),
        ];
    }

    /// <summary>
    /// 逐键输入：可打印 ASCII 走 <c>keyDown</c>(带 text)+<c>keyUp</c>，
    /// 非 ASCII（中文/emoji/全角标点）走 <c>Input.insertText</c>。
    /// 两条路的分工不是风格选择，而是受控编辑器认什么决定的（见下方注释）。
    /// </summary>
    public static IReadOnlyList<CdpCall> PlanType(string? text)
    {
        var value = text ?? string.Empty;
        var calls = new List<CdpCall>(value.Length * 2);

        foreach (var character in value)
        {
            if (StrokeFor(character.ToString()) is { } stroke)
            {
                calls.Add(Key("keyDown", stroke, includeText: true));
                calls.Add(Key("keyUp", stroke, includeText: false));
                continue;
            }

            // 非 ASCII（中文/emoji/全角标点）没有可派发的物理键位，只能用 insertText。
            //
            // 曾经用“只带 text 的 keyDown”代替，认为那等于输入法提交。抖音评论框
            // （Draft.js 受控 contenteditable）实测证伪：DOM 里字出现了、EditorState 仍是空的，
            // 于是发送按钮不出现、回车也不提交 —— 页面看到的是“有字”，框架看到的是“没字”。
            // 原因是 Draft/React 的可打印字符插入挂在 keydown 上，而 keydown 只认物理键位
            // （key/code/vkCode）；没有键位的 keyDown 只让浏览器自己改了 DOM。
            // Input.insertText 走浏览器原生插入路径，受控编辑器会正确置脏（实测 2026-10-26）。
            calls.Add(InsertText(character.ToString()));
        }

        return calls;
    }

    /// <summary>
    /// 按键：支持 <c>Control+A</c> 这类组合（修饰键在 <c>keyDown</c> 之前按下、<c>keyUp</c> 之后抬起，
    /// 顺序反了页面看到的修饰状态就是错的）。
    /// </summary>
    public static IReadOnlyList<CdpCall> PlanPress(string? key)
    {
        if (!TryResolveKey(key, out var stroke))
        {
            throw new ArgumentException($"Unsupported key '{key}'.", nameof(key));
        }

        var calls = new List<CdpCall>(6);
        foreach (var modifier in ModifierKeysOf(stroke.Modifiers))
        {
            calls.Add(Key("keyDown", modifier, includeText: false));
        }

        calls.Add(Key("rawKeyDown", stroke, includeText: false));

        // Ctrl/Alt/Meta 组合是**命令**（Ctrl+A 全选、Ctrl+V 粘贴），不是“输入这个字符”：
        // 这类组合再补 char 会把字面字符也插进编辑区（Ctrl+V 会额外插入一个 "v"）。
        // Shift 不是命令修饰（Shift+a 仍然是输入 'a'），因此只排除这三个。
        var isCommand = (stroke.Modifiers & (ControlModifier | AltModifier | MetaModifier)) != 0;
        if (!isCommand && stroke.Text is { Length: > 0 } text)
        {
            // 显式 char：CDP 里唯一会产生 beforeinput/input 的按键形态。
            // 只发 keyDown(text=…) 时，带 key/code/vkCode 的“物理键”事件不会产生编辑意图，
            // React 控制的 contenteditable（Slate/ProseMirror 这类）因此**收不到回车**，
            // 页面看起来按了、实际什么也没提交（2026-10-26 在抖音评论框实测）。
            calls.Add(Char(text));
        }

        calls.Add(Key("keyUp", stroke, includeText: false));

        foreach (var modifier in ModifierKeysOf(stroke.Modifiers))
        {
            calls.Add(Key("keyUp", modifier, includeText: false));
        }

        return calls;
    }

    /// <summary>
    /// **取消时清理按键状态**：把按下过的修饰键逐个抬起。
    /// 不做这一步，一次被取消的 Ctrl+点击会让页面以为 Ctrl 一直按着。
    /// </summary>
    public static IReadOnlyList<CdpCall> ReleaseModifiers(int modifiers)
    {
        var calls = new List<CdpCall>(3);
        foreach (var modifier in ModifierKeysOf(modifiers))
        {
            calls.Add(Key("keyUp", modifier, includeText: false));
        }

        return calls;
    }

    /// <summary>解析 <c>"Control+Shift+A"</c> / <c>"Enter"</c> 之类的按键描述。</summary>
    public static bool TryResolveKey(string? key, out KeyStroke stroke)
    {
        stroke = null!;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var parts = key.Trim().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = 0;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var modifier = parts[index].ToLowerInvariant() switch
            {
                "alt" => AltModifier,
                "control" or "ctrl" => ControlModifier,
                "meta" or "cmd" or "command" or "win" => MetaModifier,
                "shift" => ShiftModifier,
                _ => 0,
            };

            if (modifier == 0)
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (StrokeFor(parts[^1], modifiers) is not { } resolved)
        {
            return false;
        }

        stroke = resolved;
        return true;
    }

    private static KeyStroke? StrokeFor(string key, int modifiers = 0)
    {
        var named = key.ToLowerInvariant() switch
        {
            "enter" or "return" => new KeyStroke("Enter", "Enter", 13, "\r", modifiers),
            "tab" => new KeyStroke("Tab", "Tab", 9, "\t", modifiers),
            "escape" or "esc" => new KeyStroke("Escape", "Escape", 27, null, modifiers),
            "backspace" => new KeyStroke("Backspace", "Backspace", 8, null, modifiers),
            "delete" => new KeyStroke("Delete", "Delete", 46, null, modifiers),
            "arrowup" or "up" => new KeyStroke("ArrowUp", "ArrowUp", 38, null, modifiers),
            "arrowdown" or "down" => new KeyStroke("ArrowDown", "ArrowDown", 40, null, modifiers),
            "arrowleft" or "left" => new KeyStroke("ArrowLeft", "ArrowLeft", 37, null, modifiers),
            "arrowright" or "right" => new KeyStroke("ArrowRight", "ArrowRight", 39, null, modifiers),
            "home" => new KeyStroke("Home", "Home", 36, null, modifiers),
            "end" => new KeyStroke("End", "End", 35, null, modifiers),
            "pageup" => new KeyStroke("PageUp", "PageUp", 33, null, modifiers),
            "pagedown" => new KeyStroke("PageDown", "PageDown", 34, null, modifiers),
            "space" or " " => new KeyStroke(" ", "Space", 32, " ", modifiers),
            _ => null,
        };

        if (named is not null)
        {
            return named;
        }

        if (key.Length == 1)
        {
            var upper = char.ToUpperInvariant(key[0]);
            var isLetter = upper is >= 'A' and <= 'Z';
            var isDigit = key[0] is >= '0' and <= '9';
            if (!isLetter && !isDigit)
            {
                return null;
            }

            // 字母键：CDP 的 code 是 "KeyA"，virtualKeyCode 用大写字母的 ASCII。
            // **Shift 按下时 text 仍然是字符本身**（由 modifiers 表达大小写），不要在这里改大小写。
            var code = isLetter ? $"Key{upper}" : $"Digit{key[0]}";
            return new KeyStroke(key.ToLowerInvariant(), code, upper, key.ToLowerInvariant(), modifiers);
        }

        return null;
    }

    /// <summary>按固定顺序给出修饰键的独立按键（顺序稳定 ⇒ 计划可被断言）。</summary>
    private static IEnumerable<KeyStroke> ModifierKeysOf(int modifiers)
    {
        if ((modifiers & ControlModifier) != 0)
        {
            yield return new KeyStroke("Control", "ControlLeft", 17, null, 0);
        }

        if ((modifiers & AltModifier) != 0)
        {
            yield return new KeyStroke("Alt", "AltLeft", 18, null, 0);
        }

        if ((modifiers & ShiftModifier) != 0)
        {
            yield return new KeyStroke("Shift", "ShiftLeft", 16, null, 0);
        }

        if ((modifiers & MetaModifier) != 0)
        {
            yield return new KeyStroke("Meta", "MetaLeft", 91, null, 0);
        }
    }

    /// <summary>
    /// 原生插入文本：唯一能让受控富文本编辑器（Draft.js / React）真正更新自己状态的写入形态。
    /// 中文/emoji 没有物理键位，必须走这条；伪造一个错的虚拟键码会被页面当成别的按键。
    /// </summary>
    private static CdpCall InsertText(string text) =>
        new("Input.insertText", JsonSerializer.Serialize(new { text }));

    /// <summary>
    /// 显式 <c>char</c> 事件：CDP 里唯一会为命名键产生 <c>beforeinput</c>/<c>input</c> 的形态。
    /// </summary>
    private static CdpCall Char(string text) =>
        new("Input.dispatchKeyEvent", JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "char",
            ["text"] = text,
            ["unmodifiedText"] = text,
        }));

    private static CdpCall Key(string type, KeyStroke stroke, bool includeText)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["key"] = stroke.Key,
            ["code"] = stroke.Code,
            ["windowsVirtualKeyCode"] = stroke.WindowsVirtualKeyCode,
            ["nativeVirtualKeyCode"] = stroke.WindowsVirtualKeyCode,
            ["modifiers"] = stroke.Modifiers,
            ["autoRepeat"] = false,
            ["isKeypad"] = false,
            ["isSystemKey"] = false,
        };

        if (includeText && stroke.Text is { Length: > 0 })
        {
            payload["text"] = stroke.Text;
            payload["unmodifiedText"] = stroke.Text;
        }

        return new CdpCall("Input.dispatchKeyEvent", JsonSerializer.Serialize(payload));
    }

    /// <summary>
    /// §8.2 **坐标点击**（CSS viewport 像素）：直接给坐标，不经元素定位。
    /// 按下与抬起**成对**且坐标一致 —— 只按下不抬起会让页面卡在按下态。
    /// </summary>
    public static IReadOnlyList<CdpCall> PlanPointerClick(double x, double y, int clickCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clickCount, 1);

        var calls = new List<CdpCall>(DefaultPointerSteps + 2);
        foreach (var (moveX, moveY) in ApproachPath(x, y))
        {
            calls.Add(Mouse("mouseMoved", moveX, moveY, "none", 0, 0));
        }

        calls.Add(Mouse("mousePressed", x, y, "left", 1, clickCount));
        calls.Add(Mouse("mouseReleased", x, y, "left", 0, clickCount));
        return calls;
    }

    /// <summary>§8.2 **坐标移动**：带轨迹移动到目标，不按下。</summary>
    public static IReadOnlyList<CdpCall> PlanPointerMove(double x, double y)
    {
        var calls = new List<CdpCall>(DefaultPointerSteps);
        foreach (var (moveX, moveY) in ApproachPath(x, y))
        {
            calls.Add(Mouse("mouseMoved", moveX, moveY, "none", 0, 0));
        }

        return calls;
    }

    /// <summary>
    /// 移到目标所需的最少中间步数。它存在是因为行为特征：
    /// 一次 <c>mouseMoved</c> 直接落在目标中心，对页面/风控而言等于“指针凭空出现”。
    /// </summary>
    public const int DefaultPointerSteps = 6;

    /// <summary>
    /// 从“虚想起点”到目标的分步轨迹：起点固定为目标偏移 (-48,-32)，按 smoothstep 缓动（先慢后快再慢）收敛。
    /// 起点由目标坐标确定性推导、不引入随机数 —— 同一个目标永远同一条轨迹，可断言、可重放。
    /// </summary>
    private static IEnumerable<(double X, double Y)> ApproachPath(double x, double y)
    {
        const double StartOffsetX = -48;
        const double StartOffsetY = -32;

        for (var step = 1; step <= DefaultPointerSteps; step++)
        {
            var t = (double)step / DefaultPointerSteps;
            var eased = t * t * (3 - (2 * t));
            var remaining = 1 - eased;
            yield return (
                Math.Round(x + (StartOffsetX * remaining), 2),
                Math.Round(y + (StartOffsetY * remaining), 2));
        }
    }

    /// <summary>
    /// §8.2 **坐标拖拽**：起点按下 → 分步移动 → 终点抬起。
    /// 步数存在是为了让页面能观察到中间的 pointermove（一步跳到底在很多 UI 上不生效）；
    /// 每一步都是独立的 CDP 调用，调用方被取消时不会留下"按下未抬起"的状态（末尾必然有抬起）。
    /// </summary>
    public static IReadOnlyList<CdpCall> PlanPointerDrag(double fromX, double fromY, double toX, double toY, int steps = 8)
    {
        if (steps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), steps, "a pointer drag needs at least one move step");
        }

        var calls = new List<CdpCall> { Mouse("mousePressed", fromX, fromY, "left", 1, 1) };
        for (var step = 1; step <= steps; step++)
        {
            var ratio = (double)step / steps;
            calls.Add(Mouse(
                "mouseMoved",
                fromX + ((toX - fromX) * ratio),
                fromY + ((toY - fromY) * ratio),
                "left",
                1,
                0));
        }

        calls.Add(Mouse("mouseReleased", toX, toY, "left", 0, 1));
        return calls;
    }

    private static CdpCall Mouse(string type, double x, double y, string button, int buttons, int clickCount) =>
        new("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
            type,
            x,
            y,
            button,
            buttons,
            clickCount,
        }));

    private static (double X, double Y) Center(InputTarget target) =>
        (Math.Round(target.X + (target.Width / 2), 2), Math.Round(target.Y + (target.Height / 2), 2));
}
