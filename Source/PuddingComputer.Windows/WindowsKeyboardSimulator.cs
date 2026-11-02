using System.Runtime.InteropServices;

namespace PuddingComputer.Windows;

/// <summary>常用虚拟键码。只列实际会用到的，不追求完整。</summary>
public enum VirtualKey : ushort
{
    Enter = 0x0D,
    Tab = 0x09,
    Escape = 0x1B,
    Backspace = 0x08,
    Delete = 0x2E,
    Space = 0x20,
    Home = 0x24,
    End = 0x23,
    PageUp = 0x21,
    PageDown = 0x22,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    Control = 0x11,
    Shift = 0x10,
    Alt = 0x12,
    Win = 0x5B,
    A = 0x41,
    C = 0x43,
    V = 0x56,
    X = 0x58,
    Z = 0x5A,
}

/// <summary>
/// 键盘输入模拟。
/// <para>
/// ⚠️ **已知限制（2026-11-02 实测）**：对 Chromium/Electron 的 contenteditable，
/// 本方法发送的按键**可能完全无效**。原因不是本类的实现问题，而是渲染层与输入层不同源：
/// UIA 的 AX 焦点 ≠ Win32 键盘焦点，`hwndFocus` 停在窗口壳上、`hwndCaret` 为 0。
/// </para>
/// <para>
/// 但组件仍然要提供这个能力：它对 Win32 原生应用（记事本、资源管理器）工作正常，
/// 而且"能不能用"应该由调用方根据目标类型判断，而不是由输入层藏着这个限制。
/// </para>
/// </summary>
public static class WindowsKeyboardSimulator
{
    /// <summary>逐字输入。用 KEYEVENTF_UNICODE，绕过键盘布局与输入法。</summary>
    public static void TypeText(string text, int perCharDelayMs = 30)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (char ch in text)
        {
            SendUnicodeChar(ch);
            if (perCharDelayMs > 0) Thread.Sleep(perCharDelayMs);
        }
    }

    /// <summary>按一次键（按下 + 抬起）。</summary>
    public static void PressKey(VirtualKey key, int holdMs = 40)
    {
        SendVirtualKey(key, keyUp: false);
        Thread.Sleep(holdMs);
        SendVirtualKey(key, keyUp: true);
    }

    /// <summary>组合键，例如 Ctrl+A。修饰键按住 → 主键按下抬起 → 修饰键抬起。</summary>
    public static void SendShortcut(VirtualKey modifier, VirtualKey key)
    {
        if (modifier == key)
            throw new ArgumentException("修饰键与主键不能相同。", nameof(key));

        SendVirtualKey(modifier, keyUp: false);
        Thread.Sleep(20);
        SendVirtualKey(key, keyUp: false);
        Thread.Sleep(40);
        SendVirtualKey(key, keyUp: true);
        Thread.Sleep(20);
        SendVirtualKey(modifier, keyUp: true);
    }

    /// <summary>清空当前输入框内容（Ctrl+A 然后 Delete）。</summary>
    public static void ClearField()
    {
        SendShortcut(VirtualKey.Control, VirtualKey.A);
        Thread.Sleep(120);
        PressKey(VirtualKey.Delete);
    }

    private static void SendUnicodeChar(char ch)
    {
        var inputs = new INPUT[2];

        inputs[0].Type = InputKeyboard;
        inputs[0].Union.Keyboard = new KEYBDINPUT
        {
            ScanCode = ch,
            Flags = KeyEventUnicode
        };

        inputs[1].Type = InputKeyboard;
        inputs[1].Union.Keyboard = new KEYBDINPUT
        {
            ScanCode = ch,
            Flags = KeyEventUnicode | KeyEventKeyUp
        };

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendVirtualKey(VirtualKey key, bool keyUp)
    {
        var inputs = new INPUT[1];
        inputs[0].Type = InputKeyboard;
        inputs[0].Union.Keyboard = new KEYBDINPUT
        {
            VirtualKey = (ushort)key,
            Flags = keyUp ? KeyEventKeyUp : 0
        };

        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
