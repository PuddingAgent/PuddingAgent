using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.Windows;

public sealed record DesktopWindowState(long Handle, int ProcessId, long ProcessStartTimeUtcTicks,
    uint ThreadId, PhysicalRect Bounds, PhysicalRect ClientBounds, uint Dpi, bool Visible, bool Minimized,
    long ForegroundRoot, long Active, long Focus, long FocusRoot, long Caret, long Capture, uint GuiFlags)
{
    public bool HasNativeFocus => ForegroundRoot == Handle && Focus != 0 && FocusRoot == Handle;
}

public sealed record DesktopInputResult(string Action, ComputerActionReceipt Receipt,
    DesktopWindowState? Before, DesktopWindowState? After, ScreenPoint? Point = null,
    int? NativeError = null, string? Detail = null);

internal interface IWindowsInputPlatform
{
    DesktopWindowState Inspect(long handle);
    void Activate(long handle, bool repairNativeFocus);
    ScreenPoint Cursor();
    bool Move(ScreenPoint point);
    long HitRoot(ScreenPoint point);
    bool OnMonitor(ScreenPoint point);
    bool HasPressedModifiers();
    (int Submitted, int? Error) Send(NativeDesktopInput[] inputs);
    IDisposable EnterCoordinates();
}

/// <summary>Checked native input. Native focus and dispatch are not DOM/editor verification.</summary>
public sealed class WindowsDesktopInput
{
    internal static readonly object Gate = new();
    private readonly IWindowsInputPlatform _platform;
    public WindowsDesktopInput() : this(new Win32InputPlatform()) { }
    internal WindowsDesktopInput(IWindowsInputPlatform platform) => _platform = platform;

    public DesktopWindowState Inspect(long handle)
    {
        lock (Gate) { using var dpi = _platform.EnterCoordinates(); return _platform.Inspect(handle); }
    }

    public DesktopInputResult Focus(long handle, bool repairNativeFocus = false)
    {
        lock (Gate)
        {
            using var dpi = _platform.EnterCoordinates();
            var before = _platform.Inspect(handle);
            _platform.Activate(handle, repairNativeFocus);
            var after = _platform.Inspect(handle);
            return Result("focus", before, after, ComputerExecution.Completed,
                after.HasNativeFocus ? ComputerVerification.Passed : ComputerVerification.Failed,
                error: after.HasNativeFocus ? null : "native_focus_not_acquired",
                detail: "Verification concerns native window focus only; the focused Chromium editor is unknown.");
        }
    }

    public DesktopInputResult Click(long handle, ScreenPoint point, int steps = 12,
        string button = "left", int clicks = 1, CancellationToken ct = default)
        => MoveOrClick(handle, point, steps, button, clicks, ct);

    public DesktopInputResult Move(long handle, ScreenPoint point, int steps = 12, CancellationToken ct = default)
        => MoveOrClick(handle, point, steps, "left", 0, ct);

    private DesktopInputResult MoveOrClick(long handle, ScreenPoint point, int steps, string button, int clicks, CancellationToken ct)
    {
        if (button is not ("left" or "right") || clicks is < 0 or > 2) throw new ArgumentException("Invalid mouse action.");
        lock (Gate)
        {
            using var dpi = _platform.EnterCoordinates();
            ct.ThrowIfCancellationRequested();
            var action = clicks == 0 ? "mouse_move" : clicks == 2 ? "double_click" : button == "right" ? "right_click" : "click";
            var before = _platform.Inspect(handle);
            var failure = CheckPointer(before, point);
            if (failure is not null) return Blocked(action, before, failure, point);
            var from = _platform.Cursor();
            var count = Math.Clamp(steps, 1, 120);
            for (var i = 1; i <= count; i++)
            {
                if (ct.IsCancellationRequested)
                    return Result(action, before, _platform.Inspect(handle), ComputerExecution.Completed,
                        ComputerVerification.Failed, 0, clicks * 2, "cancelled_after_pointer_move", point);
                var t = (1 - Math.Cos(Math.PI * i / count)) / 2;
                if (!_platform.Move(new((int)Math.Round(from.X + ((double)point.X - from.X) * t),
                    (int)Math.Round(from.Y + ((double)point.Y - from.Y) * t))))
                    return Result(action, before, _platform.Inspect(handle), ComputerExecution.Completed,
                        ComputerVerification.Failed, 0, clicks * 2, "cursor_move_failed", point);
                Thread.Sleep(8);
            }
            var ready = _platform.Inspect(handle);
            failure = _platform.Cursor() != point ? "cursor_did_not_land" : CheckPointer(ready, point);
            if (ready.ProcessId != before.ProcessId || ready.ProcessStartTimeUtcTicks != before.ProcessStartTimeUtcTicks || ready.Bounds != before.Bounds)
                failure = "window_changed_during_move";
            if (failure is not null)
                return Result(action, before, ready, ComputerExecution.Completed, ComputerVerification.Failed,
                    0, clicks * 2, failure, point);
            if (clicks == 0)
                return Result(action, before, ready, ComputerExecution.Completed, ComputerVerification.Passed,
                    point: point, detail: "Only cursor position is verified.");
            var events = Enumerable.Range(0, clicks).SelectMany(_ => new[] {
                NativeDesktopInput.Mouse(button == "left" ? 0x0002u : 0x0008u),
                NativeDesktopInput.Mouse(button == "left" ? 0x0004u : 0x0010u) }).ToArray();
            return Dispatch(action, before, events, point);
        }
    }

    public DesktopInputResult Key(long handle, string chord, CancellationToken ct = default)
        => Keyboard(handle, "key", BuildChord(chord), ct);

