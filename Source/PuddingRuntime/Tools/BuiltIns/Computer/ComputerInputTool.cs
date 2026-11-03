using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 本机输入模拟与会话级坐标助手。
/// <para>
/// 存在的理由（2026-11-03 实测）：对没有无障碍树的桌面应用（典型是 Chromium 客户端，
/// UIA 只暴露 <c>Chrome Legacy Window</c> 一个空 Pane）只能靠像素坐标操作，
/// 而"坐标算错"和"事件没送到"在外部看起来完全一样。本工具把这两件事分开：
/// 每条动作都返回可核对的事实（窗口矩形、客户区原点、DPI、命中测试、写入长度），
/// 让调用方<strong>不需要相信"已派发"</strong>就能判断动作是否真的落地。
/// </para>
/// <para>
/// 两处刻意选择：
/// (1) 文本输入默认走「剪贴板 + Ctrl+V」而不是 Unicode 注入；
/// (2) 鼠标平滑移动用插值后的 <c>SetCursorPos</c>，而不是相对位移
///     ——相对位移受系统鼠标加速影响会走短（实测目标 45,40 停在 80,72）。
/// </para>
/// </summary>
[Tool(
    id: "computer_input",
    name: "Computer input",
    description:
        "模拟本机输入并给出可核对事实：剪贴板写入（文本/文件）、按键与组合键、平滑鼠标移动与点击、窗口与客户区坐标解析（含 DPI 与命中测试）。用于操作不支持无障碍树的桌面应用；文本输入默认走剪贴板+Ctrl+V。",
    category: ToolCategory.Execute,
    permission: ToolPermissionLevel.Medium,
    SortOrder = 61)]
