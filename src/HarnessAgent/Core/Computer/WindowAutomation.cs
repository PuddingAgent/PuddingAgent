using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace HarnessAgent.Core.Computer;

/// <summary>Windows UI Automation via user32.dll — no external dependencies.</summary>
public static class WindowAutomation
{
    #region P/Invoke

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
        string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;

    #endregion

    // ── Window Discovery ──

    /// <summary>Find a top-level window by title (partial match).</summary>
    public static IntPtr FindWindowByTitle(string title)
    {
        // Try exact match first
        var hwnd = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.ToString().Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                hwnd = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return hwnd;
    }

    /// <summary>Find a window by class name.</summary>
    public static IntPtr FindWindowByClass(string className) => FindWindow(className, null);

    /// <summary>Find a child window/control by class.</summary>
    public static IntPtr FindChildByClass(IntPtr parent, string className) =>
        FindWindowEx(parent, IntPtr.Zero, className, null);

    /// <summary>Get window title.</summary>
    public static string GetWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, 256);
        return sb.ToString();
    }

    /// <summary>Get window class name.</summary>
    public static string GetWindowClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, 256);
        return sb.ToString();
    }

    /// <summary>Get window bounding rectangle.</summary>
    public static Rectangle GetWindowRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Get current foreground window handle.</summary>
    public static IntPtr GetActiveWindow() => GetForegroundWindow();

    // ── Input ──

    /// <summary>Bring window to foreground.</summary>
    public static void Focus(IntPtr hwnd) => SetForegroundWindow(hwnd);

    /// <summary>Move cursor to screen coordinates and click.</summary>
    public static void Click(int x, int y, bool rightClick = false)
    {
        SetCursorPos(x, y);
        Thread.Sleep(10);
        var down = rightClick ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        var up = rightClick ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Double-click at screen coordinates.</summary>
    public static void DoubleClick(int x, int y)
    {
        Click(x, y);
        Thread.Sleep(50);
        Click(x, y);
    }

    /// <summary>Type a string of text character by character.</summary>
    public static void TypeText(string text, int delayMs = 5)
    {
        foreach (var ch in text)
        {
            var vk = (byte)VkKeyScan(ch);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            Thread.Sleep(delayMs);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(delayMs);
        }
    }

    /// <summary>Press and release a single key by virtual key code.</summary>
    public static void PressKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        Thread.Sleep(5);
        keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>Press Enter.</summary>
    public static void PressEnter() => PressKey(0x0D);

    /// <summary>Press Tab.</summary>
    public static void PressTab() => PressKey(0x09);

    // ── Convenience ──

    /// <summary>Focus a window by title and click at an offset from its top-left.</summary>
    public static void ClickInWindow(string title, int offsetX, int offsetY)
    {
        var hwnd = FindWindowByTitle(title);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"Window not found: '{title}'");

        var rect = GetWindowRect(hwnd);
        Focus(hwnd);
        Thread.Sleep(100);
        Click(rect.Left + offsetX, rect.Top + offsetY);
    }

    /// <summary>Focus a window by title and type text into it.</summary>
    public static void TypeInWindow(string title, int offsetX, int offsetY, string text)
    {
        var hwnd = FindWindowByTitle(title);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"Window not found: '{title}'");

        var rect = GetWindowRect(hwnd);
        Focus(hwnd);
        Thread.Sleep(100);
        Click(rect.Left + offsetX, rect.Top + offsetY);
        Thread.Sleep(50);
        TypeText(text);
    }

    // ── EnumWindows callback ──

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
}
