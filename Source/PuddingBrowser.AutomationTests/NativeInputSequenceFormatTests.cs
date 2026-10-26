using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 原生输入（CDP）的序列规划（设计方案 §7）。
/// 真正要钉住的是"按键/指针状态不会留在页面里"：按下必成对抬起、修饰键顺序正确、取消能清理。
/// </summary>
[TestClass]
public sealed class NativeInputSequenceTests
{
    private static readonly NativeInputSequence.InputTarget Target = new(X: 10, Y: 20, Width: 100, Height: 40);

    private static JsonElement Parse(NativeInputSequence.CdpCall call) =>
        JsonDocument.Parse(call.ParametersJson).RootElement;

    [TestMethod]
    public void Click_MovesPressesAndReleasesAtTheCenter()
    {
        var calls = NativeInputSequence.PlanClick(Target);

        Assert.AreEqual(3, calls.Count);
        Assert.AreEqual("Input.dispatchMouseEvent", calls[0].Method);

        // 顺序固定：移动 → 按下 → 抬起（**成对**，不留按住状态）。
        Assert.AreEqual("mouseMoved", Parse(calls[0]).GetProperty("type").GetString());
        Assert.AreEqual("mousePressed", Parse(calls[1]).GetProperty("type").GetString());
        Assert.AreEqual("mouseReleased", Parse(calls[2]).GetProperty("type").GetString());

        // 位置取元素中心，且按下时 buttons=1、抬起时 buttons=0。
        Assert.AreEqual(60d, Parse(calls[1]).GetProperty("x").GetDouble());
        Assert.AreEqual(40d, Parse(calls[1]).GetProperty("y").GetDouble());
        Assert.AreEqual(1, Parse(calls[1]).GetProperty("buttons").GetInt32());
        Assert.AreEqual(0, Parse(calls[2]).GetProperty("buttons").GetInt32());
        Assert.AreEqual("left", Parse(calls[1]).GetProperty("button").GetString());
    }

    [TestMethod]
    public void HoverOnlyMoves_NeverPresses()
    {
        var calls = NativeInputSequence.PlanHover(Target);

        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual("mouseMoved", Parse(calls[0]).GetProperty("type").GetString());
        Assert.AreEqual(0, Parse(calls[0]).GetProperty("buttons").GetInt32());
    }

    [TestMethod]
    public void ScrollSendsAWheelEventWithBothDeltas()
    {
        var calls = NativeInputSequence.PlanScroll(Target, deltaX: 0, deltaY: 320);

        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual("mouseWheel", Parse(calls[0]).GetProperty("type").GetString());
        Assert.AreEqual(320d, Parse(calls[0]).GetProperty("deltaY").GetDouble());
    }

    [TestMethod]
    public void FillUsesInsertText_AndClearingUsesSelectAllThenDelete()
    {
        var fill = NativeInputSequence.PlanFill("hello");
        Assert.AreEqual(1, fill.Count);
        Assert.AreEqual("Input.insertText", fill[0].Method);
        StringAssert.Contains(fill[0].ParametersJson, "hello");

        // 空文本在原生路径下必须真的清空：Ctrl+A → Delete（insertText("") 什么也不做）。
        var clear = NativeInputSequence.PlanFill(string.Empty);
        Assert.IsTrue(clear.Count >= 4, "清空应包含修饰键按下/抬起与删除键");
        Assert.AreEqual("Input.dispatchKeyEvent", clear[0].Method);
    }

    [TestMethod]
    public void TypeEmitsKeyDownAndKeyUpPerCharacter()
    {
        var calls = NativeInputSequence.PlanType("ab");

        Assert.AreEqual(4, calls.Count);
        for (var index = 0; index < calls.Count; index++)
        {
            Assert.AreEqual("Input.dispatchKeyEvent", calls[index].Method);
            var expected = index % 2 == 0 ? "keyDown" : "keyUp";
            Assert.AreEqual(expected, Parse(calls[index]).GetProperty("type").GetString());
        }

        // keyDown 带 text（页面才能收到 input），keyUp 不带。
        Assert.AreEqual("a", Parse(calls[0]).GetProperty("text").GetString());
        Assert.IsFalse(Parse(calls[1]).TryGetProperty("text", out _));
    }

