using System.Runtime.InteropServices;
using System.Text;
using Pudding.Contracts.Computer;

namespace PuddingComputer.Windows;

/// <summary>
/// 一个顶层窗口的基本信息。
/// <para>
/// <see cref="Bounds"/> 是 <c>GetWindowRect</c> 给出的 Win32 窗口矩形；
/// <see cref="VisualBounds"/> 是**真实渲染范围** —— 两者可能显著不同，见该字段说明。
/// </para>
/// </summary>
public sealed record WindowInfo(
    nint Handle,
    string ClassName,
    string Title,
    PhysicalRect Bounds,
    int ProcessId,
    bool IsVisible,
    bool IsMinimized)
{
    /// <summary>
    /// 真实渲染边界：顶层窗口矩形与其所有渲染型子窗口矩形的并集。
    /// <para>
    /// **为什么需要这个**（2026-11-02 实测）：
    /// Chromium 会把页面内容渲染到 Win32 窗口矩形之外。实测 ChatGPT 主窗口：
    /// </para>
    /// <code>
    /// Chrome_WidgetWin_1       946 x 1155   ← GetWindowRect 报的
    /// Intermediate D3D Window 1229 x 1434   ← 实际渲染的
    /// </code>
    /// <para>
    /// 后果非常具体：输入框和发送键位于 y ≈ 1200~1330，**超出 1155**。
    /// 于是 <c>WindowFromPoint</c> 对整块区域返回 NULL，任何"在窗口矩形内"的可点性判定
    /// 都会把这些元素误判为不可点，而它们在屏幕上明明可见。
    /// </para>
    /// <para>
    /// 所以可点性应当以 <see cref="VisualBounds"/> 为准，而不是 <see cref="Bounds"/>。
    /// 注意这仍然只是"画面上属于该窗口"，不等于"该点一定能收到点击"——
    /// 后者仍需 <c>WindowFromPoint</c> 实测，两者要一起看。
    /// </para>
    /// </summary>
    public PhysicalRect VisualBounds { get; init; } = Bounds;

    /// <summary>渲染范围是否与 Win32 窗口矩形不一致（即存在溢出渲染）。</summary>
    public bool HasOverflowRendering =>
        VisualBounds.X != Bounds.X || VisualBounds.Y != Bounds.Y ||
        VisualBounds.Width != Bounds.Width || VisualBounds.Height != Bounds.Height;
}

