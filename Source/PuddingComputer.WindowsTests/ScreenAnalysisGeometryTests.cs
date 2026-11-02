using Pudding.Contracts.Computer;
using PuddingComputer.Automation;
using PuddingComputer.Windows;

namespace PuddingComputer.WindowsTests;

/// <summary>
/// <see cref="ScreenAnalysis.CreateGeometry"/> 与 <see cref="ScreenAnalysis.AuthorizedRoot"/>。
/// <para>
/// 这两个成员存在的理由是把「分析产物」和「点击」接起来：
/// 没有它们，调用方要自己拼图内尺寸、自己手算采集方式，而**坐标写错不会报错**。
/// </para>
/// </summary>
[TestClass]
public sealed class ScreenAnalysisGeometryTests
{
    /// <summary>
    /// 几何必须 1:1 映射：图内 (0,0) 对应屏幕的截图原点。
    /// <para>
    /// 这是整条链路的地基。如果这里错了，后面所有点击都会偏，而且不报错。
    /// </para>
    /// </summary>
    [TestMethod]
    public void CreateGeometry_MapsImageOriginToScreenOrigin()
    {
        using var capture = WindowsScreenCapturer.CaptureVirtualScreen();
        using var analysis = ComputerScreenAnalyzer.Analyze(capture, new PhysicalRect(0, 0, 800, 600));

        var geometry = analysis.CreateGeometry();
        var mapped = ComputerCoordinateMapper.Map(geometry, new ImagePoint(0, 0));

        Assert.IsTrue(mapped.IsOk);
        Assert.AreEqual(analysis.ScreenBounds.X, mapped.Point.X, "图内 X=0 应映射到截图左边界");
        Assert.AreEqual(analysis.ScreenBounds.Y, mapped.Point.Y, "图内 Y=0 应映射到截图上边界");
    }

    /// <summary>
    /// 图内尺寸必须等于截图尺寸，采集方式必须是 ScreenPixels。
    /// 换成 WindowRender 会让该几何永远不签发可点击坐标。
    /// </summary>
    [TestMethod]
    public void CreateGeometry_DescribesTheActualCapture()
    {
        using var capture = WindowsScreenCapturer.CaptureVirtualScreen();
        using var analysis = ComputerScreenAnalyzer.Analyze(capture, new PhysicalRect(0, 0, 800, 600));

        var geometry = analysis.CreateGeometry();

        Assert.AreEqual(capture.Bounds.Width, geometry.ImageWidth);
        Assert.AreEqual(capture.Bounds.Height, geometry.ImageHeight);
        Assert.AreEqual(ComputerCaptureMode.ScreenPixels, geometry.CaptureMode,
            "整屏 GDI 截图是可点击的采集方式");
        Assert.IsTrue(geometry.CoordinateInputAllowed);
        Assert.AreEqual(analysis.ScreenBounds.X, geometry.SourceRect.X);
        Assert.AreEqual(analysis.ScreenBounds.Y, geometry.SourceRect.Y);
    }

    /// <summary>
    /// 图内右下角必须映射到屏幕右下角 —— 与原点那条一起构成两端的锚点。
    /// </summary>
    [TestMethod]
    public void CreateGeometry_MapsFarCornerConsistently()
    {
        using var capture = WindowsScreenCapturer.CaptureVirtualScreen();
        using var analysis = ComputerScreenAnalyzer.Analyze(capture, new PhysicalRect(0, 0, 800, 600));

        var geometry = analysis.CreateGeometry();
        var mapped = ComputerCoordinateMapper.Map(
            geometry, new ImagePoint(geometry.ImageWidth - 1, geometry.ImageHeight - 1));

        Assert.IsTrue(mapped.IsOk);
        Assert.AreEqual(analysis.ScreenBounds.X + geometry.ImageWidth - 1, mapped.Point.X);
        Assert.AreEqual(analysis.ScreenBounds.Y + geometry.ImageHeight - 1, mapped.Point.Y);
    }

    /// <summary>
    /// 调用方自己指定窗口时，分析器无法断言"这就是授权窗口"，所以
    /// <see cref="ScreenAnalysis.AuthorizedRoot"/> 必须为 null ——
    /// 若给它一个猜出来的句柄，归属校验就成了摆设却看起来在工作。
    /// </summary>
    [TestMethod]
    public void AuthorizedRoot_IsNullWhenWindowWasCallerSupplied()
    {
        using var capture = WindowsScreenCapturer.CaptureVirtualScreen();
        using var analysis = ComputerScreenAnalyzer.Analyze(capture, new PhysicalRect(0, 0, 800, 600));

        Assert.IsNull(analysis.ResolvedWindow);
        Assert.IsNull(analysis.AuthorizedRoot,
            "没有解析出的窗口就不该有授权根，否则等于凭空信任");
    }
}
