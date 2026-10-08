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

    [TestMethod]
    public void PressWithModifiers_WrapsTheKeyBetweenModifierDownAndUp()
    {
        var calls = NativeInputSequence.PlanPress("Control+A");

        Assert.AreEqual(4, calls.Count);
        Assert.AreEqual("Control", Parse(calls[0]).GetProperty("key").GetString());
        Assert.AreEqual("keyDown", Parse(calls[0]).GetProperty("type").GetString());
        Assert.AreEqual("a", Parse(calls[1]).GetProperty("key").GetString());
        Assert.AreEqual("keyDown", Parse(calls[1]).GetProperty("type").GetString());
        Assert.AreEqual("keyUp", Parse(calls[2]).GetProperty("type").GetString());
        Assert.AreEqual("Control", Parse(calls[3]).GetProperty("key").GetString());
        Assert.AreEqual("keyUp", Parse(calls[3]).GetProperty("type").GetString());

        // 修饰键按 CDP 位掩码表达（Control=2），不是自己编的字符串。
        Assert.AreEqual(NativeInputSequence.ControlModifier, Parse(calls[1]).GetProperty("modifiers").GetInt32());
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
    public void Plans_AreDeterministic()
    {
        // 同样的输入必须给出同样的调用序列（否则"取消清理"与重放都会变得不可预测）。
        Assert.AreEqual(
            NativeInputSequence.PlanPress("Control+Shift+A")[1].ParametersJson,
            NativeInputSequence.PlanPress("Control+Shift+A")[1].ParametersJson);
    }
}
