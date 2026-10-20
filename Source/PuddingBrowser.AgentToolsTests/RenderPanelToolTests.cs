using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.AgentTools;
using PuddingCode.Tools;
using PuddingCore.Panels;

namespace PuddingBrowser.AgentToolsTests;

/// <summary>
/// render_panel 的行为边界：默认画布、契约形状校验、结果不含内容，
/// 以及最重要的一条 —— **通道不可用只影响「显示」，不影响「已写入」**。
///
/// 与 preview_document 同理：这里跑的是工具本体而不是内部纯函数 ——
/// 基类选错（BrowserAgentToolBase 强制要求浏览器调用者身份）只会在真正执行时暴露。
/// </summary>
public sealed class RenderPanelToolTests
{
    private static ToolExecutionRequest Request(string argumentsJson) => new()
    {
        ToolCallId = "call-1",
        ArgumentsJson = argumentsJson,
        Context = new ToolExecutionContext
        {
            WorkspaceId = "default",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
        },
    };

    private static RenderPanelTool Tool(
        InMemoryPanelCanvasStore store,
        FakeSurface surface,
        bool desktopAvailable = true)
        => new(store, surface, new FakeCallContexts(desktopAvailable));

    [Fact]
    public async Task DefaultsToTheMainCanvas_StoresIt_AndReturnsIdentityOnly()
    {
        var store = new InMemoryPanelCanvasStore();
        var surface = new FakeSurface();

        var result = await Tool(store, surface).ExecuteAsync(Request("""{"html":"<p>hi</p>"}"""));

        Assert.True(result.Success, result.Error);
        // 与 Shell 侧「交互面板」单例 tab 对齐：不传 panelId 时落到同一个画布。
        Assert.Equal("main", surface.LastRequest!.PanelId);
        Assert.Equal("<p>hi</p>", surface.LastRequest.Content);
        Assert.Equal("html", surface.LastRequest.ContentType);

        // 权威事实在存储里，不在桌面返回里。
        var stored = store.Get("main");
        Assert.NotNull(stored);
        Assert.Equal("<p>hi</p>", stored!.Content);
        Assert.Equal(1L, stored.Revision);

        // 结果只带身份与阶段：内容不回显（可能很长，也可能含用户数据）。
        // 注意不要写成 DoesNotContain("<p>hi</p>")：序列化会把 < > 转义，那条断言永远成立 —— 是假绿。
        Assert.DoesNotContain("hi", result.Output, StringComparison.Ordinal);
        Assert.Contains("main", result.Output, StringComparison.Ordinal);
        Assert.Contains("stored_and_shown", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitPanelIdAndTitleArePassedThrough()
    {
        var store = new InMemoryPanelCanvasStore();
        var surface = new FakeSurface();

        var result = await Tool(store, surface).ExecuteAsync(
            Request("""{"panelId":"design.v2","html":"<svg/>","title":"车身结构"}"""));

        Assert.True(result.Success, result.Error);
        Assert.Equal("design.v2", surface.LastRequest!.PanelId);
        Assert.Equal("车身结构", surface.LastRequest.Title);
        Assert.Equal("车身结构", store.Get("design.v2")!.Title);
    }

    [Fact]
    public async Task RepublishingIncrementsRevision()
    {
        var store = new InMemoryPanelCanvasStore();
        var tool = Tool(store, new FakeSurface());

        await tool.ExecuteAsync(Request("""{"html":"<p>v1</p>"}"""));
        await tool.ExecuteAsync(Request("""{"html":"<p>v2</p>"}"""));

        var stored = store.Get("main");
        Assert.NotNull(stored);
        Assert.Equal(2L, stored!.Revision);
        Assert.Equal("<p>v2</p>", stored.Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyHtml_FailsBeforeAnyStoreOrDesktopWrite(string html)
    {
        var store = new InMemoryPanelCanvasStore();
        var surface = new FakeSurface();

        var result = await Tool(store, surface).ExecuteAsync(Request($$"""{"html":"{{html}}"}"""));

        Assert.False(result.Success);
        // fail closed 在两侧写入之前：存储不落、命令不发。
        Assert.Null(store.Get("main"));
        Assert.Null(surface.LastRequest);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("中文")]
    public async Task IllegalPanelId_FailsBeforeAnyStoreOrDesktopWrite(string panelId)
    {
        var store = new InMemoryPanelCanvasStore();
        var surface = new FakeSurface();

        var result = await Tool(store, surface).ExecuteAsync(
            Request($$"""{"panelId":"{{panelId}}","html":"<p>x</p>"}"""));

        Assert.False(result.Success);
        Assert.Equal(0, store.List().Count);
        Assert.Null(surface.LastRequest);
    }

    [Fact]
    public async Task NoDesktop_StillStoresContentAndReportsStored()
    {
        var store = new InMemoryPanelCanvasStore();
        var surface = new FakeSurface();

        var result = await Tool(store, surface, desktopAvailable: false)
            .ExecuteAsync(Request("""{"html":"<p>x</p>"}"""));

        // 通道没连上**不是失败**：内容已落地，只是还没显示。
        Assert.True(result.Success, result.Error);
        Assert.Equal("<p>x</p>", store.Get("main")!.Content);

        Assert.Contains("no Desktop is connected", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(RenderPanelToolStatus.StoredAndShown, result.Output, StringComparison.Ordinal);
        // 一条命令都没发出去。
        Assert.Null(surface.LastRequest);
    }

    [Fact]
    public async Task CapabilityFailure_StillStoresContentAndExplainsWhy()
    {
        var store = new InMemoryPanelCanvasStore();
        var surface = new FakeSurface
        {
            Result = CapabilityResult<DesktopPanelRenderResult>.Failure(
                DesktopCapabilityError.UiUnavailable("panel render is not wired into the tool area yet")),
        };

        var result = await Tool(store, surface).ExecuteAsync(Request("""{"html":"<p>x</p>"}"""));

        // 桌面拒绝渲染也要保留内容：那是「没显示」，不是「没写进去」。
        Assert.True(result.Success, result.Error);
        Assert.Equal("<p>x</p>", store.Get("main")!.Content);

        // 但原因必须如实透出，不许含糊成成功。
        Assert.Contains("ui_unavailable", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RenderPanelToolStatus.StoredAndShown, result.Output, StringComparison.Ordinal);
    }

    private sealed class FakeSurface : IDesktopPanelCapabilitySurface
    {
        public DesktopPanelRenderRequest? LastRequest { get; private set; }

        public CapabilityResult<DesktopPanelRenderResult> Result { get; set; } =
            CapabilityResult<DesktopPanelRenderResult>.Success(
                new DesktopPanelRenderResult("main", DesktopPanelRenderStatus.Rendered));

        public Task<CapabilityResult<DesktopPanelRenderResult>> RenderPanelAsync(
            DesktopPanelRenderRequest request,
            DesktopCallContext call,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCallContexts(bool available = true) : IDesktopCapabilityCallContextFactory
    {
        public DesktopCallContext? TryCreate(TimeSpan? timeout = null) =>
            available ? NewCall(timeout) : null;

        public DesktopCallContext? TryCreate(string? permissionEvidenceSummary, TimeSpan? timeout = null) =>
            available ? NewCall(timeout) : null;

        private static DesktopCallContext NewCall(TimeSpan? timeout) => new(
            new DesktopInstanceId("desk-1"),
            new OperationId($"op-{Guid.NewGuid():N}"),
            DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30)));
    }
}
