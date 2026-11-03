using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using POINT = ComputerUseFocusProbe.Native.POINT;
using INPUT = ComputerUseFocusProbe.Native.INPUT;
using GUITHREADINFO = ComputerUseFocusProbe.Native.GUITHREADINFO;
using WNDCLASS = ComputerUseFocusProbe.Native.WNDCLASS;

namespace ComputerUseFocusProbe;

/// <summary>
/// 焦点与输入的分层探针。
///
/// 目的：把「点击是否取得编辑焦点」和「文字是否真的进入控件」拆开验证，而不是笼统地说
/// 「输入不生效」。判据只用可观测的原生状态：前台窗口、目标 GUI 线程的焦点/插入符句柄、
/// 以及目标控件的文本长度。任何一项拿不到就记 unknown，不猜成功。
///
/// 用法：
///   --self                          自建一个带 EDIT 的窗口做正对照（取红用：通道坏了必须在这里先暴露）
///   --hwnd &lt;n&gt; --x &lt;cx&gt; --y &lt;cy&gt;     对既有窗口的客户区坐标做同一组实验
///   --out &lt;path&gt;                   结果同时落盘
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Native.SetProcessDPIAware();

        var outPath = ArgValue(args, "--out");
        var lines = new List<string>();

        if (args.Any(a => a == "--write"))
        {
            var writeHwnd = ParseLong(ArgValue(args, "--hwnd"));
            var text = ArgValue(args, "--text") ?? "probe";
            if (writeHwnd == 0)
            {
                Console.WriteLine("usage: --write --hwnd <n> --l .. --t .. --r .. --b .. --text <value>");
                return 2;
            }

            return A11yWrite.Run((nint)writeHwnd,
                ParseInt(ArgValue(args, "--l")), ParseInt(ArgValue(args, "--t")),
                ParseInt(ArgValue(args, "--r")), ParseInt(ArgValue(args, "--b")), text);
        }

        if (args.Any(a => a == "--a11y"))
        {
            var a11yHwnd = ParseLong(ArgValue(args, "--hwnd"));
            if (a11yHwnd == 0)
            {
                Console.WriteLine("usage: --a11y --hwnd <n> --l <screenL> --t <screenT> --r <screenR> --b <screenB>");
                return 2;
            }

            return A11y.Run((nint)a11yHwnd,
                ParseInt(ArgValue(args, "--l")), ParseInt(ArgValue(args, "--t")),
                ParseInt(ArgValue(args, "--r")), ParseInt(ArgValue(args, "--b")));
        }

        Target target;
        if (args.Any(a => a == "--self"))
        {
            target = SelfWindow.Start();
        }
        else
        {
            var hwnd = ParseLong(ArgValue(args, "--hwnd"));
            if (hwnd == 0)
            {
                Console.WriteLine("usage: --self | --hwnd <n> --x <cx> --y <cy> [--out path]");
                return 2;
            }

            target = new Target((nint)hwnd, ParseInt(ArgValue(args, "--x")), ParseInt(ArgValue(args, "--y")), SelfOwned: false);
        }

        try
        {
            Native.ShowWindow(target.Root, Native.SW_RESTORE);
            var brought = Native.SetForegroundWindow(target.Root);
            Thread.Sleep(400);
            lines.Add($"TARGET root=0x{target.Root:X} client=({target.ClientX},{target.ClientY}) self_owned={target.SelfOwned} brought_to_front={brought}");

            var edit = target.SelfOwned ? SelfWindow.EditHandle : nint.Zero;
            var point = target.SelfOwned ? new POINT { X = target.ClientX, Y = target.ClientY } : default;

            // A. 基线：窗口在前台时，目标 GUI 线程的焦点在哪。
            Record(lines, "A_baseline", target, edit);

            // B. 只投递窗口消息（不经过输入队列）：页面级动作有效，但通常不建立编辑焦点。
            var screen = ClientToScreen(target.Root, target.ClientX, target.ClientY);
            SendMouseMessages(target.Root, target.ClientX, target.ClientY);
            Thread.Sleep(300);
            Record(lines, "B_wm_mouse_messages", target, edit);

            // C. SendInput 真点击：经过输入队列，走系统激活与焦点转换。
            Native.SetCursorPos(screen.X, screen.Y);
            Thread.Sleep(150);
            ClickLeft();
            Thread.Sleep(400);
            Record(lines, "C_sendinput_click", target, edit);

            // D. SendInput Unicode：不依赖剪贴板与 IME，纯按键注入。
            TypeUnicode("probe1234");
            Thread.Sleep(300);
            Record(lines, "D_sendinput_unicode", target, edit);

            // E. 剪贴板 + Ctrl+V：唯一能保证中文字节完整的方式，但依赖应用真的处理 Ctrl+V。
            SetClipboardText("中文探针");
            SendCtrlV();
            Thread.Sleep(400);
            Record(lines, "E_clipboard_ctrl_v", target, edit);

            // F. 直投 WM_CHAR：绕过输入队列，直接进消息循环。
            SendWmChar(target.Root, 'X');
            Thread.Sleep(300);
            Record(lines, "F_wm_char", target, edit);
        }
        finally
        {
            SelfWindow.Stop(target.SelfOwned);
        }

        foreach (var line in lines)
        {
            Console.WriteLine(line);
        }

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllLines(outPath, lines, new UTF8Encoding(false));
        }

        return 0;
    }

    /// <summary>记录一个阶段的观测值。拿不到的项写 n/a，不推断。</summary>
    private static void Record(List<string> lines, string stage, Target target, nint edit)
    {
        var fg = Native.GetForegroundWindow();
        var focusHwnd = nint.Zero;
        var caretHwnd = nint.Zero;
        var guiState = "n/a";

        var tid = Native.GetWindowThreadProcessId(target.Root, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (Native.GetGUIThreadInfo(tid, ref info))
        {
            focusHwnd = info.hwndFocus;
            caretHwnd = info.hwndCaret;
            guiState = "ok";
        }

        var textLen = edit != nint.Zero ? ReadEditText(edit).Length : -1;
        var textHash = edit != nint.Zero ? Hash8(ReadEditText(edit)) : "n/a";

        lines.Add(
            $"STAGE={stage} fg=0x{fg:X} target_is_fg={(fg == target.Root ? 1 : 0)} gui_thread_info={guiState} " +
            $"gui_focus=0x{focusHwnd:X} gui_caret=0x{caretHwnd:X} " +
            $"focus_class={ClassName(focusHwnd)} text_len={textLen} text_sha={textHash}");
    }

    private static string ReadEditText(nint h)
    {
        var len = (int)Native.SendMessage(h, Native.WM_GETTEXTLENGTH, 0, 0);
        if (len <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(len + 2);
        Native.SendMessage(h, Native.WM_GETTEXT, (nint)(len + 1), sb);
        return sb.ToString();
    }

    private static string ClassName(nint h)
    {
        if (h == nint.Zero)
        {
            return "none";
        }

        var sb = new StringBuilder(256);
        var n = Native.GetClassName(h, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "unknown";
    }

    private static void SendMouseMessages(nint root, int cx, int cy)
    {
        var child = Native.WindowFromPoint(ClientToScreen(root, cx, cy));
        var pt = new POINT { X = cx, Y = cy };
        Native.ScreenToClient(child, ref pt);
        var lp = (nint)((pt.Y << 16) | (pt.X & 0xFFFF));
        Native.SendMessageTimeout(child, Native.WM_MOUSEMOVE, 0, lp, 2, 800, out _);
        Native.SendMessageTimeout(child, Native.WM_LBUTTONDOWN, 1, lp, 2, 800, out _);
        Native.SendMessageTimeout(child, Native.WM_LBUTTONUP, 0, lp, 2, 800, out _);
    }

    private static void SendWmChar(nint root, char c)
    {
        Native.SendMessageTimeout(root, Native.WM_CHAR, (nint)c, 1, 2, 800, out _);
    }

    private static POINT ClientToScreen(nint root, int cx, int cy)
    {
        var pt = new POINT { X = cx, Y = cy };
        Native.ClientToScreen(root, ref pt);
        return pt;
    }

    private static void ClickLeft()
    {
        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
        Thread.Sleep(60);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
    }

    /// <summary>逐字符发送 Unicode 按键，不使用剪贴板，也不经过 IME。</summary>
    private static void TypeUnicode(string text)
    {
        foreach (var ch in text)
        {
            var inputs = new INPUT[2];
            inputs[0].type = Native.INPUT_KEYBOARD;
            inputs[0].ki.wScan = (ushort)ch;
            inputs[0].ki.dwFlags = Native.KEYEVENTF_UNICODE;
            inputs[1] = inputs[0];
            inputs[1].ki.dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP;
            Native.SendInput(2, inputs, Marshal.SizeOf<INPUT>());
            Thread.Sleep(20);
        }
    }

    private static void SendCtrlV()
    {
        var keys = new (ushort vk, bool down)[]
        {
            (Native.VK_CONTROL, true), (Native.VK_V, true), (Native.VK_V, false), (Native.VK_CONTROL, false),
        };

        foreach (var (vk, down) in keys)
        {
            var inputs = new INPUT[1];
            inputs[0].type = Native.INPUT_KEYBOARD;
            inputs[0].ki.wVk = vk;
            inputs[0].ki.dwFlags = down ? 0u : Native.KEYEVENTF_KEYUP;
            Native.SendInput(1, inputs, Marshal.SizeOf<INPUT>());
            Thread.Sleep(40);
        }
    }

    private static void SetClipboardText(string text)
    {
        if (!Native.OpenClipboard(nint.Zero))
        {
            return;
        }

        try
        {
            Native.EmptyClipboard();
            var bytes = (uint)((text.Length + 1) * 2);
            var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, bytes);
            if (h == nint.Zero)
            {
                return;
            }

            var p = Native.GlobalLock(h);
            if (p == nint.Zero)
            {
                return;
            }

            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            Native.GlobalUnlock(h);
            Native.SetClipboardData(Native.CF_UNICODETEXT, h);
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    private static string Hash8(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes[..4]);
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int ParseInt(string? s) => int.TryParse(s, out var v) ? v : 0;

    private static long ParseLong(string? s) => long.TryParse(s, out var v) ? v : 0;
}

internal sealed record Target(nint Root, int ClientX, int ClientY, bool SelfOwned);

/// <summary>正对照组：自建 Win32 窗口 + EDIT，用来证明探针本身能区分成功与失败。</summary>
internal static class SelfWindow
{
    private static Thread? _thread;
    private static nint _root;
    private static nint _edit;
    private static readonly ManualResetEventSlim Ready = new(false);

    public static nint EditHandle => _edit;

    public static Target Start()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!Ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("self window did not become ready");
        }

        return new Target(_root, 60, 74, SelfOwned: true);
    }

    public static void Stop(bool selfOwned)
    {
        if (!selfOwned || _root == nint.Zero)
        {
            return;
        }

        Native.PostMessage(_root, Native.WM_CLOSE, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(3));
    }

    private static void Run()
    {
        var hInstance = Native.GetModuleHandle(null);
        var wc = new WNDCLASS
        {
            lpfnWndProc = Native.DefWindowProc,
            hInstance = hInstance,
            lpszClassName = "CuFocusProbeWnd",
            hCursor = Native.LoadCursor(nint.Zero, 32512),
        };
        Native.RegisterClass(ref wc);

        _root = Native.CreateWindowEx(0, "CuFocusProbeWnd", "ComputerUse Focus Probe",
            Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE, 100, 100, 720, 320,
            nint.Zero, nint.Zero, hInstance, nint.Zero);

        _edit = Native.CreateWindowEx(0, "EDIT", string.Empty,
            Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_BORDER | Native.ES_AUTOHSCROLL,
            20, 60, 600, 28, _root, nint.Zero, hInstance, nint.Zero);

        if (_root == nint.Zero || _edit == nint.Zero)
        {
            Console.WriteLine($"SELF_WINDOW_FAILED root=0x{_root:X} edit=0x{_edit:X} err={Marshal.GetLastWin32Error()}");
        }

        Ready.Set();

        while (Native.GetMessage(out var msg, nint.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }
}

internal static partial class Native
{
    public const int SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_CHAR = 0x0102;

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_BORDER = 0x00800000;
    public const uint ES_AUTOHSCROLL = 0x0080;

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_V = 0x56;

    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint h, int c);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(nint h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nint extra);
    [DllImport("user32.dll")] public static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern nint SendMessage(nint h, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern nint SendMessage(nint h, uint msg, nint wParam, StringBuilder lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SendMessageTimeout(nint h, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] public static extern int GetClassName(nint h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool PostMessage(nint h, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern nint SetClipboardData(uint format, nint handle);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("kernel32.dll")] public static extern nint GlobalAlloc(uint flags, uint bytes);
    [DllImport("kernel32.dll")] public static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClass(ref WNDCLASS wc);

    [DllImport("user32.dll")] public static extern nint DefWindowProc(nint h, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern nint LoadCursor(nint inst, int name);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, nint h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessage(ref MSG msg);
}
