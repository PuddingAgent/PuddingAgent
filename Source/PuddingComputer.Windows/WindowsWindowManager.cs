using System.Runtime.InteropServices;
using System.Text;
using Pudding.Contracts.Computer;

namespace PuddingComputer.Windows;

/// <summary>一个顶层窗口的基本信息。</summary>
public sealed record WindowInfo(
    nint Handle,
    string ClassName,
    string Title,
    PhysicalRect Bounds,
    int ProcessId,
    bool IsVisible,
    bool IsMinimized);

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

            var bounds = GetBounds(handle);
            if (bounds is null || bounds.Value.Width <= 0 || bounds.Value.Height <= 0)
                return true;

            results.Add(new WindowInfo(
                handle,
                GetClassName(handle),
                GetTitle(handle),
                bounds.Value,
                GetProcessId(handle),
                IsVisible: true,
                IsMinimized: IsIconic(handle)));

            return true;
        }, 0);

        return results;
    }

    /// <summary>按句柄查窗口；句柄失效时返回 null。</summary>
    public static WindowInfo? Describe(nint handle)
    {
        if (!IsWindow(handle)) return null;

        var bounds = GetBounds(handle);
        if (bounds is null) return null;

        return new WindowInfo(
            handle,
            GetClassName(handle),
            GetTitle(handle),
            bounds.Value,
            GetProcessId(handle),
            IsWindowVisible(handle),
            IsIconic(handle));
    }

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
    public static nint WindowAt(ImagePoint point)
    {
        var native = new NativePoint
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y)
        };
        return WindowFromPoint(native);
    }

    /// <summary>取窗口所属的顶层窗口（例如从渲染层子窗口回溯到主窗口）。</summary>
    public static nint RootOf(nint handle) => handle == 0 ? 0 : GetAncestor(handle, GaRoot);

    private static PhysicalRect? GetBounds(nint handle)
    {
        if (!GetWindowRect(handle, out var rect)) return null;
        return new PhysicalRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
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
