using System.Text.Json;
using PuddingHost.ScreenCapture;

namespace PuddingHost.Tests.ScreenCapture;

/// <summary>
/// 屏幕采集的**线格式**（tool JSON 输出）契约。
///
/// <para>这组用例守的是一个真实缺陷：领域模型 <see cref="ScreenWindow"/> 的句柄是
/// <see cref="nint"/>，<c>System.Text.Json</c> 不支持序列化 <see cref="IntPtr"/>，
/// 曾导致 <c>action=displays</c> 整条失败（原文：
/// <c>Serialization and deserialization of 'System.IntPtr' instances is not supported. Path: $.windows.Handle.</c>）。
/// 所以这里既钉死"原始模型确实不可序列化"这一前提，也钉死"投影后可用且能原样回传"。</para>
/// </summary>
public sealed class ScreenCaptureWireTests
{
    private static ScreenWindow Window(
        nint handle,
        string title = "title",
        bool isVisible = true,
        bool isMinimized = false,
        bool isCloaked = false) =>
        new(handle, title, "process", new PixelRect(1, 2, 3, 4), isVisible, isMinimized, isCloaked);

    [Fact]
    public void RawScreenWindow_CannotBeSerialized_BecauseHandleIsIntPtr()
    {
        // 前提守卫：如果哪天 runtime 支持了 IntPtr，这条会取红 —— 那时就可以删掉投影层。
        var ex = Assert.ThrowsAny<Exception>(() =>
            JsonSerializer.Serialize(new[] { Window((nint)0x1A2B3C) }, ScreenCaptureWire.JsonOptions));

        Assert.Contains("IntPtr", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectWindows_SerializesHandleAsHexString()
    {
        var json = JsonSerializer.Serialize(
            ScreenCaptureWire.ProjectWindows([Window((nint)0x1A2B3C)]),
            ScreenCaptureWire.JsonOptions);

        Assert.Contains("\"handle\":\"0x1A2B3C\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectWindows_PreservesCountOrderAndFields()
    {
        // 两个句柄故意一大一小，并让标题可辨认顺序：投影不得重排或去重。
        ScreenWindow[] windows =
        [
            Window((nint)0xFFFF, "first"),
            Window((nint)0x1, "second"),
        ];

        var projected = ScreenCaptureWire.ProjectWindows(windows);

        Assert.Equal(2, projected.Length);
        Assert.Equal("first", projected[0].Title);
        Assert.Equal("second", projected[1].Title);
        Assert.Equal(new PixelRect(1, 2, 3, 4), projected[0].Bounds);
        Assert.True(projected[0].IsVisible);
    }

    [Fact]
    public void ProjectWindows_IsEmpty_ForNoWindows()
    {
        Assert.Empty(ScreenCaptureWire.ProjectWindows(Array.Empty<ScreenWindow>()));
    }

    [Theory]
    [InlineData(0x1)]
    [InlineData(0x1A2B3C)]
    [InlineData(0x7FFFFFFF)]
    public void FormatHandle_RoundTripsThroughTryParseHandle(long raw)
    {
        var handle = (nint)raw;

        var text = ScreenCaptureWire.FormatHandle(handle);

        Assert.StartsWith("0x", text, StringComparison.Ordinal);
        Assert.True(ScreenCaptureGeometry.TryParseHandle(text, out var parsed));
        Assert.Equal(handle, parsed);
    }

    [Fact]
    public void ProjectWindows_CarriesMinimizedAndCloakedFlags()
    {
        // 两个标志必须原样过线：调用方靠它们决定"这张图能不能用"。
        var projected = ScreenCaptureWire.ProjectWindows(
        [
            Window((nint)0x10, "min", isVisible: true, isMinimized: true),
            Window((nint)0x11, "cloaked", isVisible: true, isCloaked: true),
        ]);

        Assert.True(projected[0].IsMinimized);
        Assert.False(projected[0].IsCloaked);
        Assert.True(projected[1].IsCloaked);
        Assert.False(projected[1].IsMinimized);
    }
}
