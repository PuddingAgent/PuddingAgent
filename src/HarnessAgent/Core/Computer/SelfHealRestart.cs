using System.Drawing;
using System.Text.Json;

namespace HarnessAgent.Core.Computer;

public sealed class SelfHealRestart
{
    private readonly string _configPath;
    public CodexLayout? Layout { get; private set; }

    public SelfHealRestart(string? configPath = null)
    {
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HarnessAgent", "codex-layout.json");
    }

    public CodexLayout Discover()
    {
        var hwnd = WindowAutomation.FindWindowByTitle("回应问候");
        if (hwnd == IntPtr.Zero)
            hwnd = WindowAutomation.FindWindowByTitle("Codex");

        if (hwnd == IntPtr.Zero)
        {
            var active = WindowAutomation.GetActiveWindow();
            var title = WindowAutomation.GetWindowTitle(active);
            throw new InvalidOperationException(
                $"Codex window not found. Active: \"{title}\". Start Codex and retry.");
        }

        var rect = WindowAutomation.GetWindowRect(hwnd);
        var layout = new CodexLayout
        {
            WindowTitle = WindowAutomation.GetWindowTitle(hwnd),
            WindowClass = WindowAutomation.GetWindowClass(hwnd),
            WindowBounds = rect,
            InputBoxOffsetX = 148,
            InputBoxOffsetY = 1343,
            InputBoxWidth = 720,
            InputBoxHeight = 90,
            SendButtonOffsetX = 870,
            SendButtonOffsetY = 1395,
            DiscoveredAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        Layout = layout;
        SaveLayout(layout);
        return layout;
    }

    public CodexLayout? LoadCachedLayout()
    {
        if (!File.Exists(_configPath)) return null;
        try
        {
            var json = File.ReadAllText(_configPath);
            Layout = JsonSerializer.Deserialize<CodexLayout>(json);
            return Layout;
        }
        catch { return null; }
    }

    public async Task<RestartResult> ExecuteRestartAsync(
        string command, int waitAfterSendMs = 30000, CancellationToken ct = default)
    {
        var layout = Layout ?? LoadCachedLayout() ?? Discover();
        if (layout == null)
            return RestartResult.Failed("Could not discover Codex layout.");

        try
        {
            var hwnd = WindowAutomation.FindWindowByTitle(layout.WindowTitle);
            if (hwnd == IntPtr.Zero)
                return RestartResult.Failed($"Codex window '{layout.WindowTitle}' not found.");

            WindowAutomation.Focus(hwnd);
            await Task.Delay(200, ct);

            int cx = layout.WindowBounds.Left + layout.InputBoxOffsetX;
            int cy = layout.WindowBounds.Top + layout.InputBoxOffsetY;
            WindowAutomation.Click(cx, cy);
            await Task.Delay(100, ct);

            WindowAutomation.PressKey(0x11); await Task.Delay(5, ct);
            WindowAutomation.PressKey(0x41); await Task.Delay(5, ct);
            WindowAutomation.PressKey(0x08);
            await Task.Delay(50, ct);

            WindowAutomation.TypeText(command, delayMs: 3);
            await Task.Delay(100, ct);
            WindowAutomation.PressEnter();

            await Task.Delay(waitAfterSendMs, ct);
            return RestartResult.Success($"Sent: \"{command[..Math.Min(60, command.Length)]}...\"");
        }
        catch (Exception ex)
        {
            return RestartResult.Failed(ex.Message);
        }
    }

    private void SaveLayout(CodexLayout layout)
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_configPath,
            JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record CodexLayout
{
    public required string WindowTitle { get; init; }
    public string WindowClass { get; init; } = "";
    public Rectangle WindowBounds { get; init; }
    public int InputBoxOffsetX { get; init; }
    public int InputBoxOffsetY { get; init; }
    public int InputBoxWidth { get; init; }
    public int InputBoxHeight { get; init; }
    public int SendButtonOffsetX { get; init; }
    public int SendButtonOffsetY { get; init; }
    public string DiscoveredAt { get; init; } = "";
}

public sealed record RestartResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = "";
    public string? Error { get; init; }

    public static RestartResult Success(string msg) => new() { IsSuccess = true, Message = msg };
    public static RestartResult Failed(string error) => new() { IsSuccess = false, Error = error, Message = error };
}
