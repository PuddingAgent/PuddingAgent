using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Tools;
using PuddingCode.Models;
using Pudding.Contracts.Computer;
using PuddingComputer.Windows;

namespace PuddingRuntime.Services.Tools;

/// <summary>Tool adapter over the independently tested Windows input component.</summary>
[Tool(id: "computer_input", name: "Computer input",
    description: "Windows GUI input. Use windows to choose a handle, focus, then computer_observe. Click with observationId + targetId, or observationId + imageX/imageY from that exact image. key/type require handle and verified native focus. type uses Unicode by default; inputMode=paste uses clipboard+Ctrl+V. Receipts distinguish blocked/dispatched/verified; a dispatched click or text is NOT a verified UI effect: observe again before retrying. focus verifies native focus only, not the Chromium editor. Raw screen x/y require handle. Also supports window/resolve, mouse_move, clipboard_text/files/read.",
    category: ToolCategory.Execute, permission: ToolPermissionLevel.Medium, SortOrder = 61)]
public sealed class ComputerInputTool(ComputerDesktopSession desktop) : PuddingToolBase<ComputerInputArgs>
{
    private static readonly object ToolGate = new();
    protected override Task<ToolExecutionResult> ExecuteCoreAsync(ComputerInputArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult(Fail("Computer input requires Windows."));
        lock (ToolGate)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                using var dpi = WindowsDpiEnforcer.EnterPhysicalCoordinates();
                return Task.FromResult(Execute(args, context, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Do not turn exceptions after a native call into a safe-to-retry assertion.
                return Task.FromResult(new ToolExecutionResult { Success = false, ExitCode = 1,
                    Error = ex.GetType().Name + ": " + ex.Message, Status = "unknown",
                    Output = Json(new { completion = "unknown", mayHaveSideEffects = true, safeToRetry = false,
                        error = ex.Message, nativeError = ex is Win32Exception native ? (int?)native.NativeErrorCode : null,
                        next = "Inspect target state before retrying; access denied may require matching desktop/session/integrity." }) });
            }
        }
    }

    private ToolExecutionResult Execute(ComputerInputArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var action = args.Action?.Trim().ToLowerInvariant();
        var owner = Json(new[] { context.WorkspaceId, context.SessionId, context.AgentInstanceId });
        switch (action)
        {
            case "clipboard_text": return ClipboardText(args.Text);
            case "clipboard_files": return ClipboardFiles(args.FilePaths);
            case "clipboard_read": return ClipboardRead();
            case "windows": return ToolExecutionResult.Ok(Json(WindowsWindowManager.EnumerateTopLevel()
                .Select(w => new { handle = (long)w.Handle, w.Title, w.ClassName, w.ProcessId, w.Bounds, w.IsMinimized })));
        }
        if (action is not ("focus" or "window" or "resolve" or "key" or "type" or "mouse_move" or "click" or "right_click" or "double_click"))
            return Fail("Unknown action. Use windows, focus, window, resolve, key, type, mouse_move, click, right_click, double_click or clipboard_text/files/read.");
        DesktopInputResult result;
        if (args.ObservationId is { Length: > 0 } observationId && action is "click" or "right_click" or "double_click")
        {
            if (args.Handle is not null || args.WindowTitle is not null || args.X is not null || args.Y is not null)
                return Fail("Observation click already identifies its window. Do not mix handle/windowTitle/screen coordinates with observationId.");
            if (args.ImageX.HasValue != args.ImageY.HasValue) return Fail("imageX and imageY must be supplied together.");
            result = desktop.ClickObservation(owner, observationId, args.TargetId,
                args.ImageX is { } x && args.ImageY is { } y ? new ImagePoint(x, y) : null,
                args.Steps, action == "right_click" ? "right" : "left", action == "double_click" ? 2 : 1, ct);
        }
        else
        {
            if (args.ObservationId is not null || args.TargetId is not null || args.ImageX is not null || args.ImageY is not null)
                return Fail("observationId and image/target coordinates apply only to an observation click.");
            if (!TryResolveWindow(args.WindowTitle, args.Handle, out var handle, out var failure)) return Fail(failure!);
            if (action == "window") return ToolExecutionResult.Ok(Json(desktop.Input.Inspect(handle)));
            if (action == "focus") result = desktop.Input.Focus(handle, args.RepairNativeFocus);
            else if (action == "key") result = desktop.Input.Key(handle, args.Keys ?? "", ct);
            else if (action == "type")
            {
                if (string.IsNullOrEmpty(args.Text)) return Fail("type requires text.");
                if (args.InputMode is null or "unicode") result = desktop.Input.TypeText(handle, args.Text, ct);
                else if (args.InputMode == "paste")
                {
                    var chord = string.IsNullOrWhiteSpace(args.Keys) ? "Ctrl+V" : args.Keys;
                    WindowsDesktopInput.ValidateChord(chord);
                    var before = desktop.Input.Inspect(handle);
                    if (!before.HasNativeFocus) return Fail("native_focus_mismatch: focus the target and its editor first.");
                    if (!TrySetClipboardText(args.Text, out var error)) return Fail("Clipboard write failed: " + error);
                    if (ClipboardReadText() != args.Text) return Fail("Clipboard readback unavailable or changed; no paste was dispatched.");
                    result = desktop.Input.Key(handle, chord, ct) with { Action = "type" };
                }
                else return Fail("inputMode must be unicode or paste.");
            }
            else
            {
                ScreenPoint point;
                if (action == "resolve")
                {
                    if (args.ClientX is not { } cx || args.ClientY is not { } cy) return Fail("resolve requires clientX/clientY in physical client pixels.");
                    var window = desktop.Input.Inspect(handle);
                    if (cx < 0 || cy < 0 || cx >= window.ClientBounds.Width || cy >= window.ClientBounds.Height) return Fail("Client point is outside the window.");
                    point = new(window.ClientBounds.X + cx, window.ClientBounds.Y + cy);
                    if (!args.Click) return ToolExecutionResult.Ok(Json(new { window, screenPoint = point, coordinateSpace = "physical-screen-pixels" }));
                }
                else
                {
                    if (args.X is not { } x || args.Y is not { } y) return Fail("Raw mouse actions require x/y physical screen pixels and a target handle.");
                    point = new(x, y);
                }
                result = action == "mouse_move" ? desktop.Input.Move(handle, point, args.Steps, ct)
                    : desktop.Input.Click(handle, point, args.Steps, action == "right_click" ? "right" : "left", action == "double_click" ? 2 : 1, ct);
            }
        }
        if (result.Receipt.MayHaveSideEffects && result.Before is { } target) desktop.Invalidate(owner, target.Handle);
        var ok = result.Receipt.Completion is ComputerCompletion.Dispatched or ComputerCompletion.Verified;
        return new ToolExecutionResult { Success = ok, ExitCode = ok ? 0 : 1,
            Status = result.Receipt.Completion.ToString().ToLowerInvariant(),
            Error = ok ? null : result.Receipt.ErrorCode, Output = Json(result) };
    }

