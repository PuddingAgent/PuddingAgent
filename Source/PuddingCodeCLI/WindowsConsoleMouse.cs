using System.Runtime.InteropServices;

namespace PuddingCodeCLI;

internal static class WindowsConsoleMouse
{
    private static bool s_modeInitialized;
    private static bool s_modeAvailable;

    public static bool TryReadWheelDirection(out int direction)
    {
        direction = 0;
        if (!OperatingSystem.IsWindows()) return false;
        if (!EnsureMode()) return false;

        var handle = GetStdHandle(STD_INPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE) return false;

        while (true)
        {
            if (!PeekConsoleInputW(handle, out var peek, 1, out var peeked) || peeked == 0)
                return false;

            // Keep keyboard events for Console.ReadKey path.
            if (peek.EventType != MOUSE_EVENT)
                return false;

            if (!ReadConsoleInputW(handle, out var record, 1, out var read) || read == 0)
                return false;

            var mouse = record.MouseEvent;
            if (mouse.dwEventFlags == MOUSE_WHEELED)
            {
                var delta = unchecked((short)((mouse.dwButtonState >> 16) & 0xffff));
                if (delta == 0) continue;
                direction = delta > 0 ? -1 : 1; // -1: up, +1: down
                return true;
            }
        }
    }

    private static bool EnsureMode()
    {
        if (s_modeInitialized) return s_modeAvailable;
        s_modeInitialized = true;

        var handle = GetStdHandle(STD_INPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE)
            return s_modeAvailable = false;

        if (!GetConsoleMode(handle, out var mode))
            return s_modeAvailable = false;

        // Enable mouse events and preserve other modes.
        mode |= ENABLE_MOUSE_INPUT;
        mode |= ENABLE_EXTENDED_FLAGS;
        mode &= ~ENABLE_QUICK_EDIT_MODE;

        s_modeAvailable = SetConsoleMode(handle, mode);
        return s_modeAvailable;
    }

    private const int STD_INPUT_HANDLE = -10;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;

    private const ushort MOUSE_EVENT = 0x0002;
    private const uint MOUSE_WHEELED = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEventRecord
    {
        public Coord dwMousePosition;
        public uint dwButtonState;
        public uint dwControlKeyState;
        public uint dwEventFlags;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public MouseEventRecord MouseEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PeekConsoleInputW(
        IntPtr hConsoleInput,
        out InputRecord lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsRead);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadConsoleInputW(
        IntPtr hConsoleInput,
        out InputRecord lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsRead);
}

