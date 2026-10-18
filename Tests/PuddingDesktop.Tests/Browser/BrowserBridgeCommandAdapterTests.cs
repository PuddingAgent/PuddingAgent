using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// I2-c D1 映射契约的第一片：`page.getInfo` → `webview.page_state`。
/// 先钉住"映射什么/拒绝什么/原因可行动"，再在后续切片切换派发器入口。
/// </summary>
public sealed class BrowserBridgeCommandAdapterTests
{
    [Fact]
    public void PageGetInfo_MapsToThePageStateCapability()
    {
        var command = Command(BrowserBridgeCommandNames.PageGetInfo, "ctx", "page");

        Assert.True(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason), reason);

        Assert.NotNull(descriptor);
        Assert.Equal(DesktopCapability.WebViewPageState, descriptor!.Capability);
        Assert.NotNull(request);
        Assert.Equal("ctx/page", request!.PageState!.Key);
    }

    [Fact]
    public void PageCommandsWithoutExplicitTargets_AreRejectedWithAnActionableReason()
    {
        var missingPage = Command(BrowserBridgeCommandNames.PageGetInfo, "ctx", null);
        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            missingPage, out _, out _, out var pageReason));
        Assert.Contains("explicit context and page ids", pageReason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnmappedCommands_FailClosedInsteadOfFallingBack()
    {
        // 尚未迁移的命令不得"看起来成功"或回退到 controller：返回 false 并给出原因。
        var command = Command(BrowserBridgeCommandNames.PageGoto, "ctx", "page");
        Assert.False(BrowserBridgeCommandAdapter.TryMapReadCommand(
            command, out var descriptor, out var request, out var reason));

        Assert.Null(descriptor);
        Assert.Null(request);
        Assert.Contains("not mapped", reason, StringComparison.Ordinal);
    }

    private static BrowserBridgeCommand Command(string name, string? contextId, string? pageId) => new()
    {
        OperationId = Guid.NewGuid(),
        Name = name,
        ContextId = contextId,
        PageId = pageId,
        DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30),
        Arguments = JsonSerializer.SerializeToElement(new { }),
    };
}
