using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PuddingDesktop;

internal static class DesktopIcon
{
    internal static string FilePath => Path.Combine(AppContext.BaseDirectory, "Assets", "Pudding.ico");

    // Load a private HICON at the system's small-icon size; the tray owns its lifetime.
    internal static IntPtr LoadTrayIcon()
    {
        var icon = LoadImage(IntPtr.Zero, FilePath, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
        if (icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load Pudding tray icon.");
        return icon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr icon);
}
