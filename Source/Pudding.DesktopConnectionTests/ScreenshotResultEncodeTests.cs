using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Xunit;
using Proto = Pudding.Rpc.Protocol.V1;

namespace PuddingDesktopConnectionTests;

/// <summary>
/// B4 结果**编码侧**证据：Desktop 把领域结果编成线缆时 —— **观察 id 原样**、
/// **可选字段用 presence（0 是合法值，不能被当成"未设置"）**、**分块顺序与 IsLast 保真**、**空就是空**。
/// </summary>
public sealed class ScreenshotResultEncodeTests
{
    private static readonly DesktopCapabilityDescriptor ScreenshotCapability = new(
        DesktopCapability.BrowserScreenshot,
        "browser.screenshot",
        1,
        DesktopCapabilityKind.WebView,
        DesktopCapabilityTraits.RequiresTrustedContext | DesktopCapabilityTraits.RequiresPageTarget);

    private static DesktopScreenshotMetadata Metadata(
        double? cropX = null, double? scrollY = null) => new(
        "obs-42", "image/png", 2048, 1280, 720, 512, 288,
        DeviceScaleFactor: 2, ViewportWidth: 1280, ViewportHeight: 720,
        ScrollX: 0, ScrollY: scrollY,
        CropX: cropX, CropY: 0, CropWidth: 100, CropHeight: 50,
        PageVersion: 3, DomRevision: 4, ViewportRevision: 5, DesktopScreenshotDetail.Low);

    private static Proto.ScreenshotOutcome Encode(BrowserScreenshotResult result)
    {
        var frame = DesktopFrameMapping.Result(
            OperationId.NewId(), default, ScreenshotCapability, DesktopCapabilityResponse.FromScreenshot(result));
        Assert.NotNull(frame.Result);
        Assert.NotNull(frame.Result.Screenshot);
        return frame.Result.Screenshot;
    }

    [Fact]
    public void TheObservationIdAndScalarsSurviveEncoding()
    {
        var wire = Encode(new BrowserScreenshotResult(Metadata(), []));

        Assert.Equal("obs-42", wire.ObservationId);
        Assert.Equal("image/png", wire.MimeType);
        Assert.Equal(2048, wire.ByteCount);
        Assert.Equal(3, wire.PageVersion);
        Assert.Equal(4, wire.DomRevision);
        Assert.Equal(5, wire.ViewportRevision);
        Assert.Equal("low", wire.Detail);
    }

    [Fact]
    public void ZeroValuedOptionalsArePresentNotAbsent()
    {
        // ScrollX = 0 与 CropY = 0 都是**合法坐标**：编码后必须"已设置且为 0"。
        var wire = Encode(new BrowserScreenshotResult(Metadata(cropX: 0, scrollY: 0), []));

        Assert.True(wire.HasScrollX);
        Assert.Equal(0, wire.ScrollX);
        Assert.True(wire.HasCropX);
        Assert.Equal(0, wire.CropX);
        Assert.True(wire.HasCropY);
        Assert.Equal(0, wire.CropY);

        // 未提供的可选字段必须"未设置"（不臆造默认值）。
        var sparse = Encode(new BrowserScreenshotResult(Metadata(), []));
        Assert.False(sparse.HasCropX);
        Assert.False(sparse.HasScrollY);
    }

    [Fact]
    public void ChunksKeepTheirOrderAndLastFlag()
    {
        var result = new BrowserScreenshotResult(Metadata(), [
            new DesktopScreenshotChunk(0, IsLast: false, [1, 2]),
            new DesktopScreenshotChunk(1, IsLast: true, [3, 4, 5]),
        ]);

        var wire = Encode(result);

        Assert.Equal(2, wire.Chunks.Count);
        Assert.Equal(0, wire.Chunks[0].Index);
        Assert.False(wire.Chunks[0].IsLast);
        Assert.Equal([1, 2], wire.Chunks[0].Data.ToByteArray());
        Assert.Equal(1, wire.Chunks[1].Index);
        Assert.True(wire.Chunks[1].IsLast);
        Assert.Equal([3, 4, 5], wire.Chunks[1].Data.ToByteArray());
    }

    [Fact]
    public void NoChunksStaysEmptyInsteadOfAFabricatedOne()
    {
        var wire = Encode(new BrowserScreenshotResult(Metadata(), []));
        Assert.Empty(wire.Chunks);
    }
}