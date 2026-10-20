using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.AgentTools;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentToolsTests;

/// <summary>
/// render_panel 工具的行为边界：默认画布、契约形状校验、结果不含内容、
/// 未连接 Desktop 与能力失败都必须如实报错。
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

    [Fact]
    public async Task DefaultsToTheMainCanvasAndReturnsIdentityOnly()
    {
        var surface = new FakeSurface();
        var tool = new RenderPanelTool(surface, new FakeCallContexts());

        var result = await tool.ExecuteAsync(Request("""{"html":"<p>hi</p>"}"""));

        Assert.True(result.Success, result.Error);
        // 与 Shell 侧「交互面板」单例 tab 对齐：不传 panelId 时落到同一个画布。
        Assert.Equal("main", surface.LastRequest!.PanelId);
        Assert.Equal("<p>hi</p>", surface.LastRequest.Content);
        Assert.Equal("html", surface.LastRequest.ContentType);

        // 结果只带身份与阶段：内容不回显（可能很长，也可能含用户数据）。
        Assert.DoesNotContain("<p>hi</p>", result.Output, StringComparison.Ordinal);
        Assert.Contains("main", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitPanelIdAndTitleArePassedThrough()
    {
        var surface = new FakeSurface();
        var tool = new RenderPanelTool(surface, new FakeCallContexts());

        var result = await tool.ExecuteAsync(
            Request("""{"panelId":"design.v2","html":"<svg/>","title":"车身结构"}"""));

        Assert.True(result.Success, result.Error);
        Assert.Equal("design.v2", surface.LastRequest!.PanelId);
        Assert.Equal("车身结构", surface.LastRequest.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyHtml_FailsBeforeAnyDesktopCall(string html)
    {
        var surface = new FakeSurface();
        var tool = new RenderPanelTool(surface, new FakeCallContexts());

        var result = await tool.ExecuteAsync(Request($$"""{"html":"{{html}}"}"""));

        Assert.False(result.Success);
        // fail closed 在发命令之前：一条命令都不许发出去。
        Assert.Null(surface.LastRequest);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("中文")]
    public async Task IllegalPanelId_FailsBeforeAnyDesktopCall(string panelId)
    {
        var surface = new FakeSurface();
        var tool = new RenderPanelTool(surface, new FakeCallContexts());

        var result = await tool.ExecuteAsync(
            Request($$"""{"panelId":"{{panelId}}","html":"<p>x</p>"}"""));

        Assert.False(result.Success);
        Assert.Null(surface.LastRequest);
    }

    [Fact]
    public async Task NoDesktop_FailsWithExplicitCode()
    {
        var tool = new RenderPanelTool(new FakeSurface(), new FakeCallContexts(available: false));

        var result = await tool.ExecuteAsync(Request("""{"html":"<p>x</p>"}"""));

        Assert.False(result.Success);
        Assert.Contains("desktop_not_connected", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityFailure_IsReportedAsFailure()
    {
        var surface = new FakeSurface
        {
            Result = CapabilityResult<DesktopPanelRenderResult>.Failure(
                DesktopCapabilityError.UiUnavailable("panel render is not wired into the tool area yet")),
        };
        var tool = new RenderPanelTool(surface, new FakeCallContexts());

        var result = await tool.ExecuteAsync(Request("""{"html":"<p>x</p>"}"""));

        Assert.False(result.Success);
        Assert.Contains("ui_unavailable", result.Error!, StringComparison.OrdinalIgnoreCase);
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