    public DesktopInputResult TypeText(long handle, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is < 1 or > 4000) throw new ArgumentException("Text length must be 1..4000 UTF-16 units.");
        return Keyboard(handle, "type", text.SelectMany(ch => new[] {
            NativeDesktopInput.Unicode(ch, false), NativeDesktopInput.Unicode(ch, true) }).ToArray(), ct);
    }

    private DesktopInputResult Keyboard(long handle, string action, NativeDesktopInput[] inputs, CancellationToken ct)
    {
        lock (Gate)
        {
            using var dpi = _platform.EnterCoordinates();
            ct.ThrowIfCancellationRequested();
            var before = _platform.Inspect(handle);
            if (!before.Visible || before.Minimized || !before.HasNativeFocus)
                return Blocked(action, before, "native_focus_mismatch");
            if (_platform.HasPressedModifiers()) return Blocked(action, before, "modifier_already_pressed");
            return Dispatch(action, before, inputs);
        }
    }

    private DesktopInputResult Dispatch(string action, DesktopWindowState before, NativeDesktopInput[] inputs, ScreenPoint? point = null)
    {
        var (sent, error) = _platform.Send(inputs);
        var detail = "OS acceptance does not verify the UI effect. Observe again before retrying.";
        try
        {
            // A partially accepted prefix can leave a key/button down. Release only
            // keys introduced by that prefix, never replay the requested operation.
            if (sent > 0 && sent < inputs.Length)
            {
                var releases = inputs.Take(sent).Where(x => x.IsDown).Select(x => x.Release()).Reverse().ToArray();
                if (releases.Length > 0)
                {
                    var cleanup = _platform.Send(releases);
                    detail += $" Release cleanup accepted {cleanup.Submitted}/{releases.Length}; native error={cleanup.Error?.ToString() ?? "none"}.";
                }
            }
            Thread.Sleep(80);
            var after = _platform.Inspect(before.Handle);
            var focusLost = after.ForegroundRoot != before.Handle;
            return Result(action, before, after, ComputerExecution.Dispatched,
                focusLost ? ComputerVerification.Failed : ComputerVerification.Unverified,
                sent, inputs.Length, sent != inputs.Length ? "partial_input" : focusLost ? "foreground_changed" : null,
                point, error, detail);
        }
        catch (Exception ex)
        {
            return Result(action, before, null, ComputerExecution.Unknown, ComputerVerification.Unverified,
                sent, inputs.Length, "post_dispatch_check_failed", point, error, detail + " " + ex.Message);
        }
    }

    private string? CheckPointer(DesktopWindowState state, ScreenPoint point)
    {
        if (!state.Visible || state.Minimized) return "window_not_visible";
        if (state.ForegroundRoot != state.Handle) return "foreground_mismatch";
        if (!_platform.OnMonitor(point)) return "point_off_screen";
        if (point.X < state.Bounds.X || point.Y < state.Bounds.Y ||
            point.X >= (long)state.Bounds.X + state.Bounds.Width || point.Y >= (long)state.Bounds.Y + state.Bounds.Height)
            return "point_outside_window";
        return _platform.HitRoot(point) == state.Handle ? null : "unexpected_window_at_point";
    }

    internal static NativeDesktopInput[] BuildChord(string chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) throw new ArgumentException("keys is required.");
        var parts = chord.Split('+', StringSplitOptions.TrimEntries);
        var keys = parts.Select(KeyCode).ToArray();
        if (keys.Length > 5 || keys.Take(keys.Length - 1).Any(k => k is not (0x11 or 0x10 or 0x12 or 0x5B)) || keys.Distinct().Count() != keys.Length)
            throw new ArgumentException("Chord must contain distinct modifiers followed by one key.");
        return keys.Select(k => NativeDesktopInput.Key(k, false))
            .Concat(keys.Reverse().Select(k => NativeDesktopInput.Key(k, true))).ToArray();
    }

    public static void ValidateChord(string chord) => _ = BuildChord(chord);

    private static ushort KeyCode(string name)
    {
        var key = name.ToUpperInvariant();
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') return key[0];
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 12) return (ushort)(0x6F + f);
        return key switch { "CTRL" or "CONTROL" => 0x11, "SHIFT" => 0x10, "ALT" => 0x12, "WIN" or "META" => 0x5B,
            "ENTER" or "RETURN" => 0x0D, "TAB" => 9, "ESC" or "ESCAPE" => 0x1B, "SPACE" => 0x20,
            "BACKSPACE" => 8, "DELETE" or "DEL" => 0x2E, "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, "INSERT" or "INS" => 0x2D,
            _ => throw new ArgumentException("Unknown key: " + name) };
    }

    internal static DesktopInputResult Blocked(string action, DesktopWindowState? before, string error, ScreenPoint? point = null)
        => Result(action, before, before, ComputerExecution.NotStarted, ComputerVerification.NotRequested, error: error, point: point);

    private static DesktopInputResult Result(string action, DesktopWindowState? before, DesktopWindowState? after,
        ComputerExecution execution, ComputerVerification verification, int? submitted = null, int? expected = null,
        string? error = null, ScreenPoint? point = null, int? nativeError = null, string? detail = null)
    {
        var verdict = ComputerReceiptEvaluator.Evaluate(execution, verification, submitted, expected);
        return new(action, new(Guid.NewGuid().ToString("N"), execution, verification, verdict.Completion,
            verdict.MayHaveSideEffects, verdict.SafeToRetry, "win32-sendinput", submitted, expected, [], error),
            before, after, point, nativeError, detail);
    }
}
