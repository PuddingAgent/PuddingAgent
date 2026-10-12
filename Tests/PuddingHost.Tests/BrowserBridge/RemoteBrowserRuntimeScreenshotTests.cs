using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.AgentTools;
using PuddingBrowser.Protocol;
using PuddingHost.BrowserBridge;
using Xunit;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// Bridge 传输**成功往返**（进程内）：桥客户端发送 <c>page.screenshot</c>，并把桌面返回的
/// <see cref="BrowserScreenshotDescriptor"/> 解码回来 —— 证明"发命令 → 解结果"与线缆形状一致。
/// </summary>
public sealed class RemoteBrowserRuntimeScreenshotTests
{
    [Fact]
    public async Task Screenshot_SendsPageScreenshotAndDecodesTheDescriptor()
    {
        var descriptor = new BrowserScreenshotDescriptor
        {
            ObservationId = "obs-1",
            MimeType = "image/png",
            ByteCount = 4,
            SourceWidth = 1280,
            SourceHeight = 720,
            OutputWidth = 512,
            OutputHeight = 288,
            DeviceScaleFactor = 1,
            ViewportWidth = 1280,
            ViewportHeight = 720,
            ScrollX = 0,
            ScrollY = 0,
            CropX = 0,
            CropY = 0,
            CropWidth = 1280,
            CropHeight = 720,
            PageVersion = 7,
            DomRevision = 1,
            ViewportRevision = 1,
            Detail = DesktopScreenshotDetailWire.NameOf(DesktopScreenshotDetail.Low),
            Chunks =
            [
                new BrowserScreenshotChunkDescriptor
                {
                    Index = 0,
                    IsLast = true,
                    Base64 = Convert.ToBase64String([1, 2, 3, 4]),
                }
            ],
        };

        var broker = new FakeBroker(descriptor);
        var runtime = new RemoteBrowserRuntime(broker, callerIdentity: new FakeCallerIdentity());

        var decoded = await runtime.ScreenshotAsync(
            new BrowserScreenshotRequest(
                new DesktopPageTarget("ctx-1", "page-1"),
                DesktopPageVersion.Require(7)),
            CancellationToken.None);

        Assert.Equal(BrowserBridgeCommandNames.PageScreenshot, broker.LastCommandName);
        Assert.Equal("obs-1", decoded.ObservationId);
        Assert.Equal(7, decoded.PageVersion);
        Assert.Equal("AQIDBA==", decoded.Chunks[0].Base64);
    }

    [Fact]
    public async Task Surface_MapsTheDescriptorIntoTheContractResult()
    {
        // 映射层：描述符 ⇒ BrowserScreenshotResult（Base64 → 字节、元数据逐项、Detail 线名解析）。
        var descriptor = SampleDescriptor(0);
        var runtime = new RemoteBrowserRuntime(
            new FakeBroker(descriptor), callerIdentity: new FakeCallerIdentity());
        var surface = new BridgeBrowserCapabilitySurface(runtime);

        var result = await surface.ScreenshotAsync(
            new BrowserScreenshotRequest(new DesktopPageTarget("ctx-1", "page-1"), DesktopPageVersion.Require(7)),
            new DesktopCallContext(
                new DesktopInstanceId("desk-1"),
                new OperationId(Guid.NewGuid().ToString("N")),
                DateTimeOffset.UtcNow.AddSeconds(30),
                correlationId: null,
                new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "run-1")));

        Assert.True(result.IsSuccess);
        var value = result.Value;
        Assert.Equal("obs-1", value.Metadata.ObservationId);
        Assert.Equal("image/png", value.Metadata.MimeType);
        Assert.Equal(4, value.Metadata.ByteCount);
        Assert.Equal(1280, value.Metadata.SourceWidth);
        Assert.Equal(720, value.Metadata.SourceHeight);
        Assert.Equal(512, value.Metadata.OutputWidth);
        Assert.Equal(288, value.Metadata.OutputHeight);
        Assert.Equal(7, value.Metadata.PageVersion);
        Assert.Equal(1, value.Metadata.DomRevision);
        Assert.Equal(1, value.Metadata.ViewportRevision);
        Assert.Equal(DesktopScreenshotDetail.Low, value.Metadata.Detail);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, value.Chunks[0].Data);
    }

    private static BrowserScreenshotDescriptor SampleDescriptor(int unused) => new()
    {
        ObservationId = "obs-1",
        MimeType = "image/png",
        ByteCount = 4,
        SourceWidth = 1280,
        SourceHeight = 720,
        OutputWidth = 512,
        OutputHeight = 288,
        DeviceScaleFactor = 1,
        ViewportWidth = 1280,
        ViewportHeight = 720,
        ScrollX = 0,
        ScrollY = 0,
        CropX = 0,
        CropY = 0,
        CropWidth = 1280,
        CropHeight = 720,
        PageVersion = 7,
        DomRevision = 1,
        ViewportRevision = 1,
        Detail = DesktopScreenshotDetailWire.NameOf(DesktopScreenshotDetail.Low),
        Chunks =
        [
            new BrowserScreenshotChunkDescriptor
            {
                Index = 0,
                IsLast = true,
                Base64 = Convert.ToBase64String([1, 2, 3, 4]),
            }
        ],
    };

    private sealed class FakeBroker(BrowserScreenshotDescriptor descriptor) : IDesktopBrowserCommandBroker
    {
        public string? LastCommandName { get; private set; }

        public bool IsDesktopConnected => true;

        public Task<BrowserBridgeCommandResult> ExecuteAsync(
            BrowserBridgeCommand command, CancellationToken cancellationToken)
        {
            LastCommandName = command.Name;
            return Task.FromResult(new BrowserBridgeCommandResult
            {
                OperationId = command.OperationId,
                Success = true,
                Value = JsonSerializer.SerializeToElement(
                    descriptor, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            });
        }

        public Task CancelAsync(Guid operationId, CancellationToken cancellationToken) => Task.CompletedTask;

        // 这两个成员用于接收来自连接的结果/失败推送；替身不需要，明确拒绝而不是静默。
        public void HandleResult(Guid connectionId, long generation, BrowserBridgeCommandResult result) =>
            throw new NotSupportedException();

        public void FailPendingForConnection(
            Guid connectionId, long generation, string errorCode, string message) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCallerIdentity : IBrowserCallerIdentityAccessor
    {
        public BrowserCallerIdentity? Current { get; } = new(BrowserCallerKind.Agent, "agent-1", "run-1");

        public string? CurrentFailureReason => null;
    }
}
