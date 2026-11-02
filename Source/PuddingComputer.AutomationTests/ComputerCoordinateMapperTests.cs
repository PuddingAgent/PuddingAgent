using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.AutomationTests;

/// <summary>
/// 坐标映射（设计 §7.1）。断言用的数字来自 CU0 探针 #2 的真实采集：
/// 虚拟屏 4608x1440，截取矩形 (2104,600,400,240)，图 400x240，图内 x=200 → 屏幕 x=2304。
/// <para>
/// 这条链路一旦算错，表现是「点到了别的地方」而不是报错，所以必须用纯函数逐格钉住。
/// </para>
/// </summary>
[TestClass]
public sealed class ComputerCoordinateMapperTests
{
    /// <summary>探针 #2 实测的那块采集矩形。</summary>
    private static ComputerImageGeometry ScreenPixels(int imageWidth, int imageHeight) =>
        new(new PhysicalRect(2104, 600, 400, 240),
            imageWidth, imageHeight,
            ComputerCaptureMode.ScreenPixels,
            CoordinateInputAllowed: true);

    [TestMethod]
    public void One_to_one_mapping_matches_the_measured_probe()
    {
        var geometry = ScreenPixels(400, 240);

        var result = ComputerCoordinateMapper.Map(geometry, new ImagePoint(200, 120));

        Assert.IsTrue(result.IsOk);
        Assert.AreEqual(2304, result.X, "2104 + 200 * 400 / 400 = 2304（探针 #2 实测值）");
        Assert.AreEqual(720, result.Y, "600 + 120 * 240 / 240 = 720");
    }

    [TestMethod]
    public void Downscaled_image_maps_proportionally()
    {
        // 图 200x120（半尺寸）覆盖同一块 400x240 区域：图内 (100,60) 应落在区域中心
        var geometry = ScreenPixels(200, 120);

        var result = ComputerCoordinateMapper.Map(geometry, new ImagePoint(100, 60));

        Assert.IsTrue(result.IsOk);
        Assert.AreEqual(2304, result.X);
        Assert.AreEqual(720, result.Y);
    }

    [TestMethod]
    public void Upscaled_image_maps_proportionally()
    {
        // 图 800x480（两倍）覆盖同一区域：图内 (400,240) 同样落在中心
        var geometry = ScreenPixels(800, 480);

        var result = ComputerCoordinateMapper.Map(geometry, new ImagePoint(400, 240));

        Assert.IsTrue(result.IsOk);
        Assert.AreEqual(2304, result.X);
        Assert.AreEqual(720, result.Y);
    }

    [TestMethod]
    public void Window_render_images_never_yield_clickable_coordinates()
    {
        var geometry = new ComputerImageGeometry(
            new PhysicalRect(2104, 600, 400, 240), 400, 240,
            ComputerCaptureMode.WindowRender,
            CoordinateInputAllowed: false);

        var result = ComputerCoordinateMapper.Map(geometry, new ImagePoint(200, 120));

        Assert.AreEqual(CoordinateMapStatus.CoordinateInputNotAllowed, result.Status,
            "WindowRender 绕过了遮挡关系，按它落点等于假设窗口在最前");
        Assert.IsFalse(result.IsOk);
    }

    [TestMethod]
    public void Capture_mode_wins_even_if_the_flag_lies()
    {
        // 即使调用方把 CoordinateInputAllowed 写成 true，WindowRender 依然不许出坐标
        var geometry = new ComputerImageGeometry(
            new PhysicalRect(0, 0, 100, 100), 100, 100,
            ComputerCaptureMode.WindowRender,
            CoordinateInputAllowed: true);

        var result = ComputerCoordinateMapper.Map(geometry, new ImagePoint(10, 10));

        Assert.AreEqual(CoordinateMapStatus.CoordinateInputNotAllowed, result.Status,
            "采集方式比调用方声明的标志更权威");
    }

    [TestMethod]
    public void Out_of_range_image_points_are_rejected()
    {
        var geometry = ScreenPixels(400, 240);

        Assert.AreEqual(CoordinateMapStatus.OutOfBounds,
            ComputerCoordinateMapper.Map(geometry, new ImagePoint(-1, 0)).Status);
        Assert.AreEqual(CoordinateMapStatus.OutOfBounds,
            ComputerCoordinateMapper.Map(geometry, new ImagePoint(400, 0)).Status,
            "右边界是开区间：400 已经越界");
        Assert.AreEqual(CoordinateMapStatus.OutOfBounds,
            ComputerCoordinateMapper.Map(geometry, new ImagePoint(0, 240)).Status);
        Assert.AreEqual(CoordinateMapStatus.Ok,
            ComputerCoordinateMapper.Map(geometry, new ImagePoint(399, 239)).Status);
    }

    [TestMethod]
    public void Negative_source_rect_origin_is_supported()
    {
        // 左侧副屏时 SourceRect.X 为负，映射结果也应为负
        var geometry = new ComputerImageGeometry(
            new PhysicalRect(-1920, 0, 1920, 1080), 1920, 1080,
            ComputerCaptureMode.ScreenPixels, CoordinateInputAllowed: true);

        var result = ComputerCoordinateMapper.Map(geometry, new ImagePoint(960, 540));

        Assert.IsTrue(result.IsOk);
        Assert.AreEqual(-960, result.X);
        Assert.AreEqual(540, result.Y);
    }

    [TestMethod]
    public void Zero_sized_image_is_rejected_rather_than_dividing_by_zero()
    {
        var geometry = new ComputerImageGeometry(
            new PhysicalRect(0, 0, 100, 100), 0, 0,
            ComputerCaptureMode.ScreenPixels, CoordinateInputAllowed: true);

        Assert.AreEqual(CoordinateMapStatus.OutOfBounds,
            ComputerCoordinateMapper.Map(geometry, new ImagePoint(0, 0)).Status);
    }
}
