namespace PuddingHost.ScreenCapture;

/// <summary>
/// 屏幕坐标矩形（**物理像素**，虚拟桌面坐标系，原点在虚拟桌面左上角）。
/// 坐标一律整数像素：屏幕采集没有「CSS 像素」这种中间单位，
/// 一旦引入缩放换算，Region 与截图结果就会错位。
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public long Area => (long)Width * Height;

    /// <summary>完全包含（含边界相等）。</summary>
    public bool Contains(PixelRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>相交区域；不相交时返回空矩形（<see cref="IsEmpty"/> 为 true）。</summary>
    public PixelRect Intersect(PixelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= x || bottom <= y
            ? new PixelRect(x, y, 0, 0)
            : new PixelRect(x, y, right - x, bottom - y);
    }

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

/// <summary>一台显示器（<see cref="Index"/> 由枚举顺序决定，0 起）。</summary>
public sealed record ScreenDisplay(int Index, string DeviceName, PixelRect Bounds, bool IsPrimary);

/// <summary>
/// 一个可截图的顶层窗口。
///
/// <para>三个可见性字段各说各的事，**不可互相替代**（实测过混淆代价）：</para>
/// <list type="bullet">
/// <item><see cref="IsVisible"/> 是 <c>WS_VISIBLE</c> 样式位：窗口未被隐藏，但**不代表此刻在屏幕上看得见**。</item>
/// <item><see cref="IsMinimized"/> 为 true 时 <see cref="Bounds"/> 是 <c>-32000,-32000 160x28</c> 的
/// 屏外占位矩形，**不是窗口真实尺寸** —— 拿它采集只能得到一张碎片。</item>
/// <item><see cref="IsCloaked"/> 为 true 表示窗口被 DWM 隐藏（典型场景：在另一个虚拟桌面）。
/// 此时 <see cref="Bounds"/> 与渲染内容仍然有效，<c>PrintWindow</c> 通常能抓到窗口自身绘制结果，
/// 但屏幕位图里不会有它。</item>
/// </list>
/// </summary>
public sealed record ScreenWindow(
    nint Handle,
    string Title,
    string ProcessName,
    PixelRect Bounds,
    bool IsVisible,
    bool IsMinimized,
    bool IsCloaked);

/// <summary>一次采集得到的 BGRA 像素缓冲（top-down，stride 由后端给出）。</summary>
public sealed record CapturedScreenImage(int Width, int Height, byte[] Bgra, int Stride);

/// <summary>
/// 采集后端端口。之所以抽成接口：几何/预算/编码都是纯逻辑，**可以被单元测试覆盖**；
/// 只有真正碰 GDI 的这一层必须在有交互桌面的 Windows 上跑。
/// </summary>
public interface IScreenCaptureBackend
{
    IReadOnlyList<ScreenDisplay> GetDisplays();

    /// <summary>枚举可见顶层窗口（按面积降序，最多 <paramref name="maxWindows"/> 个）。</summary>
    IReadOnlyList<ScreenWindow> GetWindows(int maxWindows);

    /// <summary>按虚拟桌面坐标采集一个区域。</summary>
    CapturedScreenImage Capture(PixelRect region);

    /// <summary>
    /// 采集指定窗口。优先用 <c>PrintWindow</c>（可采集被遮挡窗口），失败时回退为
    /// 按窗口矩形从屏幕位图复制 —— 回退路径对遮挡内容会失真，因此结果里如实回报采集方式。
    /// </summary>
    CapturedScreenImage CaptureWindow(nint handle, PixelRect bounds);
}
