using Pudding.Contracts.Computer;
using PuddingComputer.Automation;
using PuddingComputer.Windows;

namespace PuddingComputer.WindowsTests;

/// <summary>
/// <see cref="WindowsInputSimulator.ResolveClick"/> 的约束测试。
/// <para>
/// 锁定的核心只有一条：**图内坐标必须经过映射才能变成可点击坐标**。
/// 2026-11-03 之前 <c>Click</c> 直接 <c>ClickAt(target.Clickable)</c>，
/// 把图内坐标当屏幕坐标用，中间一次映射都没有 —— 那是"点击总是偏几百像素
/// 却从不报错"的根源。
/// </para>
/// <para>
/// 为什么编译期不够：签名强制传 geometry，只能拦住"忘了传"，
/// 拦不住"传了错的 geometry"或"拿到映射结果又用回原值"。
/// 所以必须用断言把映射的**结果**钉住。
/// </para>
/// </summary>
[TestClass]
public sealed class WindowsInputSimulatorClickTests
{
    /// <summary>与 ComputerCoordinateMapperTests 同一块采集区域：(2104,600,400,240)，图 400x240。</summary>
    private static ComputerImageGeometry Geometry(
        ComputerCaptureMode mode = ComputerCaptureMode.ScreenPixels,
        bool allowed = true) =>
        new(new PhysicalRect(2104, 600, 400, 240),
            400, 240,
            mode,
            CoordinateInputAllowed: allowed);

    /// <summary>图内 (200,120) 位于采集区中心，映射后应为屏幕 (2304,720)。</summary>
    private static AnnotatedTarget Target(int imageX = 200, int imageY = 120, bool insideWindow = true) =>
        new(
            Id: 1,
            Kind: "button",
            Global: new PhysicalRect(2104 + imageX, 600 + imageY, 40, 20),
            WindowLocal: new PhysicalRect(imageX, imageY, 40, 20),
            Clickable: new ImagePoint(imageX, imageY),
            InsideWindow: insideWindow);

    /// <summary>
    /// **核心用例**：解析出的点必须是映射后的屏幕坐标，而不是图内坐标本身。
    /// 如果把 <c>ResolveClick</c> 里的 Map 去掉，这条会立刻变红。
    /// </summary>
    [TestMethod]
    public void ResolvedPoint_IsMappedToScreen_NotTheRawImagePoint()
    {
        var resolution = WindowsInputSimulator.ResolveClick(Target(), Geometry(), allowOutsideWindow: false);

        Assert.IsTrue(resolution.IsOk);

        // 图内坐标是 (200,120)，屏幕坐标必须是 (2304,720)
        Assert.AreEqual(2304, resolution.Point.X, "2104 + 200 * 400 / 400");
        Assert.AreEqual(720, resolution.Point.Y, "600 + 120 * 240 / 240");

        Assert.AreNotEqual(200, resolution.Point.X, "拿到的不能还是图内 X");
        Assert.AreNotEqual(120, resolution.Point.Y, "拿到的不能还是图内 Y");
    }

    /// <summary>
    /// WindowRender 图不签发可点击坐标 —— 它绕过了遮挡关系。
    /// </summary>
    [TestMethod]
    public void WindowRenderGeometry_IsRejected_AndYieldsNoUsablePoint()
    {
        var resolution = WindowsInputSimulator.ResolveClick(
            Target(),
            Geometry(ComputerCaptureMode.WindowRender, allowed: false),
            allowOutsideWindow: false);

        Assert.IsFalse(resolution.IsOk);
        Assert.AreEqual(ClickOutcome.RejectedOffScreen, resolution.Failure);
    }

    /// <summary>
    /// 即使采集方式说"可点"，图内坐标越界也必须拒绝。
    /// </summary>
    [TestMethod]
    public void OutOfBoundsImagePoint_IsRejected()
    {
        var resolution = WindowsInputSimulator.ResolveClick(
            Target(imageX: 400), // 右边界开区间，400 已越界
            Geometry(),
            allowOutsideWindow: false);

        Assert.IsFalse(resolution.IsOk);
        Assert.AreEqual(ClickOutcome.RejectedOffScreen, resolution.Failure);
    }

    /// <summary>
    /// 目标被标记为窗口外（合成层）时，默认拒绝 —— 这类点击会穿透到桌面。
    /// </summary>
    [TestMethod]
    public void OutsideWindowTarget_IsRejectedByDefault()
    {
        var resolution = WindowsInputSimulator.ResolveClick(
            Target(insideWindow: false),
            Geometry(),
            allowOutsideWindow: false);

        Assert.IsFalse(resolution.IsOk);
        Assert.AreEqual(ClickOutcome.RejectedOutsideWindow, resolution.Failure);
    }

    /// <summary>
    /// 显式允许窗口外时，仍然必须先映射再放行 —— 放行不等于跳过映射。
    /// </summary>
    [TestMethod]
    public void OutsideWindowTarget_WhenAllowed_StillGetsMapped()
    {
        var resolution = WindowsInputSimulator.ResolveClick(
            Target(insideWindow: false),
            Geometry(),
            allowOutsideWindow: true);

        Assert.IsTrue(resolution.IsOk);
        Assert.AreEqual(2304, resolution.Point.X, "放行后依然要映射到屏幕坐标");
        Assert.AreEqual(720, resolution.Point.Y);
    }

    /// <summary>
    /// 失败时 <c>Point</c> 是 default（屏幕原点），所以调用方**只能看 IsOk**。
    /// 这条断言把"拿失败结果的 Point 去点"这个陷阱显式记下来。
    /// </summary>
    [TestMethod]
    public void FailedResolution_PointIsDefault_SoCallersMustCheckIsOk()
    {
        var resolution = WindowsInputSimulator.ResolveClick(
            Target(insideWindow: false),
            Geometry(),
            allowOutsideWindow: false);

        Assert.IsFalse(resolution.IsOk);
        Assert.AreEqual(0, resolution.Point.X);
        Assert.AreEqual(0, resolution.Point.Y);
    }

    /// <summary>
    /// 负数原点的采集区（左侧副屏）也要正确映射。
    /// </summary>
    [TestMethod]
    public void NegativeSourceOrigin_MapsToNegativeScreenCoordinate()
    {
        var geometry = new ComputerImageGeometry(
            new PhysicalRect(-1920, 0, 1920, 1080), 1920, 1080,
            ComputerCaptureMode.ScreenPixels, CoordinateInputAllowed: true);

        var target = new AnnotatedTarget(
            Id: 1, Kind: "button",
            Global: new PhysicalRect(-960, 540, 40, 20),
            WindowLocal: new PhysicalRect(960, 540, 40, 20),
            Clickable: new ImagePoint(960, 540),
            InsideWindow: true);

        var resolution = WindowsInputSimulator.ResolveClick(target, geometry, allowOutsideWindow: false);

        Assert.IsTrue(resolution.IsOk);
        Assert.AreEqual(-960, resolution.Point.X);
        Assert.AreEqual(540, resolution.Point.Y);
    }
}
