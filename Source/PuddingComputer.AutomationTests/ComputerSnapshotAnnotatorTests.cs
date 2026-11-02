using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.AutomationTests;

/// <summary>
/// 坐标标注（用户要求：坐标必须由 PuddingAgent 附加，模型不参与计算）。
/// <para>
/// 这里的断言都是"算错了不会报错、只会点歪"的那类，所以逐条钉死。
/// </para>
/// </summary>
[TestClass]
public sealed class ComputerSnapshotAnnotatorTests
{
    /// <summary>实测的窗口几何：ChatGPT 主窗口 (2560,0) 2048x1104。</summary>
    private static PhysicalRect Window => new(2560, 0, 2048, 1104);

    [TestMethod]
    public void Ids_are_sequential_from_one()
    {
        var raw = new[]
        {
            new RawDetection(new PhysicalRect(100, 100, 40, 20), "button"),
            new RawDetection(new PhysicalRect(200, 200, 40, 20), "button"),
            new RawDetection(new PhysicalRect(300, 300, 40, 20), "text"),
        };

        var targets = ComputerSnapshotAnnotator.Annotate(raw, Window);

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, targets.Select(t => t.Id).ToArray());
    }

    [TestMethod]
    public void Global_and_window_local_differ_by_window_origin()
    {
        var raw = new[] { new RawDetection(new PhysicalRect(2660, 100, 40, 20), "button") };

        var t = ComputerSnapshotAnnotator.Annotate(raw, Window).Single();

        Assert.AreEqual(2660, t.Global.X);
        Assert.AreEqual(100, t.Global.Y);
        Assert.AreEqual(100, t.WindowLocal.X, "2660 - 2560 = 100");
        Assert.AreEqual(100, t.WindowLocal.Y, "100 - 0 = 100");
        Assert.AreEqual(40, t.WindowLocal.Width, "尺寸不随坐标平移改变");
        Assert.AreEqual(20, t.WindowLocal.Height);
    }

    [TestMethod]
    public void Clickable_point_is_the_rect_center()
    {
        var raw = new[] { new RawDetection(new PhysicalRect(2660, 100, 40, 20), "button") };

        var t = ComputerSnapshotAnnotator.Annotate(raw, Window).Single();

        Assert.AreEqual(2680, t.Clickable.X, "2660 + 40/2");
        Assert.AreEqual(110, t.Clickable.Y, "100 + 20/2");
    }

    [TestMethod]
    public void Elements_outside_the_window_are_flagged_unclickable()
    {
        // 实测过的情况：发送键渲染在 y=1327，而窗口只到 1104
        var raw = new[] { new RawDetection(new PhysicalRect(4483, 1327, 35, 35), "button") };

        var t = ComputerSnapshotAnnotator.Annotate(raw, Window).Single();

        Assert.IsFalse(t.InsideWindow,
            "该点 WindowFromPoint 返回 NULL，点击会被穿透到桌面，必须标为不可点");
    }

    [TestMethod]
    public void Element_fully_inside_is_clickable()
    {
        var raw = new[] { new RawDetection(new PhysicalRect(2660, 100, 40, 20), "button") };

        var t = ComputerSnapshotAnnotator.Annotate(raw, Window).Single();

        Assert.IsTrue(t.InsideWindow);
    }

    [TestMethod]
    public void Element_crossing_the_edge_but_center_inside_is_clickable()
    {
        // 右边缘越界 10px（X=4578,W=40 → 尾 4618 > 窗右界 4608），
        // 但**中心点** 4578+20=4598 仍在窗内。
        //
        // 2026-11-03 端到端修正：旧实现要求“整块矩形在窗内”，
        // 结果一个 232x20 的真实目标因左边界超出窗 100px 被判为不可点，
        // Click 直接返回 RejectedOutsideWindow —— 而它的中心点稳在窗内，
        // WindowFromPoint 也确实命中该窗口。点击只作用在中心，所以按中心判。
        var raw = new[] { new RawDetection(new PhysicalRect(2560 + 2048 - 30, 100, 40, 20), "button") };

        var t = ComputerSnapshotAnnotator.Annotate(raw, Window).Single();

        Assert.IsTrue(t.InsideWindow, "中心点在窗内 → 可点；整块越界不决定可点性");
    }

    [TestMethod]
    public void Element_whose_center_lies_outside_is_not_clickable()
    {
        // X=4603,W=40 → 中心 4623 > 窗右界 4608，中心本身就在窗外
        var raw = new[] { new RawDetection(new PhysicalRect(2560 + 2048 - 5, 100, 40, 20), "button") };

        var t = ComputerSnapshotAnnotator.Annotate(raw, Window).Single();

        Assert.IsFalse(t.InsideWindow, "中心点在窗外 → 不可点");
    }

    [TestMethod]
    public void Tiny_detections_are_dropped_as_noise()
    {
        var raw = new[]
        {
            new RawDetection(new PhysicalRect(100, 100, 3, 3), "noise"),
            new RawDetection(new PhysicalRect(200, 200, 40, 20), "button"),
        };

        var targets = ComputerSnapshotAnnotator.Annotate(raw, Window);

        Assert.AreEqual(1, targets.Count);
        Assert.AreEqual(1, targets[0].Id, "丢弃后编号仍从 1 连续");
    }

    [TestMethod]
    public void Rendered_table_contains_coordinates_not_guesses()
    {
        var raw = new[] { new RawDetection(new PhysicalRect(2660, 100, 40, 20), "button") };
        var targets = ComputerSnapshotAnnotator.Annotate(raw, Window);

        var text = ComputerSnapshotAnnotator.Render(targets);

        StringAssert.Contains(text, "(2660,100,40,20)", "全局坐标必须出现在表里");
        StringAssert.Contains(text, "(100,100)", "窗口内坐标必须出现");
        StringAssert.Contains(text, "(2680,110)", "可点中心必须出现");
        StringAssert.Contains(text, "yes");
    }
}