    /// <summary>
    /// 非 ASCII（中文/emoji/全角）没有物理键位：旧版直接 continue，“type("大肥鱼")” 一个字也进不去
    /// （实测 2026-10-26：内容不进框、发送按钮不出现）。正确形态是只带 text 的 keyDown，
    /// 也就是 CDP 里输入法提交的样子 —— 页面走正常输入管线，自定义富文本编辑器才肯置脏。
    /// </summary>
    [TestMethod]
    public void TypeEmitsImeStyleKeyDownForNonAsciiCharacters()
    {
        var calls = NativeInputSequence.PlanType("大a");

        // '大': 一个 text-only keyDown；'a': 常规 keyDown+keyUp。
        Assert.AreEqual(3, calls.Count);

        var first = Parse(calls[0]);
        Assert.AreEqual("Input.dispatchKeyEvent", calls[0].Method);
        Assert.AreEqual("keyDown", first.GetProperty("type").GetString());
        Assert.AreEqual("大", first.GetProperty("text").GetString());
        Assert.AreEqual("大", first.GetProperty("unmodifiedText").GetString());
        // 关键判据：没有伪造键位。塞一个错的虚拟键码会让页面当成别的按键。
        Assert.IsFalse(first.TryGetProperty("windowsVirtualKeyCode", out _));
        Assert.IsFalse(first.TryGetProperty("key", out _));
    }

    [TestMethod]
    public void PressWithModifiers_WrapsTheKeyBetweenModifierDownAndUp()
    {
        var calls = NativeInputSequence.PlanPress("Control+A");

        // Control 按下 → 键 rawKeyDown → char（编辑意图）→ 键抬起 → Control 抬起。
        // 多了 char 这条：带 key/code/vkCode 的“物理键”事件不产生 beforeinput，
        // React 控制的 contenteditable 因此收不到按键（2026-10-26 抖音评论框实测）。
        Assert.AreEqual(5, calls.Count);
        Assert.AreEqual("Control", Parse(calls[0]).GetProperty("key").GetString());
        Assert.AreEqual("keyDown", Parse(calls[0]).GetProperty("type").GetString());
        Assert.AreEqual("a", Parse(calls[1]).GetProperty("key").GetString());
        Assert.AreEqual("rawKeyDown", Parse(calls[1]).GetProperty("type").GetString());
        Assert.AreEqual("char", Parse(calls[2]).GetProperty("type").GetString());
        Assert.AreEqual("a", Parse(calls[2]).GetProperty("text").GetString());
        Assert.AreEqual("keyUp", Parse(calls[3]).GetProperty("type").GetString());
        Assert.AreEqual("Control", Parse(calls[4]).GetProperty("key").GetString());
        Assert.AreEqual("keyUp", Parse(calls[4]).GetProperty("type").GetString());

        // 修饰键按 CDP 位掩码表达（Control=2），不是自己编的字符串。
        Assert.AreEqual(NativeInputSequence.ControlModifier, Parse(calls[1]).GetProperty("modifiers").GetInt32());
    }

    [TestMethod]
    public void Enter_CarriesACharEventSoEditorsSeeTheSubmit()
    {
        var calls = NativeInputSequence.PlanPress("Enter");

        // 三条：物理键位 + char + 抬起。缺了 char 就是“按了回车、页面什么也没收到”。
        Assert.AreEqual(3, calls.Count);
        Assert.AreEqual("rawKeyDown", Parse(calls[0]).GetProperty("type").GetString());
        Assert.AreEqual(13, Parse(calls[0]).GetProperty("windowsVirtualKeyCode").GetInt32());
        Assert.AreEqual("char", Parse(calls[1]).GetProperty("type").GetString());
        Assert.AreEqual("\r", Parse(calls[1]).GetProperty("text").GetString());
        Assert.AreEqual("keyUp", Parse(calls[2]).GetProperty("type").GetString());
    }

    [TestMethod]
    public void NamedKeys_CarryTheirVirtualKeyCodes()
    {
        Assert.IsTrue(NativeInputSequence.TryResolveKey("Enter", out var enter));
        Assert.AreEqual(13, enter.WindowsVirtualKeyCode);
        Assert.AreEqual("Enter", enter.Code);

        Assert.IsTrue(NativeInputSequence.TryResolveKey("ArrowDown", out var down));
        Assert.AreEqual(40, down.WindowsVirtualKeyCode);

        Assert.IsTrue(NativeInputSequence.TryResolveKey("Escape", out var escape));
        Assert.AreEqual(27, escape.WindowsVirtualKeyCode);

        // 字母/数字键：code 用 "KeyA"/"Digit1"，virtualKeyCode 用大写 ASCII。
        Assert.IsTrue(NativeInputSequence.TryResolveKey("a", out var letter));
        Assert.AreEqual("KeyA", letter.Code);
        Assert.AreEqual(65, letter.WindowsVirtualKeyCode);
    }

