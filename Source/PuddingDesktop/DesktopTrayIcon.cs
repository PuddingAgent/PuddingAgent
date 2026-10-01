using System.Runtime.InteropServices;

namespace PuddingDesktop;

// Win32 tray adapter: launcher lifetime and close policy remain in the coordinator.
internal sealed class DesktopTrayIcon : IDisposable
{
    private readonly MainWindow _window;
    private readonly Action _exit;
    private readonly IntPtr _handle;
    private readonly SubclassProc _callback;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _icon;
    private bool _disposed;
    internal bool Available { get; private set; }
    internal DesktopTrayIcon(MainWindow window, Action exit)
    {
        _window = window; _exit = exit;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _callback = OnMessage;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = DesktopIcon.LoadTrayIcon();
        if (!SetWindowSubclass(_handle, _callback, 1, 0))
        {
            DesktopIcon.DestroyIcon(_icon);
            throw new InvalidOperationException("Cannot install tray callback.");
        }
        Add();
    }
    private Data Create() => new()
    {
        Size = (uint)Marshal.SizeOf<Data>(), Window = _handle, Id = 1, Flags = 7, Message = 0x8051,
        Icon = _icon, Tip = "Pudding · 打开工作台", Info = "", InfoTitle = ""
    };
    private void Add() { var data = Create(); Available = Shell_NotifyIcon(0, ref data); }
    private IntPtr OnMessage(IntPtr hwnd, uint message, nuint w, nint l, nuint id, nuint reference)
    {
        if (message == _taskbarCreated) Add();
        if (message == 0x8051)
        {
            var action = (int)l & 0xffff;
            if (action is 0x0202 or 0x0203) { _window.AppWindow.Show(); _window.Activate(); }
            if (action == 0x0205)
            {
                var menu = CreatePopupMenu();
                try
                {
                    AppendMenu(menu, 0, 1, "打开 Pudding"); AppendMenu(menu, 0, 2, "退出 Pudding");
                    GetCursorPos(out var point); SetForegroundWindow(hwnd);
                    var selected = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, hwnd, IntPtr.Zero);
                    if (selected == 1) { _window.AppWindow.Show(); _window.Activate(); }
                    if (selected == 2) _exit();
                }
                finally { DestroyMenu(menu); }
            }
        }
        // Windows session ending uses the same coordinated stop path.
        if (message == 0x0011) { _exit(); return new IntPtr(1); }
        return DefSubclassProc(hwnd, message, w, l);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Create(); Shell_NotifyIcon(2, ref data);
        RemoveWindowSubclass(_handle, _callback, 1); Available = false;
        DesktopIcon.DestroyIcon(_icon);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Data
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Message; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, nuint w, nint l, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, nuint w, nint l);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint command, ref Data data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