    private static bool TryResolveWindow(string? title, long? handle, out long resolved, out string? error)
    {
        resolved = 0; error = null;
        if (handle is > 0) { resolved = handle.Value; return true; }
        if (string.IsNullOrWhiteSpace(title)) { error = "Specify handle or windowTitle; use action=windows to choose a target."; return false; }
        var matches = WindowsWindowManager.EnumerateTopLevel().Where(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) { error = $"Title matched {matches.Length} windows; specify an exact handle."; return false; }
        resolved = matches[0].Handle; return true;
    }

    private static ToolExecutionResult Fail(string message) => new() {
        Success = false, ExitCode = 1, Error = message, Status = "blocked",
        Output = Json(new { completion = "blocked", error = message, inputDispatched = false }) };
    private static string Json(object payload) => JsonSerializer.Serialize(payload, JsonOpts);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private static ToolExecutionResult ClipboardText(string? text)
    {
        if (text is null) return Fail("clipboard_text 需要 text。");
        if (!TrySetClipboardText(text, out var error))
            return Fail("写入剪贴板失败：" + error);
        if (!string.Equals(ClipboardReadText(), text, StringComparison.Ordinal))
            return Fail("剪贴板回读不一致；不要假定文本已就绪。");
        return ToolExecutionResult.Ok(Json(new
        {
            action = "clipboard_text",
            ok = true,
            chars = text.Length,
            // 回读校验：写完不复核就等于相信"已派发"
            readBackMatches = true,
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

    private static bool OpenClipboardWithRetry(IntPtr owner)
    {
        // Clipboard observers can briefly hold it after a write. Retry acquisition,
        // never replay a paste or overwrite a changed clipboard during readback.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    // EmptyClipboard needs a real owner for a subsequent SetClipboardData.
    // A message-only STATIC window stays on this synchronous calling thread.
    private sealed class ClipboardOwner : IDisposable
    {
        public IntPtr Handle { get; } = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        public ClipboardOwner() { if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Clipboard owner creation failed."); }
        public void Dispose() => DestroyWindow(Handle);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);

    private static bool TrySetClipboardText(string text, out string? error)
    {
        using var owner = new ClipboardOwner();
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

        if (!OpenClipboardWithRetry(owner.Handle))
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
        if (!OpenClipboardWithRetry(IntPtr.Zero)) return null;
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
        using var owner = new ClipboardOwner();
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

        if (!OpenClipboardWithRetry(owner.Handle))
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

public sealed record ComputerInputArgs
{
    [ToolParam("windows | focus | window | resolve | key | type | mouse_move | click | right_click | double_click | clipboard_text | clipboard_files | clipboard_read")]
    public string? Action { get; init; }
    public string? Text { get; init; }
    public string[]? FilePaths { get; init; }
    [ToolParam("Key chord, e.g. Ctrl+V or Tab. Unicode type does not require keys.")]
    public string? Keys { get; init; }
    [ToolParam("unicode (default) or paste (clipboard + keys, default Ctrl+V)")]
    public string? InputMode { get; init; }
    [ToolParam("Physical screen pixel X for raw mouse actions; prefer observationId + targetId.")]
    public int? X { get; init; }
    public int? Y { get; init; }
    public string? WindowTitle { get; init; }
    [ToolParam("Exact top-level HWND; required for key/type/focus. Omit when using observationId.")]
    public long? Handle { get; init; }
    public int? ClientX { get; init; }
    public int? ClientY { get; init; }
    public bool Click { get; init; }
    public int Steps { get; init; } = 12;
    [ToolParam("ID from computer_observe. Single-use; do not reuse after mutation or geometry/content changes.")]
    public string? ObservationId { get; init; }
    [ToolParam("Candidate number in that exact observation; not a semantic/UIA element identity.")]
    public int? TargetId { get; init; }
    [ToolParam("Alternative corrected point in pixels of that exact observation image; mutually exclusive with targetId.")]
    public double? ImageX { get; init; }
    public double? ImageY { get; init; }
    [ToolParam("focus only: optionally AttachThreadInput briefly and SetFocus on the top-level window if native focus is missing. Never establishes a DOM editor focus.")]
    public bool RepairNativeFocus { get; init; }
}
