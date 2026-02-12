using System;
using System.Runtime.InteropServices;

namespace PuddingAssistantDesktop.Heartbeat;

/// <summary>
/// Immutable snapshot of the desktop environment captured during each perception beat.
/// Provides context for autonomous behavior decisions without retaining references.
/// </summary>
public sealed record EnvironmentSnapshot
{
    /// <summary>Title of the currently focused window.</summary>
    public string ActiveWindowTitle { get; init; } = string.Empty;

    /// <summary>How long the user has been idle (no mouse/keyboard input).</summary>
    public TimeSpan UserIdleDuration { get; init; }

    /// <summary>Whether the pudding body is resting on a surface.</summary>
    public bool IsGrounded { get; init; }

    /// <summary>Whether the pudding is currently being dragged.</summary>
    public bool IsDragging { get; init; }

    /// <summary>Current system time when the snapshot was taken.</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>
    /// Captures a fresh environment snapshot from the current system state.
    /// </summary>
    public static EnvironmentSnapshot Capture(bool isGrounded, bool isDragging)
    {
        return new EnvironmentSnapshot
        {
            ActiveWindowTitle = GetForegroundWindowTitle(),
            UserIdleDuration = GetUserIdleTime(),
            IsGrounded = isGrounded,
            IsDragging = isDragging,
            Timestamp = DateTime.Now
        };
    }

    // ── Win32: Foreground window title ──

    private static string GetForegroundWindowTitle()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return string.Empty;

        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return string.Empty;

        var length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;

        var buffer = new char[length + 1];
        GetWindowText(hWnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    // ── Win32: User idle time ──

    private static TimeSpan GetUserIdleTime()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return TimeSpan.Zero;

        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        var idleMs = (uint)Environment.TickCount - info.dwTime;
        return TimeSpan.FromMilliseconds(idleMs);
    }

    // ── P/Invoke declarations ──

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}
