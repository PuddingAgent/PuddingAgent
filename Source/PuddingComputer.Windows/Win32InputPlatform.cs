using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Pudding.Contracts.Computer;

namespace PuddingComputer.Windows;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDesktopInput
{
    public uint Type;
    public InputUnion Data;
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MouseData Mouse;
        [FieldOffset(0)] public KeyData Key;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseData { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyData { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    public static NativeDesktopInput Mouse(uint flags) => new() { Data = new() { Mouse = new() { Flags = flags } } };
    public static NativeDesktopInput Key(ushort key, bool up) => new() {
        Type = 1, Data = new() { Key = new() { VirtualKey = key,
            Flags = (up ? 2u : 0) | (key is >= 0x21 and <= 0x2E or 0x5B ? 1u : 0) } } };
    public static NativeDesktopInput Unicode(char value, bool up) => new() {
        Type = 1, Data = new() { Key = new() { Scan = value, Flags = 4u | (up ? 2u : 0) } } };
    public readonly bool IsDown => Type == 1 ? (Data.Key.Flags & 2) == 0 : (Data.Mouse.Flags & (2 | 8)) != 0;
    public readonly NativeDesktopInput Release()
    {
        var result = this;
        if (Type == 1) result.Data.Key.Flags |= 2;
        else result.Data.Mouse.Flags = (Data.Mouse.Flags & 2) != 0 ? 4u : 16u;
        return result;
    }
}

internal sealed class Win32InputPlatform : IWindowsInputPlatform
{
    public IDisposable EnterCoordinates() => WindowsDpiEnforcer.EnterPhysicalCoordinates();
    public DesktopWindowState Inspect(long handle)
    {
        var hwnd = (nint)handle;
        if (!IsWindow(hwnd) || GetAncestor(hwnd, 2) != hwnd) throw new ArgumentException("A live top-level window handle is required.");
        var thread = GetWindowThreadProcessId(hwnd, out var pid);
        using var process = Process.GetProcessById((int)pid);
        var gui = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        if (!GetGUIThreadInfo(thread, ref gui)) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetGUIThreadInfo failed.");
        if (!GetWindowRect(hwnd, out var bounds) || !GetClientRect(hwnd, out var client)) throw new Win32Exception("Window geometry unavailable.");
        var origin = new Point();
        if (!ClientToScreen(hwnd, ref origin)) throw new Win32Exception("ClientToScreen failed.");
        return new(handle, (int)pid, process.StartTime.ToUniversalTime().Ticks, thread, bounds.ToRect(),
            new(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top), GetDpiForWindow(hwnd),
            IsWindowVisible(hwnd), IsIconic(hwnd), Root(GetForegroundWindow()), gui.Active, gui.Focus, Root(gui.Focus),
            gui.Caret, gui.Capture, gui.Flags);
    }

    public void Activate(long handle, bool repairNativeFocus)
    {
        var hwnd = (nint)handle;
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);
        SetForegroundWindow(hwnd);
        for (var i = 0; i < 10 && Root(GetForegroundWindow()) != handle; i++) Thread.Sleep(30);
        // Foreground restrictions are not bypassed. Do not focus the dummy Chromium
        // child, send WM_SETFOCUS/WM_ACTIVATE, or change TOPMOST to fake activation.
        if (!repairNativeFocus || Root(GetForegroundWindow()) != handle || Inspect(handle).HasNativeFocus) return;
        var current = GetCurrentThreadId();
        var target = GetWindowThreadProcessId(hwnd, out _);
        var attached = false;
        try
        {
            if (current != target)
            {
                attached = AttachThreadInput(current, target, true);
                if (!attached) throw new Win32Exception(Marshal.GetLastWin32Error(), "AttachThreadInput failed.");
            }
            SetFocus(hwnd);
        }
        finally { if (attached) AttachThreadInput(current, target, false); }
    }

    public ScreenPoint Cursor()
    {
        if (!GetCursorPos(out var p)) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetCursorPos failed.");
        return new(p.X, p.Y);
    }
    public bool Move(ScreenPoint point) => SetCursorPos(point.X, point.Y);
    public long HitRoot(ScreenPoint point) => Root(WindowFromPoint(new() { X = point.X, Y = point.Y }));
    public bool OnMonitor(ScreenPoint point) => WindowsMonitorEnumerator.IsOnRealMonitor(point);
    public bool HasPressedModifiers() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);
    public (int Submitted, int? Error) Send(NativeDesktopInput[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeDesktopInput>());
        return ((int)sent, sent == inputs.Length ? null : Marshal.GetLastWin32Error());
    }
    private static long Root(nint hwnd) => hwnd == 0 ? 0 : GetAncestor(hwnd, 2).ToInt64();
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly PhysicalRect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeDesktopInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int show);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint current, uint target, bool attach);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetFocus(nint hwnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
