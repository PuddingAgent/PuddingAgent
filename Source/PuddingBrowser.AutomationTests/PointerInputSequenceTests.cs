using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// §8.2 坐标输入（CSS viewport 像素）：计划器是纯函数 ⇒ 按下/抬起成对、坐标直传（含 (0,0)）、
/// 拖拽中间保持按下、步数守卫都能在进程内钉住（真实点击效果属真机验收）。
/// </summary>
[TestClass]
public sealed class PointerInputSequenceTests
{
    private static JsonElement Payload(NativeInputSequence.CdpCall call) =>
        JsonDocument.Parse(call.ParametersJson).RootElement.Clone();

    [TestMethod]
    public void PointerClickPressesAndReleasesAtTheSamePoint()
    {
        var calls = NativeInputSequence.PlanPointerClick(12.5, 44.25);
        Assert.AreEqual(2, calls.Count, "a click is exactly pressed+released");

        var pressed = Payload(calls[0]);
        var released = Payload(calls[1]);

        Assert.AreEqual("Input.dispatchMouseEvent", calls[0].Method);
        Assert.AreEqual("mousePressed", pressed.GetProperty("type").GetString());
        Assert.AreEqual("mouseReleased", released.GetProperty("type").GetString());
        Assert.AreEqual(12.5, pressed.GetProperty("x").GetDouble());
        Assert.AreEqual(44.25, pressed.GetProperty("y").GetDouble());
        Assert.AreEqual(1, pressed.GetProperty("buttons").GetInt32());
        Assert.AreEqual(0, released.GetProperty("buttons").GetInt32());
        Assert.AreEqual(12.5, released.GetProperty("x").GetDouble());
        Assert.AreEqual(44.25, released.GetProperty("y").GetDouble());
    }

    [TestMethod]
    public void PointerCoordinatesAtTheOriginAreCarriedAsZeroNotDropped()
    {
        var pressed = Payload(NativeInputSequence.PlanPointerClick(0, 0)[0]);
        Assert.AreEqual(0, pressed.GetProperty("x").GetDouble());
        Assert.AreEqual(0, pressed.GetProperty("y").GetDouble());
    }

    [TestMethod]
    public void PointerMoveDoesNotPressAnything()
    {
        var calls = NativeInputSequence.PlanPointerMove(5, 6);
        Assert.AreEqual(1, calls.Count);
        var moved = Payload(calls[0]);
        Assert.AreEqual("mouseMoved", moved.GetProperty("type").GetString());
        Assert.AreEqual(0, moved.GetProperty("buttons").GetInt32());
    }

    [TestMethod]
    public void PointerDragIsPairedAndEndsReleasedAtTheTarget()
    {
        var calls = NativeInputSequence.PlanPointerDrag(0, 0, 100, 50, steps: 4);
        Assert.AreEqual(6, calls.Count, "pressed + 4 moves + released");

        Assert.AreEqual("mousePressed", Payload(calls[0]).GetProperty("type").GetString());
        Assert.AreEqual("mouseReleased", Payload(calls[^1]).GetProperty("type").GetString());
        Assert.AreEqual(0, Payload(calls[^1]).GetProperty("buttons").GetInt32());

        var last = Payload(calls[^1]);
        Assert.AreEqual(100, last.GetProperty("x").GetDouble());
        Assert.AreEqual(50, last.GetProperty("y").GetDouble());

        for (var i = 1; i < calls.Count - 1; i++)
        {
            var step = Payload(calls[i]);
            Assert.AreEqual("mouseMoved", step.GetProperty("type").GetString());
            Assert.AreEqual(1, step.GetProperty("buttons").GetInt32());
        }
    }

    [TestMethod]
    public void PointerDragRefusesAZeroStepPlan()
    {
        var refused = false;
        try
        {
            NativeInputSequence.PlanPointerDrag(0, 0, 10, 10, steps: 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            refused = true;
        }

        Assert.IsTrue(refused, "a zero-step drag must be refused instead of guessing a default");
    }
}