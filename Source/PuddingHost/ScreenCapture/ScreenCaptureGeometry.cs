namespace PuddingHost.ScreenCapture;

/// <summary>
/// 屏幕采集的**纯几何/选择逻辑**：不碰 GDI、不碰文件、不碰 DI，
/// 因此可以在任意平台上单元测试（这是把「算得对不对」与「抓得到抓不到」分开的关键）。
///
/// <para>失败一律给出**可诊断的原因**（缺参数、越界、无匹配、有歧义），
/// 而不是抛一个上层看不懂的空引用或野异常 —— 这一点沿用浏览器工具那轮的教训：
/// 多匹配/无匹配必须能让人一眼看出该怎么改定位条件。</para>
/// </summary>
public static class ScreenCaptureGeometry
{
    /// <summary>虚拟桌面（全部显示器并集）的保留显示索引。</summary>
    public const int VirtualDesktopIndex = -1;

    /// <summary>单边像素上限：防止误传超大区域导致一次性分配过亿字节。</summary>
    public const int MaxRegionSide = 30_000;

    /// <summary>区域面积上限（约 8000 万像素 ≈ 320MB BGRA），超出直接拒绝而不是 OOM。</summary>
    public const long MaxRegionArea = 80_000_000;

    /// <summary>所有显示器外接矩形（虚拟桌面）。没有任何显示器时返回空矩形。</summary>
    public static PixelRect GetVirtualDesktop(IReadOnlyList<ScreenDisplay> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0)
        {
            return new PixelRect(0, 0, 0, 0);
        }

        var left = displays[0].Bounds.X;
        var top = displays[0].Bounds.Y;
        var right = displays[0].Bounds.Right;
        var bottom = displays[0].Bounds.Bottom;

        for (var i = 1; i < displays.Count; i++)
        {
            var b = displays[i].Bounds;
            left = Math.Min(left, b.X);
            top = Math.Min(top, b.Y);
            right = Math.Max(right, b.Right);
            bottom = Math.Max(bottom, b.Bottom);
        }

        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// 选择要采集的显示器。未指定索引时优先主显示器；索引 <see cref="VirtualDesktopIndex"/>
    /// 返回覆盖全部显示器的合成「虚拟桌面」。
    /// </summary>
    public static bool TrySelectDisplay(
        IReadOnlyList<ScreenDisplay> displays,
        int? requestedIndex,
        out ScreenDisplay display,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(displays);

        if (displays.Count == 0)
        {
            display = null!;
            error = "no display was reported by the operating system";
            return false;
        }

        if (requestedIndex is { } index && index == VirtualDesktopIndex)
        {
            display = new ScreenDisplay(VirtualDesktopIndex, "virtual-desktop", GetVirtualDesktop(displays), false);
            error = null;
            return true;
        }

        if (requestedIndex is { } explicitIndex)
        {
            var match = displays.FirstOrDefault(d => d.Index == explicitIndex);
            if (match is null)
            {
                display = null!;
                error = $"displayIndex {explicitIndex} is not available; {displays.Count} display(s) reported: "
                        + string.Join(", ", displays.Select(d => $"{d.Index}={d.Bounds}"));
                return false;
            }

            display = match;
            error = null;
            return true;
        }

        display = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
        error = null;
        return true;
    }

    /// <summary>
    /// 解析显式区域。四个分量必须同时给出（部分给出通常是调用方拼错了，猜默认值只会掩盖错误）。
    /// </summary>
    public static bool TryResolveRegion(int? x, int? y, int? width, int? height, out PixelRect region, out string? error)
    {
        region = default;

        var provided = new[] { x.HasValue, y.HasValue, width.HasValue, height.HasValue };
        var count = provided.Count(p => p);
        if (count == 0)
        {
            error = null; // 未请求区域：由调用方决定用显示器范围
            return false;
        }

        if (count != 4)
        {
            error = "region capture requires x, y, width and height all together";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            error = $"region width/height must be positive (got {width}x{height})";
            return false;
        }

        if (width > MaxRegionSide || height > MaxRegionSide)
        {
            error = $"region sides must not exceed {MaxRegionSide}px (got {width}x{height})";
            return false;
        }

        var area = (long)width.Value * height.Value;
        if (area > MaxRegionArea)
        {
            error = $"region area {area}px exceeds the {MaxRegionArea}px budget; capture a smaller region";
            return false;
        }

        region = new PixelRect(x!.Value, y!.Value, width!.Value, height!.Value);
        error = null;
        return true;
    }

    /// <summary>
    /// 在候选窗口里挑一个。优先按句柄精确匹配；否则按标题子串（忽略大小写）。
    /// 多个标题命中时取**可见且面积最大**的那个，并回报命中数量 —— 不静默猜一个就当没事。
    /// </summary>
    public static bool TryMatchWindow(
        IReadOnlyList<ScreenWindow> windows,
        string? title,
        string? handle,
        out ScreenWindow window,
        out int matchCount,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(windows);
        window = null!;
        matchCount = 0;

        if (!string.IsNullOrWhiteSpace(handle))
        {
            if (!TryParseHandle(handle!, out var hwnd))
            {
                error = $"windowHandle '{handle}' is not a usable handle (decimal or 0x-prefixed hex)";
                return false;
            }

            var exact = windows.FirstOrDefault(w => w.Handle == hwnd);
            if (exact is null)
            {
                error = $"no window with handle {hwnd} is currently open";
                return false;
            }

            window = exact;
            matchCount = 1;
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            error = "window capture requires windowTitle (substring) or windowHandle";
            return false;
        }

        var needle = title!.Trim();
        var matches = windows
            .Where(w => !string.IsNullOrEmpty(w.Title)
                        && w.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            var sample = windows
                .Where(w => !string.IsNullOrWhiteSpace(w.Title))
                .OrderByDescending(w => w.Bounds.Area)
                .Take(5)
                .Select(w => $"\"{Truncate(w.Title, 40)}\"");
            error = $"no window title contains \"{needle}\"; largest open windows: {string.Join(", ", sample)}";
            return false;
        }

        window = matches
            .OrderByDescending(w => w.IsVisible)
            .ThenByDescending(w => w.Bounds.Area)
            .First();
        matchCount = matches.Count;
        error = null;
        return true;
    }

    /// <summary>
    /// 解析 <c>windowHandle</c>：接受十进制或 <c>0x</c> 前缀十六进制。
    /// <para>与 <see cref="ScreenCaptureWire.FormatHandle"/> 构成一对：那个负责输出、这个负责还原。</para>
    /// </summary>
    public static bool TryParseHandle(string value, out nint handle)
    {
        handle = 0;
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex)
                   && (handle = (nint)hex) != 0;
        }

        return long.TryParse(text, out var dec) && (handle = (nint)dec) != 0;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