/// <summary>
/// 窗口枚举与状态控制。
/// <para>
/// 这个类存在的理由是一个具体的坑：同一个 Electron 进程会开出**多个同名同类的窗口**
/// （实测 pid 81092 同时有 0xCF0FCC 和 0xE51328，标题都是 ChatGPT）。
/// 按标题"取第一个匹配"的实现会在它们之间**随机漂移**，产生"有时成功有时失败"的假象，
/// 而那种假象极容易被误判成"输入注入不可靠"。
/// </para>
/// <para>
/// 所以本类只提供**显式枚举**，把选择权交给调用方：要么按句柄，要么按几何，不许靠猜。
/// </para>
/// </summary>
public static class WindowsWindowManager
{
    /// <summary>枚举所有可见的顶层窗口。</summary>
    public static IReadOnlyList<WindowInfo> EnumerateTopLevel()
    {
        var results = new List<WindowInfo>();

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;

            var info = BuildInfo(handle);
            if (info is not null)
                results.Add(info);

            return true;
        }, 0);

        return results;
    }

    /// <summary>按句柄查窗口；句柄失效时返回 null。</summary>
    public static WindowInfo? Describe(nint handle) =>
        IsWindow(handle) ? BuildInfo(handle) : null;

    /// <summary>把窗口提到最前并激活。返回是否成功。</summary>
    public static bool BringToFront(nint handle)
    {
        if (!IsWindow(handle)) return false;

        ShowWindow(handle, SwRestore);
        return SetForegroundWindow(handle);
    }

    /// <summary>最小化。用于把遮挡目标窗口的弹窗挪开。</summary>
    public static bool Minimize(nint handle) => IsWindow(handle) && ShowWindow(handle, SwMinimize);

    /// <summary>查询某屏幕坐标处属于哪个窗口（不做任何推断）。</summary>
    public static nint WindowAt(ScreenPoint point)
    {
        var native = new NativePoint
        {
            X = point.X,
            Y = point.Y
        };
        return WindowFromPoint(native);
    }

    /// <summary>取窗口所属的顶层窗口（例如从渲染层子窗口回溯到主窗口）。</summary>
    public static nint RootOf(nint handle) => handle == 0 ? 0 : GetAncestor(handle, GaRoot);

    /// <summary>
    /// 计算真实渲染边界：顶层矩形 ∪ 所有"渲染型"子窗口矩形。
    /// <para>
    /// 只采纳渲染相关的子窗口类名（见 <see cref="IsRenderChild"/>），
    /// 否则会被工具栏、滚动条之类的零碎子窗口把边界撑歪。
    /// </para>
    /// </summary>
    private static PhysicalRect ComputeVisualBounds(nint topLevel, PhysicalRect bounds)
    {
        int left = bounds.X, top = bounds.Y;
        int right = bounds.X + bounds.Width, bottom = bounds.Y + bounds.Height;
        bool expanded = false;

        EnumChildWindows(topLevel, (child, _) =>
        {
            if (!IsWindowVisible(child))
                return true;

            var cls = GetClassName(child);
            if (!IsRenderChild(cls))
                return true;

            if (!GetWindowRect(child, out var r))
                return true;

            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0)
                return true;

            left = Math.Min(left, r.Left);
            top = Math.Min(top, r.Top);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
            expanded = true;

            return true;
        }, 0);

        return expanded
            ? new PhysicalRect(left, top, right - left, bottom - top)
            : bounds;
    }

    /// <summary>
    /// 该类名是否代表"承载渲染内容"的子窗口。
    /// 依据实测：Chromium/Electron 用 Intermediate D3D Window 做合成层，
    /// 用 Chrome_RenderWidgetHostHWND 做渲染宿主。
    /// </summary>
    private static bool IsRenderChild(string className) =>
        className.Contains("Intermediate D3D Window", StringComparison.Ordinal) ||
        className.Contains("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal) ||
        className.Contains("RenderWidgetHost", StringComparison.Ordinal);

    private static WindowInfo? BuildInfo(nint handle)
    {
        if (!GetWindowRect(handle, out var rect))
            return null;

        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
            return null;

        var bounds = new PhysicalRect(rect.Left, rect.Top, w, h);
        bool minimized = IsIconic(handle);

        return new WindowInfo(
            handle,
            GetClassName(handle),
            GetTitle(handle),
            bounds,
            GetProcessId(handle),
            IsWindowVisible(handle),
            minimized)
        {
            // 最小化时子窗口坐标是 (-32000,-32000) 这种哨兵值，参与并集会把边界算成 7000x6400 的荒唐数字。
            // 此时直接用窗口矩形，不做溢出渲染探测。
            VisualBounds = minimized ? bounds : ComputeVisualBounds(handle, bounds)
        };
    }

    private static string GetTitle(nint handle)
    {
        int length = GetWindowTextLengthW(handle);
        if (length <= 0) return string.Empty;

        var buffer = new StringBuilder(length + 1);
        GetWindowTextW(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetClassName(nint handle)
    {
        var buffer = new StringBuilder(256);
        GetClassNameW(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static int GetProcessId(nint handle)
    {
        GetWindowThreadProcessId(handle, out uint pid);
        return (int)pid;
    }

    private const int SwRestore = 9;
    private const int SwMinimize = 6;
    private const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(nint handle, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint handle, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint handle, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
}