    [TestMethod]
    public void UnknownKeys_AreRejectedInsteadOfGuessed()
    {
        Assert.IsFalse(NativeInputSequence.TryResolveKey("Frobnicate", out _));
        Assert.IsFalse(NativeInputSequence.TryResolveKey("Control+", out _));
        Assert.IsFalse(NativeInputSequence.TryResolveKey(null, out _));
        Assert.IsFalse(NativeInputSequence.TryResolveKey("", out _));

        Assert.ThrowsExactly<ArgumentException>(() => NativeInputSequence.PlanPress("Frobnicate"));
    }

    [TestMethod]
    public void ReleaseModifiers_CleansUpSoThePageCannotStayStuckInACombo()
    {
        // 取消时清理：一次被取消的 Ctrl+点击不能把页面卡在"Ctrl 一直按着"。
        var calls = NativeInputSequence.ReleaseModifiers(
            NativeInputSequence.ControlModifier | NativeInputSequence.ShiftModifier);

        Assert.AreEqual(2, calls.Count);
        foreach (var call in calls)
        {
            Assert.AreEqual("Input.dispatchKeyEvent", call.Method);
            Assert.AreEqual("keyUp", Parse(call).GetProperty("type").GetString());
        }

        Assert.AreEqual("Control", Parse(calls[0]).GetProperty("key").GetString());
        Assert.AreEqual("Shift", Parse(calls[1]).GetProperty("key").GetString());

        Assert.AreEqual(0, NativeInputSequence.ReleaseModifiers(0).Count);
    }

    [TestMethod]
    public void DragPressesAtTheSourceMovesInStepsAndReleasesAtTheTarget()
    {
        // 拖拽的硬约束：按下必然抬起；中间必须有 mousemove 序列（很多实现依赖它）。
        var from = new NativeInputSequence.InputTarget(0, 0, 20, 20);    // 中心 (10,10)
        var to = new NativeInputSequence.InputTarget(100, 0, 20, 20);   // 中心 (110,10)

        var calls = NativeInputSequence.PlanDrag(from, to, steps: 4);

        Assert.AreEqual(4 + 3, calls.Count);
        Assert.AreEqual("mouseMoved", Parse(calls[0]).GetProperty("type").GetString());
        Assert.AreEqual(0, Parse(calls[0]).GetProperty("buttons").GetInt32());
        Assert.AreEqual("mousePressed", Parse(calls[1]).GetProperty("type").GetString());
        Assert.AreEqual(1, Parse(calls[1]).GetProperty("buttons").GetInt32());
        Assert.AreEqual(10d, Parse(calls[1]).GetProperty("x").GetDouble());

        // 中间的移动全程 buttons=1，且坐标单调走向终点。
        for (var index = 2; index < calls.Count - 1; index++)
        {
            Assert.AreEqual("mouseMoved", Parse(calls[index]).GetProperty("type").GetString());
            Assert.AreEqual(1, Parse(calls[index]).GetProperty("buttons").GetInt32());
        }

        Assert.AreEqual(35d, Parse(calls[2]).GetProperty("x").GetDouble());
        Assert.AreEqual(110d, Parse(calls[^2]).GetProperty("x").GetDouble());

        // 最后必定是抬起，且 buttons 归零。
        Assert.AreEqual("mouseReleased", Parse(calls[^1]).GetProperty("type").GetString());
        Assert.AreEqual(0, Parse(calls[^1]).GetProperty("buttons").GetInt32());
    }

    [TestMethod]
    public void DragStepCountIsBounded()
    {
        var from = new NativeInputSequence.InputTarget(0, 0, 0, 0);
        var to = new NativeInputSequence.InputTarget(10, 10, 0, 0);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NativeInputSequence.PlanDrag(from, to, steps: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => NativeInputSequence.PlanDrag(from, to, NativeInputSequence.MaxDragSteps + 1));
    }
    [TestMethod]
    public void Plans_AreDeterministic()
    {
        // 同样的输入必须给出同样的调用序列（否则"取消清理"与重放都会变得不可预测）。
        Assert.AreEqual(
            NativeInputSequence.PlanPress("Control+Shift+A")[1].ParametersJson,
            NativeInputSequence.PlanPress("Control+Shift+A")[1].ParametersJson);
    }
}
