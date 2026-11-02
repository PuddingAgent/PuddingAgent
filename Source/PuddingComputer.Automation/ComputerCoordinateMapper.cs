using Pudding.Contracts.Computer;

namespace PuddingComputer.Automation;

/// <summary>坐标映射的三种结局。刻意不让"失败"和"算出来是 0"混在一起。</summary>
public enum CoordinateMapStatus
{
    Ok,

    /// <summary>该图不签发可点击坐标（<see cref="ComputerCaptureMode.WindowRender"/>）。</summary>
    CoordinateInputNotAllowed,

    /// <summary>图内坐标越界。</summary>
    OutOfBounds,
}

/// <summary>
/// 坐标映射结果。
/// <para>
/// 成功时携带 <see cref="ScreenPoint"/>（而不是裸 int）—— 这是刻意设计的类型壁垒：
/// 调用方拿到屏幕坐标后只能传给接受 <see cref="ScreenPoint"/> 的 API，
/// 想把它当 <see cref="ImagePoint"/> 用会直接编译失败。
/// </para>
/// </summary>
public readonly record struct CoordinateMapResult(CoordinateMapStatus Status, ScreenPoint Point)
{
    public bool IsOk => Status == CoordinateMapStatus.Ok;

    /// <summary>成功时的屏幕 X；失败时无意义，调用前必须判 <see cref="IsOk"/>。</summary>
    public int X => Point.X;

    /// <summary>成功时的屏幕 Y；失败时无意义，调用前必须判 <see cref="IsOk"/>。</summary>
    public int Y => Point.Y;
}

/// <summary>
/// 图像坐标 → 屏幕物理坐标（设计 §7.1）。
/// <para>
/// 公式：<c>screenX = SourceRect.X + imageX * SourceRect.Width / ImageWidth</c>，Y 同理。
/// 两条前置检查写在计算之前：**先判 <c>CoordinateInputAllowed</c>，再判图内边界**。
/// 顺序不能反 —— 对 WindowRender 图先算再说"不许用"，等于已经用错了坐标系。
/// </para>
/// <para>
/// 为什么单独成函数：坐标算错的表现是"点到了别的地方"，而它不会报错。
/// 这类错误在真实桌面上极难复现（窗口一动就查不下去），只能靠纯函数逐格断言。
/// </para>
/// </summary>
public static class ComputerCoordinateMapper
{
    /// <summary>
    /// 把图内点映射到屏幕物理坐标。
    /// <para>
    /// 舍入用 <see cref="MidpointRounding.AwayFromZero"/>：取整规则必须唯一，
    /// 否则同一张图在两次调用里可能落到相邻像素上，点击断言会变成间歇性失败。
    /// </para>
    /// </summary>
    public static CoordinateMapResult Map(ComputerImageGeometry geometry, ImagePoint point)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        if (!geometry.CoordinateInputAllowed || geometry.CaptureMode != ComputerCaptureMode.ScreenPixels)
            return new CoordinateMapResult(CoordinateMapStatus.CoordinateInputNotAllowed, default);

        if (geometry.ImageWidth <= 0 || geometry.ImageHeight <= 0)
            return new CoordinateMapResult(CoordinateMapStatus.OutOfBounds, default);

        // 图像内部原点在左上角，合法区间是 [0, ImageWidth)
        if (point.X < 0 || point.X >= geometry.ImageWidth ||
            point.Y < 0 || point.Y >= geometry.ImageHeight)
            return new CoordinateMapResult(CoordinateMapStatus.OutOfBounds, default);

        var rect = geometry.SourceRect;
        var screenX = rect.X + point.X * rect.Width / geometry.ImageWidth;
        var screenY = rect.Y + point.Y * rect.Height / geometry.ImageHeight;

        return new CoordinateMapResult(
            CoordinateMapStatus.Ok,
            new ScreenPoint(
                (int)Math.Round(screenX, MidpointRounding.AwayFromZero),
                (int)Math.Round(screenY, MidpointRounding.AwayFromZero)));
    }
}