public sealed class ComputerInputTool : PuddingToolBase<ComputerInputArgs>
{
    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        ComputerInputArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        try
        {
            var result = args.Action?.Trim().ToLowerInvariant() switch
            {
                "clipboard_text" => ClipboardText(args.Text),
                "clipboard_files" => ClipboardFiles(args.FilePaths),
                "clipboard_read" => ClipboardRead(),
                "key" => PressKeyChord(args.Keys),
                "type" => TypeViaClipboard(args.Text, args.Keys),
                "mouse_move" => MouseMove(args.X, args.Y, args.Steps, verify: true),
                "click" => Click(args.X, args.Y, args.Steps, right: false, doubleClick: false),
                "right_click" => Click(args.X, args.Y, args.Steps, right: true, doubleClick: false),
                "double_click" => Click(args.X, args.Y, args.Steps, right: false, doubleClick: true),
                "window" => WindowInfoOf(args.WindowTitle, args.Handle),
                "resolve" => ResolvePoint(args.WindowTitle, args.Handle, args.ClientX ?? 0, args.ClientY ?? 0, args.Click),
                null or "" => Fail("action 必填。可选：clipboard_text / clipboard_files / clipboard_read / key / type / mouse_move / click / right_click / double_click / window / resolve"),
                _ => Fail($"未知 action '{args.Action}'。可选：clipboard_text / clipboard_files / clipboard_read / key / type / mouse_move / click / right_click / double_click / window / resolve"),
            };
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(ex.GetType().Name + ": " + ex.Message));
        }
    }

    // ── 剪贴板 ────────────────────────────────────────────────────────────

    private static ToolExecutionResult ClipboardText(string? text)
    {
        if (text is null) return Fail("clipboard_text 需要 text。");
        if (!TrySetClipboardText(text, out var error))
            return Fail("写入剪贴板失败：" + error);
        return ToolExecutionResult.Ok(Json(new
        {
            action = "clipboard_text",
            ok = true,
            chars = text.Length,
            // 回读校验：写完不复核就等于相信"已派发"
            readBackMatches = string.Equals(ClipboardReadText(), text, StringComparison.Ordinal),
        }));
    }

    private static ToolExecutionResult ClipboardFiles(string[]? paths)
    {
        if (paths is null || paths.Length == 0) return Fail("clipboard_files 需要 filePaths。");
        var missing = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToArray();
        if (missing.Length > 0) return Fail("以下路径不存在：" + string.Join("; ", missing));

        if (!TrySetClipboardFiles(paths, out var error))
            return Fail("写入文件到剪贴板失败：" + error);
        return ToolExecutionResult.Ok(Json(new
        {
            action = "clipboard_files",
            ok = true,
            count = paths.Length,
            paths,
        }));
    }

    private static ToolExecutionResult ClipboardRead()
    {
        var text = ClipboardReadText();
        return ToolExecutionResult.Ok(Json(new
        {
            action = "clipboard_read",
            hasText = text is not null,
            chars = text?.Length ?? 0,
            preview = text is null ? null : (text.Length > 200 ? text[..200] + "…" : text),
        }));
    }

    // ── 键盘 ──────────────────────────────────────────────────────────────

    private static ToolExecutionResult PressKeyChord(string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys)) return Fail("key 需要 keys，例如 \"Enter\" 或 \"Ctrl+V\"。");
        if (!TryParseChord(keys, out var modifiers, out var key, out var parseError))
            return Fail(parseError);

        foreach (var m in modifiers) KeyDown(m);
        KeyDown(key);
        KeyUp(key);
        for (var i = modifiers.Count - 1; i >= 0; i--) KeyUp(modifiers[i]);

        return ToolExecutionResult.Ok(Json(new { action = "key", chord = keys, dispatched = true }));
    }

    private static ToolExecutionResult TypeViaClipboard(string? text, string? pasteChord)
    {
        if (string.IsNullOrEmpty(text)) return Fail("type 需要 text。");
        if (!TrySetClipboardText(text, out var error)) return Fail("写入剪贴板失败：" + error);

        var chord = string.IsNullOrWhiteSpace(pasteChord) ? "Ctrl+V" : pasteChord;
        if (!TryParseChord(chord, out var modifiers, out var key, out var parseError))
            return Fail(parseError);

        foreach (var m in modifiers) KeyDown(m);
        KeyDown(key); KeyUp(key);
        for (var i = modifiers.Count - 1; i >= 0; i--) KeyUp(modifiers[i]);

        return ToolExecutionResult.Ok(Json(new
        {
            action = "type",
            chars = text.Length,
            chord,
            dispatched = true,
            note = "Ctrl+V 由目标应用处理，本工具无法确认是否插入；请用截图或后续动作核对。",
        }));
    }

    // ── 鼠标 ──────────────────────────────────────────────────────────────

    private static ToolExecutionResult MouseMove(int? x, int? y, int steps, bool verify)
    {
        if (x is null || y is null) return Fail("mouse_move 需要 x 与 y。");
        var path = SmoothMove(x.Value, y.Value, steps);
        return ToolExecutionResult.Ok(Json(new
        {
            action = "mouse_move",
            from = new { x = path.FromX, y = path.FromY },
            to = new { x = path.ToX, y = path.ToY },
            endedAt = new { x = path.EndX, y = path.EndY },
            landedExactly = verify && path.EndX == x.Value && path.EndY == y.Value,
            steps = path.Steps,
        }));
    }

    private static ToolExecutionResult Click(int? x, int? y, int steps, bool right, bool doubleClick)
    {
        if (x is null || y is null) return Fail("点击类动作需要 x 与 y。");
        var path = SmoothMove(x.Value, y.Value, steps);

        var down = right ? MouseEventRightDown : MouseEventLeftDown;
        var up = right ? MouseEventRightUp : MouseEventLeftUp;
        var repeat = doubleClick ? 2 : 1;
        for (var i = 0; i < repeat; i++)
        {
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            if (i + 1 < repeat) Thread.Sleep(80);
        }

        return ToolExecutionResult.Ok(Json(new
        {
            action = right ? "right_click" : "click",
            point = new { x = x.Value, y = y.Value },
            landedExactly = path.EndX == x.Value && path.EndY == y.Value,
            hitTest = HitTest(x.Value, y.Value),
        }));
    }

    /// <summary>
    /// 平滑移动：插值后逐点 <c>SetCursorPos</c>。
    /// 不用相对位移（<c>MOUSEEVENTF_MOVE</c>）是因为系统鼠标加速会把每步重新解释，
    /// 累积误差可达几十像素——终点会走短且不可预测（2026-11-03 实测）。
    /// </summary>
    private static (int FromX, int FromY, int ToX, int ToY, int EndX, int EndY, int Steps) SmoothMove(int x, int y, int steps)
    {
        GetCursorPos(out var start);
        var fromX = start.X;
        var fromY = start.Y;
        var count = Math.Clamp(steps, 1, 120);

        var dx = x - fromX;
        var dy = y - fromY;
        var distance = Math.Sqrt((double)dx * dx + (double)dy * dy);
        if (distance < 1)
        {
            SetCursorPos(x, y);
            return (fromX, fromY, x, y, x, y, 0);
        }

        for (var i = 1; i <= count; i++)
        {
            var t = i / (double)count;
            var eased = 0.5 - 0.5 * Math.Cos(Math.PI * t);   // ease-in-out
            SetCursorPos(fromX + (int)Math.Round(dx * eased), fromY + (int)Math.Round(dy * eased));
            Thread.Sleep(10);
        }

        SetCursorPos(x, y);   // 末端对齐：逼近路径保留，落点精确
        Thread.Sleep(20);
        GetCursorPos(out var end);
        return (fromX, fromY, x, y, end.X, end.Y, count);
    }

    // ── 坐标解析 ──────────────────────────────────────────────────────────

    private static ToolExecutionResult WindowInfoOf(string? titleContains, long? handle)
    {
        if (!TryResolveWindow(titleContains, handle, out var hwnd, out var failure)) return Fail(failure!);

        GetWindowRect(hwnd, out var wr);
        GetClientRect(hwnd, out var cr);
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref origin);
        var dpi = GetDpiForWindow(hwnd);

        return ToolExecutionResult.Ok(Json(new
        {
            action = "window",
            handle = hwnd.ToInt64(),
            title = WindowTitle(hwnd),
            windowRect = new { left = wr.Left, top = wr.Top, width = wr.Right - wr.Left, height = wr.Bottom - wr.Top },
            clientSize = new { width = cr.Right - cr.Left, height = cr.Bottom - cr.Top },
            clientOrigin = new { x = origin.X, y = origin.Y },
            dpi,
            scale = dpi / 96.0,
            note = "客户区相对坐标 = 屏幕坐标 - clientOrigin；若窗口最大化，clientOrigin 通常为负值。",
        }));
    }

    private static ToolExecutionResult ResolvePoint(string? titleContains, long? handle, int clientX, int clientY, bool click)
    {
        if (!TryResolveWindow(titleContains, handle, out var hwnd, out var failure)) return Fail(failure!);

        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref origin);
        var screenX = origin.X + clientX;
        var screenY = origin.Y + clientY;

        object? clickResult = null;
        if (click)
        {
            var path = SmoothMove(screenX, screenY, 24);
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            clickResult = new { landedExactly = path.EndX == screenX && path.EndY == screenY };
        }

        return ToolExecutionResult.Ok(Json(new
        {
            action = "resolve",
            handle = hwnd.ToInt64(),
            clientPoint = new { x = clientX, y = clientY },
            clientOrigin = new { x = origin.X, y = origin.Y },
            screenPoint = new { x = screenX, y = screenY },
            hitTest = HitTest(screenX, screenY),
            click = clickResult,
        }));
    }

    // ── 帮助 ──────────────────────────────────────────────────────────────

    private static bool TryResolveWindow(string? titleContains, long? handle, out IntPtr hwnd, out string? failure)
    {
        hwnd = IntPtr.Zero;
        failure = null;

        if (handle is > 0)
        {
            hwnd = new IntPtr(handle.Value);
            if (!IsWindow(hwnd))
            {
                failure = $"handle {handle.Value} 不是有效窗口。";
                return false;
            }
            return true;
        }

        if (string.IsNullOrWhiteSpace(titleContains))
        {
            failure = "需要 windowTitle（标题片段）或 handle。";
            return false;
        }

        var matches = new List<(IntPtr Hwnd, string Title)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var title = WindowTitle(h);
            if (title.Length > 0 && title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                matches.Add((h, title));
            return true;
        }, IntPtr.Zero);

        if (matches.Count == 0)
        {
            failure = $"没有标题包含 '{titleContains}' 的可见窗口。";
            return false;
        }
        if (matches.Count > 1)
        {
            failure = $"标题包含 '{titleContains}' 的窗口有 {matches.Count} 个，请指定 handle。候选：" +
                      string.Join("; ", matches.Take(5).Select(m => m.Hwnd.ToInt64() + "=" + m.Title));
            return false;
        }

        hwnd = matches[0].Hwnd;
        return true;
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string HitTest(int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return "no-window";
        return GetAncestor(hwnd, GaRoot) + ":" + WindowTitle(GetAncestor(hwnd, GaRoot));
    }

    private static bool TryParseChord(string chord, out List<byte> modifiers, out byte key, out string? error)
    {
        modifiers = [];
        key = 0;
        error = null;

        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "空按键序列。";
            return false;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var isLast = i == parts.Length - 1;
            var vk = VirtualKeyOf(part);
            if (vk is null)
            {
                error = $"无法识别的按键 '{part}'。支持 Ctrl/Shift/Alt/Win、A-Z、0-9、Enter/Tab/Esc/Space/Backspace/Delete/方向键/F1-F12。";
                return false;
            }

            if (!isLast)
            {
                if (vk is not (0x11 or 0x10 or 0x12 or 0x5B or 0x5C))
                {
                    error = $"'{part}' 出现在组合键前缀位置但不是修饰键。";
                    return false;
                }
                modifiers.Add(vk.Value);
            }
            else
            {
                key = vk.Value;
            }
        }

        if (key == 0)
        {
            // 只给了修饰键：当作单键按下
            key = modifiers[^1];
            modifiers.RemoveAt(modifiers.Count - 1);
        }
        return true;
    }

    private static byte? VirtualKeyOf(string name)
    {
        if (name.Length == 1)
        {
            var ch = char.ToUpperInvariant(name[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9') return (byte)ch;
        }

        return name.ToLowerInvariant() switch
        {
            "ctrl" or "control" => 0x11,
            "shift" => 0x10,
            "alt" => 0x12,
            "win" or "meta" => 0x5B,
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            "space" => 0x20,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73,
            "f5" => 0x74, "f6" => 0x75, "f7" => 0x76, "f8" => 0x77,
            "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
            _ => null,
        };
    }

    private static void KeyDown(byte vk) => keybd_event(vk, 0, 0, UIntPtr.Zero);
    private static void KeyUp(byte vk) => keybd_event(vk, 0, KeyEventKeyUp, UIntPtr.Zero);

    private static ToolExecutionResult Fail(string message) => ToolExecutionResult.Ok(Json(new { ok = false, error = message }));

    private static string Json(object payload) => JsonSerializer.Serialize(payload, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // ── Win32 ─────────────────────────────────────────────────────────────

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);

    private const uint CfUnicodeText = 13;
    private const uint CfHDrop = 15;
    private const uint GmemMoveable = 0x0042;

    private static bool TrySetClipboardText(string text, out string? error)
    {
        error = null;
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero)
        {
            error = "GlobalAlloc 失败";
            return false;
        }

        var target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            error = "GlobalLock 失败";
            return false;
        }
        Marshal.Copy(bytes, 0, target, bytes.Length);
        GlobalUnlock(handle);

        if (!OpenClipboard(IntPtr.Zero))
        {
            GlobalFree(handle);
            error = "OpenClipboard 失败（剪贴板被其他进程占用）";
            return false;
        }

        try
        {
            EmptyClipboard();
            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                error = "SetClipboardData 失败";
                return false;
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static string? ClipboardReadText()
    {
        if (!IsClipboardFormatAvailable(CfUnicodeText)) return null;
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero) return null;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TrySetClipboardFiles(string[] paths, out string? error)
    {
        error = null;

        // DROPFILES 头(20 字节) + 以 \0 分隔且以双 \0 结尾的宽字符路径表
        var list = string.Join("\0", paths) + "\0\0";
        var pathBytes = Encoding.Unicode.GetBytes(list);
        var header = 20;
        var total = header + pathBytes.Length;

        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)total);
        if (handle == IntPtr.Zero)
        {
            error = "GlobalAlloc 失败";
            return false;
        }

        var target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            error = "GlobalLock 失败";
            return false;
        }

        // DROPFILES { pFiles=20, pt={0,0}, fNC=FALSE, fWide=TRUE }
        var buffer = new byte[total];
        BitConverter.GetBytes(header).CopyTo(buffer, 0);
        BitConverter.GetBytes(1).CopyTo(buffer, 16);   // fWide = TRUE
        pathBytes.CopyTo(buffer, header);
        Marshal.Copy(buffer, 0, target, total);
        GlobalUnlock(handle);

        if (!OpenClipboard(IntPtr.Zero))
        {
            GlobalFree(handle);
            error = "OpenClipboard 失败（剪贴板被其他进程占用）";
            return false;
        }

        try
        {
            EmptyClipboard();
            if (SetClipboardData(CfHDrop, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                error = "SetClipboardData(CF_HDROP) 失败";
                return false;
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }
}

/// <summary><see cref="ComputerInputTool"/> 参数。</summary>
public sealed record ComputerInputArgs
{
    /// <summary>动作名，见工具描述。</summary>
    [Description("clipboard_text | clipboard_files | clipboard_read | key | type | mouse_move | click | right_click | double_click | window | resolve")]
    public string? Action { get; init; }

    /// <summary>文本（clipboard_text / type）。</summary>
    public string? Text { get; init; }

    /// <summary>要放入剪贴板的文件或目录（clipboard_files）。</summary>
    public string[]? FilePaths { get; init; }

    /// <summary>按键序列，如 "Enter"、"Ctrl+V"（key；type 时可覆盖默认粘贴键）。</summary>
    public string? Keys { get; init; }

    /// <summary>屏幕坐标 X（鼠标类动作）。</summary>
    public int? X { get; init; }

    /// <summary>屏幕坐标 Y（鼠标类动作）。</summary>
    public int? Y { get; init; }

    /// <summary>窗口标题片段（window / resolve）。</summary>
    public string? WindowTitle { get; init; }

    /// <summary>窗口句柄（window / resolve；标题有歧义时用它指定）。</summary>
    public long? Handle { get; init; }

    /// <summary>客户区相对 X（resolve）。</summary>
    public int? ClientX { get; init; }

    /// <summary>客户区相对 Y（resolve）。</summary>
    public int? ClientY { get; init; }

    /// <summary>resolve 时是否顺便点击。</summary>
    public bool Click { get; init; }

    /// <summary>平滑移动的插值步数（默认 24）。</summary>
    public int Steps { get; init; } = 24;
}
