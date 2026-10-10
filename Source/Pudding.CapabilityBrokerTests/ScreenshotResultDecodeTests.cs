using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Xunit;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.CapabilityBrokerTests;

/// <summary>
/// B4 结果**解码侧**证据：wire ⇒ 领域 —— **presence 语义**（0 有值、未设置是 null）、
/// **空分块不臆造**、**观察 id 保真**、**未登记 detail 明确失败**、**载荷与所命令能力不符明确失败**。
/// </summary>
public sealed class ScreenshotResultDecodeTests
{
    private static Proto.OperationResult Wire(bool scrollPresent = true, string detail = "low")
    {
        var outcome = new Proto.ScreenshotOutcome
        {
            ObservationId = "obs-9",
            MimeType = "image/png",
            ByteCount = 12,
            SourceWidth = 1280,
            SourceHeight = 720,
            OutputWidth = 512,
            OutputHeight = 288,
            PageVersion = 3,
            DomRevision = 4,
            ViewportRevision = 5,
            Detail = detail,
        };

        if (scrollPresent)
        {
            // 0 是合法坐标：必须 presence，不能被当成"未设置"。
            outcome.ScrollX = 0;
        }

        outcome.CropY = 0;

        return new Proto.OperationResult
        {
            OperationId = "op-1",
            Generation = 1,
            Screenshot = outcome,
        };
    }

    [Fact]
    public void DecodedFieldsMatchTheWireAndZeroStaysZero()
    {
        var decoded = DesktopResultDecoder.Decode(Wire(), DesktopCapability.BrowserScreenshot, requestedTarget: null);

        Assert.True(decoded.IsSuccess, decoded.IsFailure ? decoded.Error!.Message : null);
        var metadata = decoded.Value.Screenshot!.Metadata;

        Assert.Equal("obs-9", metadata.ObservationId);
        Assert.Equal("image/png", metadata.MimeType);
        Assert.Equal(12, metadata.ByteCount);
        Assert.Equal(3, metadata.PageVersion);
        Assert.Equal(4, metadata.DomRevision);
        Assert.Equal(5, metadata.ViewportRevision);
        Assert.Equal(DesktopScreenshotDetail.Low, metadata.Detail);

        // presence：0 保留为 0；未设置的是 null（**不臆造**）。
        Assert.Equal(0, metadata.ScrollX);
        Assert.Equal(0, metadata.CropY);
        Assert.Null(metadata.CropX);
        Assert.Null(metadata.ScrollY);
        Assert.Null(metadata.DeviceScaleFactor);

        // 空分块 ⇒ 空列表（不伪造一片）。
        Assert.Empty(decoded.Value.Screenshot!.Chunks);
    }

    [Fact]
    public void AnUnregisteredDetailIsRefused()
    {
        var decoded = DesktopResultDecoder.Decode(Wire(detail: "ultra"), DesktopCapability.BrowserScreenshot, null);
        Assert.True(decoded.IsFailure);
        Assert.Contains("detail", decoded.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APayloadThatDoesNotMatchTheCommandedCapabilityIsRefused()
    {
        var decoded = DesktopResultDecoder.Decode(Wire(), DesktopCapability.BrowserSnapshot, null);
        Assert.True(decoded.IsFailure);
        Assert.Contains("does not match", decoded.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }
}