using Pudding.Contracts.Desktop;
using Xunit;

namespace Pudding.ContractsTests;

/// <summary>
/// B4 截图契约（§8.1）：范围与参数**一一对应**、必须钉版本、线名冻结、元数据/结果结构校验。
/// </summary>
public sealed class ScreenshotContractTests
{
    private static readonly DesktopPageTarget Target = new("ctx-1", "p-1");

    private static DesktopLocator Button => new(DesktopLocatorKind.Css, "button");

    [Fact]
    public void AViewportScreenshotTakesNoLocatorAndNoRegion()
    {
        Assert.Throws<ArgumentException>(() => new BrowserScreenshotRequest(
            Target, DesktopPageVersion.Require(3), DesktopScreenshotScope.Viewport, locator: Button));
        Assert.Throws<ArgumentException>(() => new BrowserScreenshotRequest(
            Target, DesktopPageVersion.Require(3), DesktopScreenshotScope.Viewport, regionX: 1, regionY: 1, regionWidth: 10, regionHeight: 10));
    }

    [Fact]
    public void AnElementScreenshotRequiresALocator()
    {
        Assert.Throws<ArgumentException>(() => new BrowserScreenshotRequest(
            Target, DesktopPageVersion.Require(3), DesktopScreenshotScope.Element));
    }

    [Fact]
    public void ARegionScreenshotRequiresAPositiveRectangle()
    {
        Assert.Throws<ArgumentException>(() => new BrowserScreenshotRequest(
            Target, DesktopPageVersion.Require(3), DesktopScreenshotScope.Region, regionX: 1, regionY: 1));
        Assert.Throws<ArgumentException>(() => new BrowserScreenshotRequest(
            Target, DesktopPageVersion.Require(3), DesktopScreenshotScope.Region,
            regionX: 0, regionY: 0, regionWidth: 0, regionHeight: 10));
    }

    [Fact]
    public void AScreenshotMustPinThePageVersion()
    {
        Assert.Throws<ArgumentException>(() => new BrowserScreenshotRequest(
            Target, new DesktopPageVersion(0)));
    }

    [Fact]
    public void ScopeAndDetailWireNamesAreStableAndUnknownNamesAreRejected()
    {
        Assert.Equal("viewport", DesktopScreenshotScopeWire.NameOf(DesktopScreenshotScope.Viewport));
        Assert.Equal("element", DesktopScreenshotScopeWire.NameOf(DesktopScreenshotScope.Element));
        Assert.Equal("low", DesktopScreenshotDetailWire.NameOf(DesktopScreenshotDetail.Low));
        Assert.Equal("original", DesktopScreenshotDetailWire.NameOf(DesktopScreenshotDetail.Original));

        Assert.True(DesktopScreenshotScopeWire.TryParse("region", out var region));
        Assert.Equal(DesktopScreenshotScope.Region, region);
        Assert.False(DesktopScreenshotScopeWire.TryParse("full-page", out _));
        Assert.False(DesktopScreenshotDetailWire.TryParse("medium", out _));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DesktopScreenshotScopeWire.NameOf((DesktopScreenshotScope)99));
    }

    [Fact]
    public void MetadataAndResultRefuseAnEmptyObservationOrMissingParts()
    {
        var valid = new DesktopScreenshotMetadata(
            "obs-1", "image/png", 1024, 1280, 720, 512, 288,
            DeviceScaleFactor: 2, ViewportWidth: 1280, ViewportHeight: 720,
            ScrollX: 0, ScrollY: 0, CropX: null, CropY: null, CropWidth: null, CropHeight: null,
            PageVersion: 3, DomRevision: 4, ViewportRevision: 5, DesktopScreenshotDetail.Low);

        valid.Validate();

        var empty = valid with { ObservationId = "  " };
        Assert.Throws<ArgumentException>(empty.Validate);

        var noChunks = new BrowserScreenshotResult(valid, []);
        noChunks.Validate(); // 空分块列表结构上合法（内容为空由上层按字节数判断）

        // 位置记录本身不做校验；**显式调用 Validate()** 才拒绝缺失部件。
        var missingMetadata = new BrowserScreenshotResult(null!, []);
        Assert.Throws<ArgumentNullException>(missingMetadata.Validate);
    }
}