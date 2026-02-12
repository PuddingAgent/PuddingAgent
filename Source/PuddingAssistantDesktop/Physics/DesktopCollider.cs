using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PuddingAssistantDesktop.Physics;

/// <summary>
/// Represents a rectangular platform in screen coordinates
/// that the pudding can land on (window title bar, taskbar, screen edge).
/// </summary>
internal readonly record struct Platform(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>
/// Enumerates visible desktop windows and the taskbar to produce collision
/// platforms for the physics engine. Uses Win32 API on Windows;
/// returns only screen-bottom fallback on other platforms.
/// </summary>
internal static class DesktopCollider
{
    /// <summary>Thickness of the collision surface on top of each window.</summary>
    private const double PlatformThickness = 6.0;

    /// <summary>Minimum window width to be considered a valid platform.</summary>
    private const int MinWindowWidth = 80;

    /// <summary>Minimum window height to be considered a valid platform.</summary>
    private const int MinWindowHeight = 40;

    /// <summary>
    /// Collects all visible window top edges as platforms, plus a bottom-edge
    /// fallback spanning the full virtual desktop width at the given bottom Y.
    /// </summary>
    /// <param name="virtualLeft">Leftmost X across all screens (can be negative).</param>
    /// <param name="virtualTop">Topmost Y across all screens (can be negative).</param>
    /// <param name="virtualRight">Rightmost X across all screens.</param>
    /// <param name="virtualBottom">Bottommost Y (working area) across all screens.</param>
    /// <param name="excludeWindowHandle">Handle of the spirit window itself (to exclude).</param>
    public static List<Platform> EnumeratePlatforms(
        double virtualLeft, double virtualTop,
        double virtualRight, double virtualBottom,
        IntPtr excludeWindowHandle)
    {
        var platforms = new List<Platform>(32);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            EnumerateWindowPlatforms(platforms, excludeWindowHandle);
        }

        // Bottom edge spanning all screens as ultimate fallback (lands above taskbar)
        platforms.Add(new Platform(
            virtualLeft,
            virtualBottom - PlatformThickness,
            virtualRight,
            virtualBottom));

        return platforms;
    }

    private static void EnumerateWindowPlatforms(List<Platform> platforms, IntPtr excludeHandle)
    {
        var tuple = (platforms, excludeHandle);
        var gcHandle = GCHandle.Alloc(tuple);
        try
        {
            EnumWindows(static (hWnd, lParam) =>
            {
                var (list, exclude) = ((List<Platform>, IntPtr))GCHandle.FromIntPtr(lParam).Target!;

                if (hWnd == exclude) return true;
                if (!IsWindowVisible(hWnd)) return true;
                if (IsIconic(hWnd)) return true; // minimized

                if (GetWindowRect(hWnd, out var rect))
                {
                    var w = rect.Right - rect.Left;
                    var h = rect.Bottom - rect.Top;

                    // Filter out tiny windows and full-screen overlays
                    // Allow negative rect.Top for windows on monitors above primary
                    if (w >= MinWindowWidth && h >= MinWindowHeight)
                    {
                        // Use the top edge of the window as a landing platform
                        list.Add(new Platform(
                            rect.Left,
                            rect.Top,
                            rect.Right,
                            rect.Top + PlatformThickness));
                    }
                }

                return true; // continue enumeration
            }, GCHandle.ToIntPtr(gcHandle));
        }
        finally
        {
            gcHandle.Free();
        }
    }

    // ── Win32 Interop ──

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
