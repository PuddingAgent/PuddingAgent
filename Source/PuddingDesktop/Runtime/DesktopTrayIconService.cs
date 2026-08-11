using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace PuddingDesktop.Runtime;

/// <summary>
/// Pure WPF tray integration backed by Shell_NotifyIcon; no WinForms host is required.
/// </summary>
public sealed class DesktopTrayIconService : IDisposable
{
    private const uint NotifyIconId = 1;
    private const uint CallbackMessage = 0x8000 + 0x51;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const int WmContextMenu = 0x007B;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;
    private static readonly IntPtr IdiApplication = new(32512);

    private Window? _window;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private uint _taskbarCreatedMessage;
    private string _toolTip = "Pudding Desktop";
    private int _disposeState;

    public event EventHandler? OpenRequested;
    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? RestartRequested;
    public event EventHandler? ExitRequested;

    public void Initialize(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (_source is not null)
            return;

        _window = window;
        _windowHandle = new WindowInteropHelper(window).Handle;
        if (_windowHandle == IntPtr.Zero)
            throw new InvalidOperationException("窗口句柄尚未创建，无法初始化系统托盘。");

        _source = HwndSource.FromHwnd(_windowHandle)
            ?? throw new InvalidOperationException("无法连接窗口消息源。");
        _source.AddHook(WindowProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _iconHandle = LoadIcon(IntPtr.Zero, IdiApplication);
        AddIcon();
    }

    public void UpdateToolTip(string text)
    {
        _toolTip = string.IsNullOrWhiteSpace(text) ? "Pudding Desktop" : text.Trim();
        if (_windowHandle == IntPtr.Zero)
            return;

        var data = CreateData(NifTip);
        _ = ShellNotifyIcon(NimModify, ref data);
    }

    private void AddIcon()
    {
        var data = CreateData(NifMessage | NifIcon | NifTip);
        if (!ShellNotifyIcon(NimAdd, ref data))
            return;

        data.TimeoutOrVersion = NotifyIconVersion4;
        _ = ShellNotifyIcon(NimSetVersion, ref data);
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = NotifyIconId,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        Tip = _toolTip.Length <= 127 ? _toolTip : _toolTip[..127],
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if ((uint)message == _taskbarCreatedMessage)
        {
            AddIcon();
            return IntPtr.Zero;
        }

        if ((uint)message != CallbackMessage)
            return IntPtr.Zero;

        var mouseMessage = unchecked((int)((long)lParam & 0xFFFF));
        switch (mouseMessage)
        {
            case WmLeftButtonUp:
            case WmLeftButtonDoubleClick:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
            case WmRightButtonUp:
            case WmContextMenu:
                ShowContextMenu();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (_window is null || !GetCursorPos(out var point))
            return;

        _ = SetForegroundWindow(_windowHandle);
        var menu = new ContextMenu
        {
            Placement = PlacementMode.AbsolutePoint,
            HorizontalOffset = point.X,
            VerticalOffset = point.Y,
            // This popup is hosted by Explorer rather than the Desktop window.
            // Keep its palette self-contained: the application may be dark while
            // the native WPF popup still receives the light Windows menu surface.
            Background = TrayMenuBackgroundBrush,
            Foreground = TrayMenuForegroundBrush,
        };
        menu.Items.Add(CreateItem("打开 Pudding", () => OpenRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("启动 Core", () => StartRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem("停止 Core", () => StopRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem("重启 Core", () => RestartRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("退出 Pudding", () => ExitRequested?.Invoke(this, EventArgs.Empty)));
        menu.IsOpen = true;
    }

    private static MenuItem CreateItem(string header, Action action)
    {
        var item = new MenuItem
        {
            // A string header creates an implicit TextBlock whose application
            // style overrides the inherited MenuItem foreground in dark mode.
            // Use an explicit TextBlock so the light popup keeps dark text.
            Header = new TextBlock
            {
                Text = header,
                Foreground = TrayMenuForegroundBrush,
            },
            Background = TrayMenuBackgroundBrush,
            Foreground = TrayMenuForegroundBrush,
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static readonly System.Windows.Media.Brush TrayMenuBackgroundBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(249, 249, 249));

    private static readonly System.Windows.Media.Brush TrayMenuForegroundBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 31, 31));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        if (_windowHandle != IntPtr.Zero)
        {
            var data = CreateData(0);
            _ = ShellNotifyIcon(NimDelete, ref data);
        }

        _source?.RemoveHook(WindowProc);
        _source = null;
        _window = null;
        _windowHandle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    private static bool ShellNotifyIcon(uint message, ref NotifyIconData data)
        => Shell_NotifyIcon(message, ref data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
