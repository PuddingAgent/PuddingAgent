using System.Runtime.InteropServices;

namespace PuddingComputer.Windows;

/// <summary>Explicit process opt-in and scoped physical coordinates; loading this library never changes host DPI.</summary>
public static class WindowsDpiEnforcer
{
    /// <summary>Per-Monitor V2 的上下文句柄（微软约定为 -4）。</summary>
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    /// <summary>当前进程是否已成功声明为 DPI 感知。</summary>
    public static bool IsDeclared { get; private set; }

    /// <summary>声明失败时的 Win32 错误码（0 表示没失败）。</summary>
    public static int LastError { get; private set; }



    /// <summary>
    /// 声明 Per-Monitor V2 DPI 感知；失败时回退到 System aware，再失败则保持原状。
    /// <para>
    /// 只有在进程尚未声明过感知级别时才能成功 —— Windows 不允许已启动的进程改级别，
    /// 所以这一步必须在任何窗口 / 截图 / 光标调用之前完成。
    /// </para>
    /// </summary>
    public static void Declare()
    {
        if (IsDeclared)
            return;

        if (SetProcessDpiAwarenessContext(PerMonitorAwareV2))
        {
            IsDeclared = true;
            return;
        }

        LastError = Marshal.GetLastWin32Error();

        // 回退：老的 API，效果等同 System aware
        if (SetProcessDPIAware())
            IsDeclared = true;
    }

    /// <summary>
    /// 读取当前线程实际生效的感知级别，用于自检。
    /// 0 = Unaware，1 = System aware，2 = Per-Monitor aware。
    /// </summary>
    public static int CurrentAwareness()
        => GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());

    /// <summary>Physical-coordinate scope. Keep it on one thread; never await inside it.</summary>
    public static IDisposable EnterPhysicalCoordinates()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Computer input requires Windows.");
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        if (previous == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot enter physical coordinate space.");
        return new DpiScope(previous);
    }

    private sealed class DpiScope(IntPtr previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (!_disposed) { SetThreadDpiAwarenessContext(previous); _disposed = true; }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
}
