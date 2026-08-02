using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PuddingDesktop.Theming;

/// <summary>
/// Applies supported Windows 11 DWM backdrop and rounded-corner attributes.
/// Windows 10 keeps the normal translucent application background.
/// </summary>
public static class WindowsBackdropService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmWindowCornerRound = 2;
    private const int DwmSystemBackdropMainWindow = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    public static void Apply(Window window, bool isDarkMode)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (HwndSource.FromHwnd(handle) is { CompositionTarget: { } compositionTarget })
            compositionTarget.BackgroundColor = Colors.Transparent;

        var darkMode = isDarkMode ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

        var cornerPreference = DwmWindowCornerRound;
        DwmSetWindowAttribute(
            handle,
            DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var backdropType = DwmSystemBackdropMainWindow;
            DwmSetWindowAttribute(
                handle,
                DwmwaSystemBackdropType,
                ref backdropType,
                sizeof(int));
        }
    }
}
