using PuddingHost.ScreenCapture;

namespace PuddingHost.Tests.ScreenCapture;

/// <summary>
/// 屏幕采集的几何与选择逻辑（不触碰 GDI，可在任意平台跑）。
/// 这些用例守的是**坐标口径与失败可诊断性** —— 采集本身抓没抓到图，只能靠真机验证。
/// </summary>
public sealed class ScreenCaptureGeometryTests
{
    private static readonly ScreenDisplay Primary = new(0, @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
    private static readonly ScreenDisplay Secondary = new(1, @"\\.\DISPLAY2", new PixelRect(1920, -180, 2560, 1440), false);

    private static IReadOnlyList<ScreenDisplay> Displays() => [Primary, Secondary];

    [Fact]
    public void VirtualDesktop_UnionsEveryDisplay_IncludingNegativeOrigins()
    {
        var desktop = ScreenCaptureGeometry.GetVirtualDesktop(Displays());

        // 左 0、上 -180（副屏在左屏之上）、右 1920+2560、下 -180+1440=1260 ⇒ 高 1440。
        Assert.Equal(new PixelRect(0, -180, 4480, 1440), desktop);
    }

    [Fact]
    public void VirtualDesktop_IsEmpty_WhenNoDisplayIsReported()
    {
        Assert.True(ScreenCaptureGeometry.GetVirtualDesktop(Array.Empty<ScreenDisplay>()).IsEmpty);
    }

    [Fact]
    public void SelectDisplay_DefaultsToPrimary_NotToTheFirstEntry()
    {
        // 故意把副屏排在前面：默认必须是"主显示器"，而不是"数组第 0 个"。
        IReadOnlyList<ScreenDisplay> displays = [Secondary, Primary];

        Assert.True(ScreenCaptureGeometry.TrySelectDisplay(displays, null, out var selected, out var error));
        Assert.Null(error);
        Assert.Equal(0, selected.Index);
        Assert.True(selected.IsPrimary);
    }

    [Fact]
    public void SelectDisplay_MinusOne_ReturnsTheWholeVirtualDesktop()
    {
        Assert.True(ScreenCaptureGeometry.TrySelectDisplay(
            Displays(), ScreenCaptureGeometry.VirtualDesktopIndex, out var selected, out _));

        Assert.Equal(ScreenCaptureGeometry.VirtualDesktopIndex, selected.Index);
        Assert.Equal(new PixelRect(0, -180, 4480, 1440), selected.Bounds);
    }

    [Fact]
    public void SelectDisplay_OutOfRange_ReportsWhatIsActuallyAvailable()
    {
        Assert.False(ScreenCaptureGeometry.TrySelectDisplay(Displays(), 7, out _, out var error));

        Assert.NotNull(error);
        Assert.Contains("displayIndex 7", error);
        Assert.Contains("0=", error);
        Assert.Contains("1=", error);
    }

    [Fact]
    public void SelectDisplay_NoDisplay_IsAnExplicitFailure()
    {
        Assert.False(ScreenCaptureGeometry.TrySelectDisplay(Array.Empty<ScreenDisplay>(), null, out _, out var error));
        Assert.Contains("no display", error);
    }

    [Fact]
    public void ResolveRegion_NoRegionRequested_ReturnsFalseWithoutError()
    {
        // "没有请求区域" 与 "区域参数写错了" 必须能区分：前者交给显示器口径，后者必须报错。
        Assert.False(ScreenCaptureGeometry.TryResolveRegion(null, null, null, null, out _, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void ResolveRegion_PartialCoordinates_AreRejected()
    {
        Assert.False(ScreenCaptureGeometry.TryResolveRegion(10, 20, null, 30, out _, out var error));
        Assert.Contains("x, y, width and height", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ResolveRegion_NonPositiveSides_AreRejected(int side)
    {
        Assert.False(ScreenCaptureGeometry.TryResolveRegion(0, 0, side, 100, out _, out var error));
        Assert.Contains("must be positive", error);
    }

    [Fact]
    public void ResolveRegion_SideAboveLimit_IsRejected()
    {
        var tooWide = ScreenCaptureGeometry.MaxRegionSide + 1;

        Assert.False(ScreenCaptureGeometry.TryResolveRegion(0, 0, tooWide, 10, out _, out var error));
        Assert.Contains("must not exceed", error);
    }

    [Fact]
    public void ResolveRegion_AreaAboveBudget_IsRejected()
    {
        // 单边都在限内，但乘积超预算 —— 只查单边会漏掉这种"两万乘两万"的申请。
        Assert.False(ScreenCaptureGeometry.TryResolveRegion(0, 0, 20_000, 20_000, out _, out var error));
        Assert.Contains("area", error);
    }

    [Fact]
    public void ResolveRegion_ValidRectangle_IsAccepted()
    {
        Assert.True(ScreenCaptureGeometry.TryResolveRegion(-100, -180, 640, 480, out var region, out var error));

        Assert.Null(error);
        Assert.Equal(new PixelRect(-100, -180, 640, 480), region);
    }

    [Fact]
    public void Intersect_ReturnsEmptyRect_WhenDisjoint()
    {
        var left = new PixelRect(0, 0, 100, 100);
        var right = new PixelRect(200, 200, 50, 50);

        Assert.True(left.Intersect(right).IsEmpty);
    }

    [Fact]
    public void Intersect_ClipsToOverlap()
    {
        var screen = new PixelRect(0, 0, 1920, 1080);
        var requested = new PixelRect(1800, 1000, 400, 400);

        Assert.Equal(new PixelRect(1800, 1000, 120, 80), screen.Intersect(requested));
    }

    [Fact]
    public void MatchWindow_ByHexHandle_FindsTheExactWindow()
    {
        IReadOnlyList<ScreenWindow> windows =
        [
            new((nint)0x2A, "Alpha", "alpha", new PixelRect(0, 0, 100, 100), true, false, false),
            new((nint)0x2B, "Beta", "beta", new PixelRect(0, 0, 200, 200), true, false, false),
        ];

        Assert.True(ScreenCaptureGeometry.TryMatchWindow(windows, null, "0x2B", out var window, out var count, out _));
        Assert.Equal((nint)0x2B, window.Handle);
        Assert.Equal(1, count);
    }

    [Fact]
    public void MatchWindow_MultipleTitleMatches_PicksLargestVisible_AndReportsTheCount()
    {
        IReadOnlyList<ScreenWindow> windows =
        [
            new((nint)1, "Pudding — chat", "PuddingAgent", new PixelRect(0, 0, 800, 600), true, false, false),
            new((nint)2, "Pudding — 设置", "PuddingAgent", new PixelRect(0, 0, 1600, 1200), true, false, false),
            new((nint)3, "Pudding — 隐藏", "PuddingAgent", new PixelRect(0, 0, 9999, 9999), false, false, false),
        ];

        Assert.True(ScreenCaptureGeometry.TryMatchWindow(windows, "pudding", null, out var window, out var count, out _));

        Assert.Equal((nint)2, window.Handle); // 可见者里最大；隐藏的巨窗不算
        Assert.Equal(3, count);               // 命中数量必须如实回报，不静默取一个
    }

    [Fact]
    public void MatchWindow_NoTitleMatch_SuggestsWhatIsOpen()
    {
        IReadOnlyList<ScreenWindow> windows =
        [
            new((nint)1, "Visual Studio Code", "Code", new PixelRect(0, 0, 1600, 1200), true, false, false),
        ];

        Assert.False(ScreenCaptureGeometry.TryMatchWindow(windows, "不存在的窗口", null, out _, out _, out var error));

        Assert.Contains("no window title contains", error);
        Assert.Contains("Visual Studio Code", error);
    }

    [Fact]
    public void MatchWindow_WithoutTitleOrHandle_IsRejected()
    {
        Assert.False(ScreenCaptureGeometry.TryMatchWindow(
            [new((nint)1, "X", "x", new PixelRect(0, 0, 10, 10), true, false, false)], " ", null, out _, out _, out var error));

        Assert.Contains("requires windowTitle", error);
    }

    [Fact]
    public void MatchWindow_MalformedHandle_IsRejectedRatherThanGuessed()
    {
        Assert.False(ScreenCaptureGeometry.TryMatchWindow(
            [new((nint)1, "X", "x", new PixelRect(0, 0, 10, 10), true, false, false)], null, "not-a-handle", out _, out _, out var error));

        Assert.Contains("not a usable handle", error);
    }

    [Fact]
    public void MatchWindow_ByHandle_Minimized_IsRejectedWithPlaceholderReason()
    {
        // 最小化窗口的 GetWindowRect 给的是 -32000 屏外占位矩形；抓它只会得到一张碎片。
        IReadOnlyList<ScreenWindow> windows =
        [
            new((nint)0x2A, "最小化的窗口", "app", new PixelRect(-32000, -32000, 160, 28), true, true, false),
        ];

        Assert.False(ScreenCaptureGeometry.TryMatchWindow(windows, null, "0x2A", out _, out _, out var error));

        Assert.Contains("is minimized", error);
        Assert.Contains("-32000", error);   // 必须把占位矩形亮出来，调用方才明白为什么不给图
    }

    [Fact]
    public void MatchWindow_PrefersNonMinimized_EvenWhenItIsSmaller()
    {
        IReadOnlyList<ScreenWindow> windows =
        [
            new((nint)1, "Pudding — 最小化", "app", new PixelRect(-32000, -32000, 160, 28), true, true, false),
            new((nint)2, "Pudding — 正常", "app", new PixelRect(0, 0, 800, 600), true, false, false),
        ];

        Assert.True(ScreenCaptureGeometry.TryMatchWindow(windows, "pudding", null, out var window, out var count, out _));

        Assert.Equal((nint)2, window.Handle);
        Assert.Equal(2, count);
    }

    [Fact]
    public void MatchWindow_AllMatchesMinimized_FailsInsteadOfReturningAPlaceholderShot()
    {
        IReadOnlyList<ScreenWindow> windows =
        [
            new((nint)1, "Pudding — A", "app", new PixelRect(-32000, -32000, 160, 28), true, true, false),
            new((nint)2, "Pudding — B", "app", new PixelRect(-32000, -32000, 160, 28), true, true, false),
        ];

        Assert.False(ScreenCaptureGeometry.TryMatchWindow(windows, "pudding", null, out _, out var count, out var error));

        Assert.Equal(2, count);             // 命中了几个仍然如实回报
        Assert.Contains("is minimized", error);
    }

    [Fact]
    public void MatchWindow_PrefersNonCloaked_OverLargerCloaked()
    {
        // 实测形态：ChatGPT 窗口在副屏上 isVisible=true、面积更大，但被 DWM 隐藏（另一虚拟桌面）。
        IReadOnlyList<ScreenWindow> windows =
        [
            new((nint)1, "ChatGPT", "ChatGPT", new PixelRect(4008, 0, 1113, 1440), true, false, true),
            new((nint)2, "ChatGPT", "ChatGPT", new PixelRect(0, 0, 800, 600), true, false, false),
        ];

        Assert.True(ScreenCaptureGeometry.TryMatchWindow(windows, "chatgpt", null, out var window, out _, out _));

        Assert.Equal((nint)2, window.Handle);
    }

    [Fact]
    public void MatchWindow_CloakedWindow_IsStillAllowed_OnlyMinimizedIsBlocked()
    {
        // cloaked 的 bounds 与渲染内容仍有效（PrintWindow 画窗口自身），所以不阻塞；
        // 只有最小化的 -32000 占位矩形会污染结果。
        IReadOnlyList<ScreenWindow> windows =
        [
            new((nint)1, "ChatGPT", "ChatGPT", new PixelRect(4008, 0, 1113, 1440), true, false, true),
        ];

        Assert.True(ScreenCaptureGeometry.TryMatchWindow(windows, "chatgpt", null, out var window, out _, out _));

        Assert.True(window.IsCloaked);
        Assert.Equal(new PixelRect(4008, 0, 1113, 1440), window.Bounds);
    }
}
