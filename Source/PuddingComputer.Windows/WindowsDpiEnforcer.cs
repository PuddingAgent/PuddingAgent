using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PuddingComputer.Windows;

/// <summary>
/// 进程级 DPI 感知引导。
/// <para>
/// **存在的理由**（2026-11-03 实测，这是 Computer Use 全链路错误的最终根因）：
/// </para>
/// <para>
/// 默认的 .NET 控制台/服务进程是 <c>DPI_AWARENESS = Unaware</c>，而 Chromium / Electron
/// 是 <c>Per-Monitor V2</c>。两套坐标系不同，Windows 会对 Unaware 进程**虚拟化**所有
/// 窗口与光标 API 的返回值，于是：
/// </para>
/// <list type="bullet">
/// <item><c>GetWindowRect</c> 返回缩放后的逻辑坐标</item>
/// <item><c>ClientToScreen</c> 返回完全错误的值</item>
/// <item>用 <c>BitBlt</c> 截到的是物理像素，与上面的坐标对不上</item>
/// <item><c>SetCursorPos</c> / <c>SendInput</c> 报告成功，但命中位置偏移</item>
/// </list>
/// <para>
/// 实测同一窗口在两种模式下的读数：
/// </para>
/// <code>
/// Unaware:        GetWindowRect = (2560,0)   946x1155 · ClientToScreen = (5706,85)  · 虚拟屏 4608x1440
/// PerMonitor V2:  GetWindowRect = (3292,106) 1600x1038 · ClientToScreen = (3292,106) · 虚拟屏 5120x1440
/// </code>
/// <para>
/// 后果：按 Unaware 的坐标去点击，每一次都偏几百像素 —— 表现为"点了没反应""点到了隔壁控件"。
/// </para>
/// <para>
/// 因此本类型用 <see cref="ModuleInitializerAttribute"/> 在**程序集加载时**立即声明感知级别，
/// 保证下游任何调用方拿到的都是真实物理坐标，无需各自记得声明。
/// </para>
/// </summary>
public static class WindowsDpiEnforcer
{
    /// <summary>Per-Monitor V2 的上下文句柄（微软约定为 -4）。</summary>
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    /// <summary>当前进程是否已成功声明为 DPI 感知。</summary>
    public static bool IsDeclared { get; private set; }

    /// <summary>声明失败时的 Win32 错误码（0 表示没失败）。</summary>
    public static int LastError { get; private set; }

    /// <summary>
    /// 由运行时在程序集加载时自动调用，不需要手动触发。
    /// </summary>
    /// <remarks>
    /// CA2255 建议库不要用 ModuleInitializer，但这里是一个**有意的例外**：
    /// DPI 感知必须在任何窗口 / 截图 / 光标调用之前声明，晚一步就永久错位
    /// （Windows 不允许进程启动后再改级别）。把这一步交给调用方等于把
    /// 整条链路的正确性寄托在“记得”上 —— 实测已经因此错了一整晚。
    /// </remarks>
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        Declare();
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
}
